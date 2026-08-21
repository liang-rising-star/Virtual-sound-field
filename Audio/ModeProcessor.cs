using System.Runtime.InteropServices;

namespace VirtualSoundField.Audio;

/// <summary>
/// 音频模式分流处理器（实例化，持有预分配缓冲区）
///
/// 预分配所有缓冲区并固定在内存中，消除热路径的内存分配和GC压力。
/// 每次 Process 调用只写入已有缓冲区，不分配新内存。
/// </summary>
public sealed class ModeProcessor : IDisposable
{
    private const int MaxFrames = 4800; // 100ms @ 48kHz（单次回调最大帧数）
    private const int MaxChannels = 6;

    // 预分配并固定的缓冲区（永不释放，永不被GC移动）
    private float[] _inputBuffer;
    private float[][] _outputBuffers;  // [MaxChannels][MaxFrames * 2]
    private byte[][] _resultBuffers;   // [MaxChannels][]
    private GCHandle _inputHandle;
    private GCHandle[] _outputHandles;
    private bool _disposed;

    public ModeProcessor()
    {
        // 预分配输入缓冲区
        _inputBuffer = new float[MaxFrames * MaxChannels];
        _inputHandle = GCHandle.Alloc(_inputBuffer, GCHandleType.Pinned);

        // 预分配输出缓冲区（最多6通道，每通道立体声）
        _outputBuffers = new float[MaxChannels][];
        _outputHandles = new GCHandle[MaxChannels];
        for (int i = 0; i < MaxChannels; i++)
        {
            _outputBuffers[i] = new float[MaxFrames * 2];
            _outputHandles[i] = GCHandle.Alloc(_outputBuffers[i], GCHandleType.Pinned);
        }

        // 预分配结果字节缓冲区
        _resultBuffers = new byte[MaxChannels][];
        for (int i = 0; i < MaxChannels; i++)
            _resultBuffers[i] = new byte[MaxFrames * 2 * 4]; // stereo float
    }

    /// <summary>每种模式的输出通道数。</summary>
    public static int GetChannelCount(RoutingMode mode) => mode switch
    {
        RoutingMode.LRSplit => 2,
        RoutingMode.FrontRear => 2,
        RoutingMode.QuadSurround => 4,
        RoutingMode.FivePointOne => 6,
        _ => 2
    };

    /// <summary>每个通道的名称。</summary>
    public static string GetChannelName(RoutingMode mode, int channelIndex) => (mode, channelIndex) switch
    {
        (RoutingMode.LRSplit, 0) => "左声道",
        (RoutingMode.LRSplit, 1) => "右声道",
        (RoutingMode.FrontRear, 0) => "前置立体声",
        (RoutingMode.FrontRear, 1) => "后置立体声",
        (RoutingMode.QuadSurround, 0) => "前左 (FL)",
        (RoutingMode.QuadSurround, 1) => "前右 (FR)",
        (RoutingMode.QuadSurround, 2) => "后左 (RL)",
        (RoutingMode.QuadSurround, 3) => "后右 (RR)",
        (RoutingMode.FivePointOne, 0) => "前左 (FL)",
        (RoutingMode.FivePointOne, 1) => "前右 (FR)",
        (RoutingMode.FivePointOne, 2) => "中置 (C)",
        (RoutingMode.FivePointOne, 3) => "低音 (LFE)",
        (RoutingMode.FivePointOne, 4) => "环绕左 (SL)",
        (RoutingMode.FivePointOne, 5) => "环绕右 (SR)",
        _ => $"通道 {channelIndex}"
    };

    /// <summary>
    /// 处理捕获数据，返回每路输出的字节数组。
    /// 注意：返回的数组是预分配的，下次调用会被覆盖！
    /// 调用方必须在下次调用前完成数据消费。
    /// </summary>
    /// <param name="channelCount">输出通道数（由模式决定）</param>
    /// <param name="outputLengths">每路输出的有效字节数</param>
    public byte[][] Process(byte[] captureData, int byteCount,
        NAudio.Wave.WaveFormat captureFormat, RoutingMode mode,
        out int channelCount, out int[] outputLengths)
    {
        int srcChannels = captureFormat.Channels;
        int bytesPerSample = captureFormat.BitsPerSample / 8;
        int totalSamples = byteCount / bytesPerSample;

        // 字节 → float（写入预分配缓冲区）
        ConvertBytesToFloat(captureData, byteCount, captureFormat, _inputBuffer);
        int floatFrames = totalSamples / srcChannels;
        if (floatFrames > MaxFrames) floatFrames = MaxFrames;

        // 按模式处理（写入预分配的输出缓冲区）
        channelCount = GetChannelCount(mode);
        switch (mode)
        {
            case RoutingMode.LRSplit:
                ProcessLRSplit(floatFrames, srcChannels);
                break;
            case RoutingMode.FrontRear:
                ProcessFrontRear(floatFrames, srcChannels);
                break;
            case RoutingMode.QuadSurround:
                ProcessQuadSurround(floatFrames, srcChannels);
                break;
            case RoutingMode.FivePointOne:
                ProcessFivePointOne(floatFrames, srcChannels);
                break;
            default:
                ProcessLRSplit(floatFrames, srcChannels);
                break;
        }

        // float → 字节（写入预分配的结果缓冲区）
        outputLengths = new int[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            int stereoFrames = floatFrames;
            int byteLen = stereoFrames * 2 * 4; // stereo * float32
            if (byteLen > _resultBuffers[i].Length) byteLen = _resultBuffers[i].Length;
            Buffer.BlockCopy(_outputBuffers[i], 0, _resultBuffers[i], 0, byteLen);
            outputLengths[i] = byteLen;
        }

        return _resultBuffers;
    }

    private void ProcessLRSplit(int frameCount, int srcChannels)
    {
        float[] left = _outputBuffers[0];
        float[] right = _outputBuffers[1];

        for (int i = 0; i < frameCount; i++)
        {
            float L = srcChannels >= 1 ? _inputBuffer[i * srcChannels] : 0;
            float R = srcChannels >= 2 ? _inputBuffer[i * srcChannels + 1] : L;
            left[i * 2] = L; left[i * 2 + 1] = L;
            right[i * 2] = R; right[i * 2 + 1] = R;
        }
    }

    private void ProcessFrontRear(int frameCount, int srcChannels)
    {
        float[] front = _outputBuffers[0];
        float[] rear = _outputBuffers[1];

        for (int i = 0; i < frameCount; i++)
        {
            float L = srcChannels >= 1 ? _inputBuffer[i * srcChannels] : 0;
            float R = srcChannels >= 2 ? _inputBuffer[i * srcChannels + 1] : L;
            front[i * 2] = L; front[i * 2 + 1] = R;
            rear[i * 2] = (L - R) * 0.7f;
            rear[i * 2 + 1] = (R - L) * 0.7f;
        }
    }

    private void ProcessQuadSurround(int frameCount, int srcChannels)
    {
        float[] fl = _outputBuffers[0];
        float[] fr = _outputBuffers[1];
        float[] rl = _outputBuffers[2];
        float[] rr = _outputBuffers[3];

        for (int i = 0; i < frameCount; i++)
        {
            float L = srcChannels >= 1 ? _inputBuffer[i * srcChannels] : 0;
            float R = srcChannels >= 2 ? _inputBuffer[i * srcChannels + 1] : L;
            float center = (L + R) * 0.5f;
            float side = (L - R) * 0.5f;

            float flS = L * 0.7f + center * 0.21f;
            float frS = R * 0.7f + center * 0.21f;
            float rlS = side * 0.3f + center * 0.12f;
            float rrS = -side * 0.3f + center * 0.12f;

            fl[i * 2] = flS; fl[i * 2 + 1] = flS;
            fr[i * 2] = frS; fr[i * 2 + 1] = frS;
            rl[i * 2] = rlS; rl[i * 2 + 1] = rlS;
            rr[i * 2] = rrS; rr[i * 2 + 1] = rrS;
        }
    }

    private void ProcessFivePointOne(int frameCount, int srcChannels)
    {
        float[] fl = _outputBuffers[0];
        float[] fr = _outputBuffers[1];
        float[] c = _outputBuffers[2];
        float[] lfe = _outputBuffers[3];
        float[] sl = _outputBuffers[4];
        float[] sr = _outputBuffers[5];

        for (int i = 0; i < frameCount; i++)
        {
            if (srcChannels >= 6)
            {
                fl[i * 2] = _inputBuffer[i * 6 + 0]; fl[i * 2 + 1] = fl[i * 2];
                fr[i * 2] = _inputBuffer[i * 6 + 1]; fr[i * 2 + 1] = fr[i * 2];
                c[i * 2] = _inputBuffer[i * 6 + 2]; c[i * 2 + 1] = c[i * 2];
                lfe[i * 2] = _inputBuffer[i * 6 + 3]; lfe[i * 2 + 1] = lfe[i * 2];
                sl[i * 2] = _inputBuffer[i * 6 + 4]; sl[i * 2 + 1] = sl[i * 2];
                sr[i * 2] = _inputBuffer[i * 6 + 5]; sr[i * 2 + 1] = sr[i * 2];
            }
            else
            {
                float L = srcChannels >= 1 ? _inputBuffer[i * srcChannels] : 0;
                float R = srcChannels >= 2 ? _inputBuffer[i * srcChannels + 1] : L;
                float center = (L + R) * 0.5f;

                fl[i * 2] = L + center * 0.3f; fl[i * 2 + 1] = fl[i * 2];
                fr[i * 2] = R + center * 0.3f; fr[i * 2 + 1] = fr[i * 2];
                c[i * 2] = center; c[i * 2 + 1] = center;
                lfe[i * 2] = center * 0.8f; lfe[i * 2 + 1] = lfe[i * 2];
                sl[i * 2] = (L - R) * 0.4f; sl[i * 2 + 1] = sl[i * 2];
                sr[i * 2] = (R - L) * 0.4f; sr[i * 2 + 1] = sr[i * 2];
            }
        }
    }

    private static void ConvertBytesToFloat(byte[] buffer, int byteCount,
        NAudio.Wave.WaveFormat format, float[] output)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        int totalSamples = byteCount / bytesPerSample;
        for (int i = 0; i < totalSamples; i++)
        {
            int offset = i * bytesPerSample;
            if (offset + bytesPerSample > byteCount) break;
            switch (format.Encoding)
            {
                case NAudio.Wave.WaveFormatEncoding.IeeeFloat:
                    if (bytesPerSample == 4) output[i] = BitConverter.ToSingle(buffer, offset);
                    break;
                case NAudio.Wave.WaveFormatEncoding.Pcm:
                    if (bytesPerSample == 2) output[i] = BitConverter.ToInt16(buffer, offset) / 32768f;
                    else if (bytesPerSample == 3)
                    {
                        int val = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
                        output[i] = val / 8388608f;
                    }
                    else if (bytesPerSample == 4) output[i] = BitConverter.ToInt32(buffer, offset) / (float)int.MaxValue;
                    break;
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            try { _inputHandle.Free(); } catch { }
            for (int i = 0; i < _outputHandles.Length; i++)
                try { _outputHandles[i].Free(); } catch { }
        }
    }
}

public enum RoutingMode
{
    LRSplit = 0,
    FrontRear = 1,
    QuadSurround = 2,
    FivePointOne = 3
}
