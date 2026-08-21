using System.Text.Json;
using VirtualSoundField.Audio;

namespace VirtualSoundField.Settings;

public sealed class SavedDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Volume { get; set; } = 100;
    public int DelayMs { get; set; } = 0;
    public int HardwareVolume { get; set; } = -1;
    public int ChannelIndex { get; set; } = -1;
}

/// <summary>Persisted device list + volumes + mode, so the family setup survives restarts.</summary>
public sealed class AppSettings
{
    public List<SavedDevice> Devices { get; set; } = new();
    public string? CaptureDeviceId { get; set; }
    public int BufferMs { get; set; } = 40;
    public int SkipThresholdMs { get; set; } = 70;
    public RoutingMode Mode { get; set; } = RoutingMode.LRSplit;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EarShare", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
