using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VirtualSoundField.Audio;

public sealed record OutputRequest(string DeviceId, float Volume01, int DelayMs = 0);

/// <summary>Result of trying to attach one output device.</summary>
public sealed record OutputStatus(string DeviceId, string Detail, bool Ok);

/// <summary>
/// 音频路由引擎：WASAPI Loopback 捕获 → ModeProcessor 分流 → 多个 OutputPipeline 输出。
///
/// 4种模式通过 ModeProcessor 在捕获回调中实时分流：
///   LRSplit     (2路) — 左/右声道分离
///   FrontRear   (2路) — 前置直通 / 后置 L-R 差分
///   QuadSurround(4路) — 虚拟环绕矩阵
///   FivePointOne(6路) — 5.1声道拆分
///
/// 每个通道独立绑定一个 OutputPipeline（含设备、音量、延迟），
/// 未绑定设备的通道自动输出静音，不影响其他通道。
/// </summary>
public sealed class MirrorEngine : IDisposable
{
    private readonly object sync = new();
    private MMDevice? captureDevice;
    private WasapiLoopbackCapture? capture;
    private volatile bool stopping;
    private MMDeviceEnumerator? sysEnumerator;
    private MMDevice? sysDevice;
    private IDisposable? mmcssHandle; // MMCSS 多媒体调度句柄
    private ModeProcessor? modeProcessor; // 预分配缓冲区的模式处理器

    // === 模式与通道管理 ===
    private RoutingMode mode = RoutingMode.LRSplit;

    /// <summary>按通道索引存储 pipeline。未绑定设备的通道为 null。</summary>
    private OutputPipeline?[] channelPipelines = Array.Empty<OutputPipeline?>();

    /// <summary>按通道索引存储设备请求信息（用于重建）。</summary>
    private OutputRequest?[] channelRequests = Array.Empty<OutputRequest?>();

    public bool IsRunning { get; private set; }
    public string? CaptureDeviceId { get; private set; }
    public string? CaptureDeviceName { get; private set; }
    public WaveFormat? CaptureFormat { get; private set; }
    public int BufferTargetMs { get; private set; } = 40;
    public int SkipThresholdMs { get; private set; } = 70;
    public RoutingMode Mode => mode;

    public int ChannelCount => ModeProcessor.GetChannelCount(mode);
    public string GetChannelName(int idx) => ModeProcessor.GetChannelName(mode, idx);

    /// <summary>An output died while mirroring.</summary>
    public event Action<int, Exception?>? ChannelFailed;
    /// <summary>Capture stopped unexpectedly.</summary>
    public event Action<Exception?>? CaptureStopped;

    // ========================================================================
    //  启动 / 停止
    // ========================================================================

    /// <param name="captureDeviceId">要捕获的设备ID，null=系统默认。</param>
    /// <param name="bufferMs">每个设备的缓冲目标（毫秒）。</param>
    /// <param name="routingMode">音频路由模式。</param>
    public List<OutputStatus> Start(string? captureDeviceId, int bufferMs, RoutingMode routingMode)
    {
        lock (sync)
        {
            var statuses = new List<OutputStatus>();
            if (IsRunning) return statuses;

            mode = routingMode;
            int chCount = ModeProcessor.GetChannelCount(mode);
            channelPipelines = new OutputPipeline?[chCount];
            channelRequests = new OutputRequest?[chCount];

            BufferTargetMs = Math.Clamp(bufferMs, OutputPipeline.MinBufferMs, OutputPipeline.MaxBufferMs);
            using var enumerator = new MMDeviceEnumerator();
            captureDevice = ResolveCaptureDevice(enumerator, captureDeviceId);
            CaptureDeviceId = captureDevice.ID;
            CaptureDeviceName = captureDevice.FriendlyName;
            capture = new WasapiLoopbackCapture(captureDevice);
            CaptureFormat = capture.WaveFormat;

            stopping = false;

            // 创建预分配缓冲区的模式处理器（消除热路径GC压力）
            modeProcessor?.Dispose();
            modeProcessor = new ModeProcessor();

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();

            // 注册 MMCSS 多媒体调度：给捕获线程更高的 CPU 优先级和更低的调度延迟
            mmcssHandle = Program.EnterMultimediaClass();

            IsRunning = true;
            return statuses;
        }
    }

    public void Stop()
    {
        lock (sync)
        {
            if (!IsRunning) return;
            stopping = true;
            IsRunning = false;

            // 释放 MMCSS 调度句柄
            try { mmcssHandle?.Dispose(); } catch { }
            mmcssHandle = null;

            if (capture != null)
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                try { capture.StopRecording(); } catch { }
            }
            CleanupCapture();

            for (int i = 0; i < channelPipelines.Length; i++)
            {
                try { channelPipelines[i]?.Dispose(); } catch { }
                channelPipelines[i] = null;
            }
            for (int i = 0; i < channelRequests.Length; i++)
                channelRequests[i] = null;
        }
    }

    // ========================================================================
    //  通道管理：绑定/解绑设备到通道
    // ========================================================================

    /// <summary>将设备绑定到指定通道。返回该通道的状态。</summary>
    public OutputStatus AssignChannel(int channelIndex, OutputRequest request)
    {
        lock (sync)
        {
            if (channelIndex < 0 || channelIndex >= ChannelCount)
                return new OutputStatus(request.DeviceId, "无效通道", false);

            // 解绑旧设备
            UnassignChannel(channelIndex);

            // 检查是否是捕获源
            if (request.DeviceId == CaptureDeviceId)
                return new OutputStatus(request.DeviceId, "跳过 — 捕获源", false);

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(request.DeviceId);
                if (device.State != DeviceState.Active)
                {
                    device.Dispose();
                    return new OutputStatus(request.DeviceId, "未连接", false);
                }

                var pipeline = new OutputPipeline(device, capture!.WaveFormat,
                    request.Volume01, request.DelayMs, BufferTargetMs, SkipThresholdMs);
                pipeline.Stopped += OnPipelineStopped;

                channelPipelines[channelIndex] = pipeline;
                channelRequests[channelIndex] = request;

                return new OutputStatus(request.DeviceId,
                    $"{pipeline.DeviceSampleRate / 1000.0:0.#} kHz", true);
            }
            catch (Exception ex)
            {
                return new OutputStatus(request.DeviceId, "失败: " + ex.Message, false);
            }
        }
    }

    /// <summary>解绑指定通道的设备。</summary>
    public void UnassignChannel(int channelIndex)
    {
        OutputPipeline? removed = null;
        lock (sync)
        {
            if (channelIndex < 0 || channelIndex >= channelPipelines.Length) return;
            removed = channelPipelines[channelIndex];
            channelPipelines[channelIndex] = null;
            if (channelIndex < channelRequests.Length)
                channelRequests[channelIndex] = null;
        }
        removed?.Dispose();
    }

    /// <summary>更新指定通道的软件音量。</summary>
    public void SetVolume(int channelIndex, float volume01)
    {
        if (channelIndex >= 0 && channelIndex < channelPipelines.Length)
            channelPipelines[channelIndex]?.SetVolume(volume01);
    }

    /// <summary>更新指定通道的延迟。</summary>
    public void SetDelay(int channelIndex, int delayMs)
    {
        if (channelIndex >= 0 && channelIndex < channelPipelines.Length)
            channelPipelines[channelIndex]?.SetExtraDelay(delayMs);
    }

    /// <summary>获取指定通道的pipeline信息。</summary>
    public bool TryGetChannelInfo(int channelIndex, out OutputPipeline pipeline)
    {
        if (channelIndex >= 0 && channelIndex < channelPipelines.Length && channelPipelines[channelIndex] != null)
        {
            pipeline = channelPipelines[channelIndex]!;
            return true;
        }
        pipeline = null!;
        return false;
    }

    /// <summary>获取指定通道绑定的设备ID。</summary>
    public string? GetChannelDeviceId(int channelIndex)
    {
        if (channelIndex >= 0 && channelIndex < channelPipelines.Length)
            return channelPipelines[channelIndex]?.DeviceId;
        return null;
    }

    // ========================================================================
    //  系统音量控制
    // ========================================================================

    public bool SetSystemVolume(float volume01)
    {
        try
        {
            sysEnumerator ??= new MMDeviceEnumerator();
            if (sysDevice == null || sysDevice.State != DeviceState.Active)
            {
                sysDevice?.Dispose();
                sysDevice = sysEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            sysDevice.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume01, 0f, 1f);
            return true;
        }
        catch { return false; }
    }

    public float GetSystemVolume()
    {
        try
        {
            sysEnumerator ??= new MMDeviceEnumerator();
            if (sysDevice == null || sysDevice.State != DeviceState.Active)
            {
                sysDevice?.Dispose();
                sysDevice = sysEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            return sysDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
        }
        catch { return 1.0f; }
    }

    public bool SetSystemMute(bool mute)
    {
        try
        {
            sysEnumerator ??= new MMDeviceEnumerator();
            if (sysDevice == null || sysDevice.State != DeviceState.Active)
            {
                sysDevice?.Dispose();
                sysDevice = sysEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            sysDevice.AudioEndpointVolume.Mute = mute;
            return true;
        }
        catch { return false; }
    }

    public void SetBufferTarget(int bufferMs)
    {
        BufferTargetMs = Math.Clamp(bufferMs, OutputPipeline.MinBufferMs, OutputPipeline.MaxBufferMs);
        foreach (var p in channelPipelines)
            p?.SetBufferTarget(BufferTargetMs);
    }

    public void SetSkipThreshold(int ms)
    {
        SkipThresholdMs = Math.Clamp(ms, 10, 500);
        foreach (var p in channelPipelines)
            p?.SetSkipThreshold(SkipThresholdMs);
    }

    // ========================================================================
    //  设备枚举（静态工具方法）
    // ========================================================================

    public static List<(string Id, string Name)> ListRenderDevices()
    {
        var result = new List<(string, string)>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            using (device)
                result.Add((device.ID, device.FriendlyName));
        return result;
    }

    public static string? GetDefaultRenderId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        Stop();
        try { modeProcessor?.Dispose(); } catch { }
        modeProcessor = null;
        if (sysDevice != null) { try { sysDevice.Dispose(); } catch { } sysDevice = null; }
        if (sysEnumerator != null) { try { sysEnumerator.Dispose(); } catch { } sysEnumerator = null; }
    }

    // ========================================================================
    //  核心：捕获回调 → ModeProcessor 分流 → 分发到各通道
    // ========================================================================

    private bool captureThreadInitialized;

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (capture == null) return;

        // 首次回调时提升捕获线程优先级，减少调度延迟
        if (!captureThreadInitialized)
        {
            captureThreadInitialized = true;
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                Thread.CurrentThread.IsBackground = true;
            }
            catch { }
        }

        try
        {
            var snapshot = channelPipelines;

            if (e.BytesRecorded == 0)
            {
                for (int i = 0; i < snapshot.Length; i++)
                    snapshot[i]?.Write(Array.Empty<byte>(), 0);
                return;
            }

            // 使用预分配缓冲区的模式处理器（零内存分配）
            var processor = modeProcessor;
            if (processor == null) return;

            byte[][] channelData = processor.Process(
                e.Buffer, e.BytesRecorded, capture!.WaveFormat, mode,
                out int channelCount, out int[] outputLengths);

            for (int i = 0; i < channelCount && i < snapshot.Length; i++)
            {
                var pipeline = snapshot[i];
                if (pipeline != null)
                    pipeline.Write(channelData[i], outputLengths[i]);
            }
        }
        catch
        {
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (!stopping)
            CaptureStopped?.Invoke(e.Exception);
    }

    private void OnPipelineStopped(OutputPipeline pipeline, Exception? exception)
    {
        int channelIndex = -1;
        lock (sync)
        {
            for (int i = 0; i < channelPipelines.Length; i++)
            {
                if (channelPipelines[i] == pipeline)
                {
                    channelIndex = i;
                    channelPipelines[i] = null;
                    channelRequests[i] = null;
                    break;
                }
            }
        }
        pipeline.Dispose();
        if (channelIndex >= 0 && !stopping)
            ChannelFailed?.Invoke(channelIndex, exception);
    }

    private static MMDevice ResolveCaptureDevice(MMDeviceEnumerator enumerator, string? requestedId)
    {
        if (!string.IsNullOrEmpty(requestedId))
        {
            try
            {
                var device = enumerator.GetDevice(requestedId);
                if (device.State == DeviceState.Active) return device;
                device.Dispose();
            }
            catch { }
        }
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private void CleanupCapture()
    {
        try { capture?.Dispose(); } catch { }
        capture = null;
        try { captureDevice?.Dispose(); } catch { }
        captureDevice = null;
        CaptureFormat = null;
        CaptureDeviceId = null;
        CaptureDeviceName = null;
    }
}
