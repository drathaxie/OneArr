namespace OneArr.Updates;

/// <summary>
/// Where an installed OneArr lives. Only "app" is replaced by an update. "config" (database and secret key)
/// and "backups" are never removed by an update.
/// </summary>
public sealed record InstallLayout(string Root)
{
    public string App => Path.Combine(Root, "app");
    public string AppPrevious => Path.Combine(Root, "app.previous");
    public string Config => Path.Combine(Root, "config");
    public string Backups => Path.Combine(Root, "backups");
    public string Downloads => Path.Combine(Root, "downloads");
    public string Logs => Path.Combine(Root, "logs");
    public string ApiExe => Path.Combine(App, "OneArr.Api.exe");
    public string VersionFile => Path.Combine(App, "version.txt");
    public string Database => Path.Combine(Config, "onearr.db");
    public string StopRequest => Path.Combine(Config, "stop.request");
    public string LastBackupMarker => Path.Combine(Backups, "last-update.txt");
    public string LastDiscardedMarker => Path.Combine(Backups, "last-discarded.txt");

    public static InstallLayout Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneArr"));

    /// <summary>Installed version from app\version.txt, or null when nothing is installed.</summary>
    public Version? InstalledVersion()
    {
        if (!File.Exists(VersionFile)) return null;
        return ReleaseVersions.Parse(File.ReadAllText(VersionFile).Trim());
    }
}

public static class ReleaseVersions
{
    /// <summary>Accepts "0.2.0" and "v0.2.0". Returns null for anything else, so bad tags are never installed.</summary>
    public static Version? Parse(string text)
    {
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        // Exactly Major.Minor.Patch, all numeric, so tags like "1.2" or "1.2.3-beta" are never installed.
        var parts = s.Split('.');
        if (parts.Length != 3 || !parts.All(p => p.Length > 0 && p.All(char.IsDigit))) return null;
        return Version.TryParse(s, out var v) ? v : null;
    }
}
