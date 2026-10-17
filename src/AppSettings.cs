using System.Text.Json;

namespace ScopeCAM;

/// <summary>
/// User defaults saved via the "Save defaults" button and applied on every
/// connect. Stored in %APPDATA%\ScopeCAM\settings.json.
/// </summary>
public class AppSettings
{
    public int ResolutionIndex { get; set; } = -1; // -1 = camera default
    public bool AutoExposure { get; set; } = true;
    public int AeTarget { get; set; } = 120;
    public uint ExposureUs { get; set; } = 20_000;
    public int GainPercent { get; set; } = 100;
    public int Sharpen { get; set; } = 150;
    public int FlickerIndex { get; set; } = 2; // 0 = 50 Hz, 1 = 60 Hz, 2 = DC
    public bool FlipH { get; set; }
    public bool FlipV { get; set; }
    public bool Crosshair { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ScopeCAM", "settings.json");

    public static AppSettings? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))
                : null;
        }
        catch
        {
            return null; // corrupt settings fall back to built-in defaults
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
