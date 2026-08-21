using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VirtualSoundField.Audio;

/// <summary>
/// One fully independent playback chain per output device:
///
///   capture bytes -> BufferedWaveProvider -> (channel router) -> volume
///                 -> drift-correcting resampler -> WasapiOut (shared, event-driven)
///
/// Each device has its own buffer and its own WASAPI playback thread, so a slow
/// or stalling device can never block the capture thread or the other outputs —
/// the capture callback only copies bytes into each buffer and returns.
///
/// The buffer is held at (BaseTargetMs + extra delay) by the drift controller.
/// The optional per-device extra delay lets fast receivers (wired / 2.4 GHz)
/// be lined up with slow ones (classic Bluetooth like AirPods).
/// </summary>
public sealed class OutputPipeline : IDisposable
{
    // Steady-state queue per device: absorbs scheduling jitter between the capture
    // clock and each output's callback timing, and gives the drift controller room
    // to work. User-adjustable; lower = less latency, higher = more stutter-proof.
    // 20 ms is aggressive (capture arrives in 10 ms bursts, so fill swings ±10 ms);
    // expect occasional crackle under load there.
    public const int MinBufferMs = 20;
    public const int MaxBufferMs = 500;
    public const int MaxExtraDelayMs = 1000;

    private readonly MMDevice device;
    private readonly WaveFormat captureFormat;
    private readonly WaveFormat internalFormat;
    private readonly BufferedWaveProvider buffer;
    private readonly VolumeSampleProvider volume;
    private readonly DriftCorrectingResampler resampler;
    private readonly WasapiOut output;
    private volatile int baseTargetMs;
    private volatile int extraDelayMs;
    private volatile bool disposed;
    private long lastWriteTick;
    private long lastRealAudioWriteTick;
    private readonly double initialFillMs;
    private volatile int skipThresholdMs;

    public string DeviceId { get; }
    public string FriendlyName { get; }
    public int DeviceSampleRate { get; }
    public double BufferedMs => buffer.BufferedDuration.TotalMilliseconds;

    /// <summary>Raised when playback stops without Dispose being called (device lost, BT dropout...).</summary>
    public event Action<OutputPipeline, Exception?>? Stopped;

    private double TargetFillMs => baseTargetMs + extraDelayMs;

    public OutputPipeline(MMDevice device, WaveFormat captureFormat, float volume01, int delayMs, int bufferMs, int skipThresholdMs = 70)
    {
        this.device = device;
        this.captureFormat = captureFormat;
        baseTargetMs = Math.Clamp(bufferMs, MinBufferMs, MaxBufferMs);
        extraDelayMs = Math.Clamp(delayMs, 0, MaxExtraDelayMs);
        this.skipThresholdMs = skipThresholdMs;
        DeviceId = device.ID;
        FriendlyName = device.FriendlyName;

        var mixFormat = device.AudioClient.MixFormat;
        DeviceSampleRate = mixFormat.SampleRate;
        int deviceChannels = mixFormat.Channels;

        // 固定使用立体声 32bit float 格式作为管道内部格式
        // ModeProcessor 输出也是这个格式，确保字节完全匹配
        internalFormat = WaveFormat.CreateIeeeFloatWaveFormat(captureFormat.SampleRate, 2);

        buffer = new BufferedWaveProvider(internalFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };

        ISampleProvider chain = buffer.ToSampleProvider();
        if (2 != deviceChannels)
            chain = new ChannelRouterProvider(chain, deviceChannels);

        volume = new VolumeSampleProvider(chain) { Volume = volume01 };

        initialFillMs = TargetFillMs + skipThresholdMs / 2.0;

        resampler = new DriftCorrectingResampler(volume, DeviceSampleRate,
            () => buffer.BufferedDuration.TotalMilliseconds, initialFillMs);

        AddSilence(initialFillMs);
        lastWriteTick = Environment.TickCount64;
        lastRealAudioWriteTick = Environment.TickCount64;

        output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 100);
        output.Init(new SampleToWaveProvider(resampler));
        output.PlaybackStopped += (_, e) =>
        {
            if (!disposed)
                Stopped?.Invoke(this, e.Exception);
        };
        output.Play();
    }

    /// <summary>Called from the capture thread. Copies data and returns immediately.</summary>
    public void Write(byte[] data, int count)
    {
        if (disposed)
            return;
        try
        {
            long now = Environment.TickCount64;

            if (count > 0)
            {
                // 静音刚结束（>100ms 无真实音频）：一次性重填缓冲区到目标水位
                // 让漂移校正器从正确水位开始工作，避免20-30秒的追赶时间
                if (now - lastRealAudioWriteTick > 100)
                {
                    buffer.ClearBuffer();
                    AddSilence(TargetFillMs);
                    resampler.ResetTrim();
                }

                buffer.AddSamples(data, 0, count);
                lastRealAudioWriteTick = now;
            }
            // 缓冲区水位由 DriftCorrectingResampler 通过时间拉伸（±0.3%速率微调）自然维持
            lastWriteTick = now;
        }
        catch
        {
            // benign race with Dispose
        }
    }

    public void SetVolume(float volume01) => volume.Volume = volume01;

    /// <summary>
    /// Change the extra delay while playing: takes effect immediately by inserting
    /// silence (more delay) or skipping queued audio (less delay), then the drift
    /// controller holds the new fill level.
    /// </summary>
    public void SetExtraDelay(int delayMs)
    {
        delayMs = Math.Clamp(delayMs, 0, MaxExtraDelayMs);
        ShiftTarget(delayMs - extraDelayMs);
        extraDelayMs = delayMs;
    }

    /// <summary>Change the base buffer size while playing, same instant-effect mechanism as the delay.</summary>
    public void SetBufferTarget(int bufferMs)
    {
        bufferMs = Math.Clamp(bufferMs, MinBufferMs, MaxBufferMs);
        ShiftTarget(bufferMs - baseTargetMs);
        baseTargetMs = bufferMs;
    }

    /// <summary>Change the skip threshold while playing.</summary>
    public void SetSkipThreshold(int ms) => skipThresholdMs = Math.Clamp(ms, 10, 500);

    private void ShiftTarget(int deltaMs)
    {
        if (deltaMs == 0 || disposed)
            return;
        resampler.TargetBufferMs = initialFillMs + deltaMs;
        if (deltaMs > 0)
            AddSilence(deltaMs);
        else
            Skip(-deltaMs);
    }

    private void AddSilence(double ms)
    {
        int bytes = MsToAlignedBytes(ms);
        if (bytes > 0)
            buffer.AddSamples(new byte[bytes], 0, bytes);
    }

    private void Skip(double ms)
    {
        int bytes = Math.Min(MsToAlignedBytes(ms), buffer.BufferedBytes);
        bytes -= bytes % captureFormat.BlockAlign;
        if (bytes > 0)
            buffer.Read(new byte[bytes], 0, bytes);
    }

    private int MsToAlignedBytes(double ms)
    {
        int bytes = (int)(internalFormat.AverageBytesPerSecond * ms / 1000.0);
        return bytes - bytes % internalFormat.BlockAlign;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try { output.Stop(); } catch { }
        try { output.Dispose(); } catch { }
        try { device.Dispose(); } catch { }
    }
}
