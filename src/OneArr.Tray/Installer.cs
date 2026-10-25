using System.Diagnostics;
using Microsoft.Win32;

namespace OneArr.Tray;

/// <summary>Self-install: copies this exe into %LOCALAPPDATA%\OneArr, adds a Start Menu entry and an optional Run key.</summary>
internal static class Installer
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "OneArr";

    public static bool IsInstalled =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(Paths.InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static void Install()
    {
        Directory.CreateDirectory(Paths.Root);
        File.Copy(Environment.ProcessPath!, Paths.InstalledExe, overwrite: true);
        SetAutostart(true);
        CreateStartMenuShortcut();
    }

    public static bool AutostartEnabled => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(RunValue) is not null;

    public static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{Paths.InstalledExe}\"");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    private static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "OneArr.lnk");

    private static void CreateStartMenuShortcut()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(ShortcutPath);
        link.TargetPath = Paths.InstalledExe;
        link.WorkingDirectory = Paths.Root;
        link.Description = "OneArr media manager";
        link.Save();
    }

    /// <summary>Removes the app, its build and logs. Config (database and secret key) is kept unless purge is set.</summary>
    public static void Uninstall(bool purge)
    {
        foreach (var p in Process.GetProcessesByName("OneArr.Api")) KillQuietly(p);
        foreach (var p in Process.GetProcessesByName("OneArr.Tray").Where(p => p.Id != Environment.ProcessId)) KillQuietly(p);

        SetAutostart(false);
        if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
        foreach (var dir in new[] { Paths.App, Paths.App + ".previous", Paths.Repo, Paths.Logs })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        if (purge && Directory.Exists(Paths.Config)) Directory.Delete(Paths.Config, recursive: true);

        // A running exe cannot delete itself; wait briefly after exit, then remove it.
        var remove = purge ? $"rd /s /q \"{Paths.Root}\"" : $"del /q \"{Paths.InstalledExe}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & {remove}") { CreateNoWindow = true, UseShellExecute = false });
    }

    private static void KillQuietly(Process p)
    {
        try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
