using System.Diagnostics;
using OneArr.Updates;

namespace OneArr.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var layout = InstallLayout.Default();
        Log.Folder = layout.Logs;

        if (args.Length == 2 && args[0] == "--command")
        {
            try
            {
                TrayCommands.Send(layout, args[1]);
                Console.WriteLine($"Sent '{args[1]}' to the running tray. It acts within a few seconds.");
                return 0;
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
        }

        if (args.Contains("--status"))
        {
            var statusPath = Path.Combine(layout.Root, "tray-status.json");
            Console.WriteLine(File.Exists(statusPath) ? File.ReadAllText(statusPath) : "{\"state\": \"not installed or never run\"}");
            return 0;
        }

        if (args.Contains("--uninstall"))
        {
            Installer.Uninstall(layout, purge: args.Contains("--purge"));
            return 0;
        }

        // Not yet installed: install from the extracted folder beside this exe, then start the installed copy.
        if (!Installer.IsInstalled(layout))
        {
            try
            {
                Installer.InstallFromFolder(AppContext.BaseDirectory, layout);
            }
            catch (IOException)
            {
                MessageBox.Show("OneArr is already running. Quit it from the tray icon, then run this installer again.", "OneArr",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(ex.Message, "OneArr", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            Process.Start(new ProcessStartInfo(Path.Combine(layout.Root, "OneArr.Tray.exe")) { UseShellExecute = true, Arguments = "--background" });
            return 0;
        }

        using var single = new Mutex(initiallyOwned: true, "Local\\OneArrTray", out bool created);
        if (!created) return 0;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(layout, backgroundStart: args.Contains("--background")));
        return 0;
    }
}
