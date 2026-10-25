using System.Diagnostics;
using Microsoft.Win32;
using OneArr.Updates;

namespace OneArr.Tray;

/// <summary>
/// Installs from an extracted release folder ("OneArr.Tray.exe" beside an "app" folder) and removes the installation.
/// Config, database and backups are only removed by an explicit --purge, never by install or update.
/// </summary>
internal static class Installer
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "OneArr";

    public static bool IsInstalled(InstallLayout layout) =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(Path.Combine(layout.Root, "OneArr.Tray.exe")), StringComparison.OrdinalIgnoreCase);

    public static void InstallFromFolder(string releaseFolder, InstallLayout layout)
    {
        var sourceApp = Path.Combine(releaseFolder, "app");
        if (!File.Exists(Path.Combine(sourceApp, "OneArr.Api.exe")))
            throw new InvalidOperationException("Run the installer from the extracted OneArr folder (it must contain an app folder).");

        Directory.CreateDirectory(layout.Root);
        // Replace only the app folder, the same way an update does. Config and backups are left alone.
        var staging = layout.App + ".staging";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        CopyDirectory(sourceApp, staging);
        if (Directory.Exists(layout.AppPrevious)) Directory.Delete(layout.AppPrevious, recursive: true);
        if (Directory.Exists(layout.App)) Directory.Move(layout.App, layout.AppPrevious);
        Directory.Move(staging, layout.App);

        File.Copy(Environment.ProcessPath!, Path.Combine(layout.Root, "OneArr.Tray.exe"), overwrite: true);
        SetAutostart(layout, true);
        CreateStartMenuShortcut(layout);
    }

    public static bool AutostartEnabled => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(RunValue) is not null;

    public static void SetAutostart(InstallLayout layout, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{Path.Combine(layout.Root, "OneArr.Tray.exe")}\" --background");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    private static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "OneArr.lnk");

    private static void CreateStartMenuShortcut(InstallLayout layout)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(ShortcutPath);
        link.TargetPath = Path.Combine(layout.Root, "OneArr.Tray.exe");
        link.WorkingDirectory = layout.Root;
        link.Description = "OneArr";
        link.Save();
    }

    /// <summary>
    /// Stops OneArr and removes the program files. Only processes whose executable is this installation's OneArr.Api.exe
    /// are ended. Config and backups stay unless purge is set.
    /// </summary>
    public static void Uninstall(InstallLayout layout, bool purge)
    {
        var requestStop = layout.StopRequest;
        Directory.CreateDirectory(layout.Config);
        File.WriteAllText(requestStop, DateTime.UtcNow.ToString("O"));
        Thread.Sleep(3000);
        foreach (var p in Process.GetProcessesByName("OneArr.Api"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, layout.ApiExe, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        SetAutostart(layout, false);
        if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);

        // Program files and caches. Config (database, secret key), backups and the old source clone are handled explicitly below.
        foreach (var dir in new[] { layout.App, layout.AppPrevious, layout.App + ".failed", layout.App + ".rolledback", layout.Downloads, layout.Logs, Path.Combine(layout.Root, "repo") })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        if (purge && Directory.Exists(layout.Config)) Directory.Delete(layout.Config, recursive: true);
        if (purge && Directory.Exists(layout.Backups)) Directory.Delete(layout.Backups, recursive: true);

        // A running exe cannot delete itself; wait briefly after exit, then remove the folder or the exe.
        var remove = purge ? $"rd /s /q \"{layout.Root}\"" : $"del /q \"{Path.Combine(layout.Root, "OneArr.Tray.exe")}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & {remove}") { CreateNoWindow = true, UseShellExecute = false });
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }
}
