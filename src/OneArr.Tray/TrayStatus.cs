using OneArr.Updates;

using System.Text.Json;

namespace OneArr.Tray;

/// <summary>
/// What the tray last did, written to tray-status.json so scripts and support can read it without a window.
/// Overwritten on each change; holds no secrets.
/// </summary>
internal sealed record TrayStatus(
    string State,
    string? InstalledVersion,
    string? LatestVersion,
    DateTime? LastCheckUtc,
    string? LastResult,
    string? LastError,
    bool PreviousVersionAvailable)
{
    public static void Write(InstallLayout layout, TrayStatus status)
    {
        Directory.CreateDirectory(layout.Root);
        var path = Path.Combine(layout.Root, "tray-status.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }
}
