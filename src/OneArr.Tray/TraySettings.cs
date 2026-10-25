using System.Text.Json;

namespace OneArr.Tray;

/// <summary>Tray preferences in %LOCALAPPDATA%\OneArr\tray.json. Missing or unreadable means defaults.</summary>
internal sealed class TraySettings
{
    public bool CheckForUpdates { get; set; } = true;
    public bool InstallUpdatesAutomatically { get; set; } = false;
    /// <summary>Test hook only. Null means the official GitHub release feed.</summary>
    public string? UpdateApi { get; set; }

    public static TraySettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<TraySettings>(File.ReadAllText(path)) ?? new TraySettings() : new TraySettings();
        }
        catch (JsonException)
        {
            return new TraySettings();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this));
    }
}
