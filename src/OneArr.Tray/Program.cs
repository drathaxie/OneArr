using System.Diagnostics;
namespace OneArr.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--uninstall"))
        {
            Installer.Uninstall(purge: args.Contains("--purge"));
            return;
        }

        // First run from anywhere other than the install folder: install, then launch the installed copy.
        if (!Installer.IsInstalled)
        {
            try
            {
                Installer.Install();
            }
            catch (IOException)
            {
                MessageBox.Show("OneArr is already running. Quit it from the tray icon, then run this installer again.", "OneArr",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Process.Start(new ProcessStartInfo(Paths.InstalledExe) { UseShellExecute = true });
            return;
        }

        using var single = new Mutex(initiallyOwned: true, "Local\\OneArrTray", out bool created);
        if (!created)
        {
            MessageBox.Show("OneArr is already running in the notification area.", "OneArr", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
