using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using VirtualSoundField.UI;

namespace VirtualSoundField;

internal static class Program
{
    [DllImport("avrt.dll")]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, out int taskIndex);

    [DllImport("avrt.dll")]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    /// <summary>
    /// 将当前线程注册为多媒体类调度（MMCSS），Windows 会给予更高的 CPU 时间片和更低的调度延迟。
    /// 即使没有管理员权限也能生效。
    /// </summary>
    public static IDisposable EnterMultimediaClass()
    {
        try
        {
            var handle = AvSetMmThreadCharacteristics("Audio", out _);
            if (handle != IntPtr.Zero)
                return new MmcsCleanup(handle);
        }
        catch { }
        return NullDisposable.Instance;
    }

    private sealed class MmcsCleanup : IDisposable
    {
        private IntPtr _handle;
        public MmcsCleanup(IntPtr handle) => _handle = handle;
        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                AvRevertMmThreadCharacteristics(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }

    [STAThread]
    private static void Main()
    {
        // Two instances would fight over the same output devices.
        using var instanceMutex = new Mutex(initiallyOwned: true, "VirtualSoundField_SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "Virtual sound field 已在运行 — 请在系统托盘中查找其图标。",
                "Virtual sound field", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 提升进程优先级：High 给予比普通程序更高的 CPU 调度权重
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; }
        catch { /* 需要管理员权限才可设置 RealTime，High 通常无需提权 */ }

        // GC 优化：SustainedLowLatency 模式减少长时间 GC 暂停，避免音频断流
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        // 预分配 GC 固定大小堆，减少运行时 GC 触发
        GC.Collect(2, GCCollectionMode.Forced, false, true);

        // PerMonitorV2: render sharply at the actual monitor DPI and re-lay-out when
        // the window moves to a screen with different scaling (e.g. docking the Surface).
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());

        GC.KeepAlive(instanceMutex);
    }
}
