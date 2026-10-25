using OneArr.Updates;

namespace OneArr.Tray;

/// <summary>
/// Commands for a running tray without its menu: `OneArr.Tray.exe --command check|install|rollback|quit`.
/// The command is a file in the install folder that the running tray polls and then deletes. A command file left
/// over from before the tray started is discarded, so nothing runs unexpectedly at login.
/// </summary>
internal static class TrayCommands
{
    public static readonly string[] Known = ["check", "install", "rollback", "quit"];

    public static string File(InstallLayout layout) => Path.Combine(layout.Root, "tray-command.txt");

    public static void Send(InstallLayout layout, string command)
    {
        if (!Known.Contains(command)) throw new ArgumentException($"Unknown command '{command}'. Use one of: {string.Join(", ", Known)}");
        Directory.CreateDirectory(layout.Root);
        System.IO.File.WriteAllText(File(layout), command);
    }

    /// <summary>Returns the pending command and removes the file, or null if there is none.</summary>
    public static string? Take(InstallLayout layout)
    {
        var path = File(layout);
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            var command = System.IO.File.ReadAllText(path).Trim();
            System.IO.File.Delete(path);
            return Known.Contains(command) ? command : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
