using System.Diagnostics;

namespace OneArr.Tray;

/// <summary>Notification-area icon: status, open UI, manual update, restart, autostart, logs and quit.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMinutes(60);
    private const int MaxRestartsPerFiveMinutes = 3;

    private readonly NotifyIcon _icon;
    private readonly ApiProcess _api = new();
    private readonly Updater _updater = new();
    private readonly SynchronizationContext _ui;
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 5000 };
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = (int)UpdateInterval.TotalMilliseconds };
    private readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _autostartItem = new("Start with Windows") { CheckOnClick = true };
    private readonly Queue<DateTime> _recentRestarts = new();
    private bool _updating;
    private string _progress = "";

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "OneArr",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _icon.DoubleClick += (_, _) => OpenUi();

        _api.UnexpectedExit += code => _ui.Post(_ => OnUnexpectedExit(code), null);
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _updateTimer.Tick += async (_, _) => await UpdateAsync(notifyIfCurrent: false);
        _statusTimer.Start();
        _updateTimer.Start();

        RefreshStatus();
        _ = StartupAsync();
    }

    private ContextMenuStrip BuildMenu()
    {
        _autostartItem.Checked = Installer.AutostartEnabled;
        _autostartItem.CheckedChanged += (_, _) => Installer.SetAutostart(_autostartItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open OneArr", null, (_, _) => OpenUi());
        menu.Items.Add("Check for updates now", null, async (_, _) => await UpdateAsync(notifyIfCurrent: true));
        menu.Items.Add("Restart OneArr", null, (_, _) => { _api.Stop(); _api.Start(); RefreshStatus(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autostartItem);
        menu.Items.Add("Open logs folder", null, (_, _) => { Directory.CreateDirectory(Paths.Logs); Process.Start("explorer.exe", Paths.Logs); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        return menu;
    }

    private async Task StartupAsync()
    {
        // Check the three tools the updater needs. A missing one is reported once and nothing else runs.
        foreach (var tool in new[] { "git", "dotnet", "npm" })
        {
            try
            {
                var (file, args) = tool == "npm" ? ("cmd.exe", new[] { "/c", "npm", "--version" }) : (tool, new[] { "--version" });
                await Shell.CaptureAsync(file, args, Paths.Root, CancellationToken.None);
            }
            catch (Exception)
            {
                Notify($"{tool} was not found. Install it, then restart OneArr.", ToolTipIcon.Error);
                return;
            }
        }

        // Start the last good build right away, then check GitHub for changes.
        if (File.Exists(Paths.ApiExe))
        {
            try { _api.Start(); } catch (Exception ex) { Log.Write("tray.log", ex.Message); }
        }
        await UpdateAsync(notifyIfCurrent: false);
    }

    private async Task UpdateAsync(bool notifyIfCurrent)
    {
        if (_updating) return;
        _updating = true;
        RefreshStatus();
        try
        {
            var changed = await _updater.UpdateAsync(_api, s => { _progress = s; RefreshStatus(); }, CancellationToken.None);
            if (changed) Notify("OneArr was updated and restarted.", ToolTipIcon.Info);
            else if (notifyIfCurrent) Notify("OneArr is up to date.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Write("tray.log", $"Update failed: {ex.Message}");
            Notify($"Update failed: {ex.Message}", ToolTipIcon.Error);
            if (!_api.IsRunning && File.Exists(Paths.ApiExe))
            {
                try { _api.Start(); } catch (Exception startEx) { Log.Write("tray.log", startEx.Message); }
            }
        }
        finally
        {
            _updating = false;
            _progress = "";
            RefreshStatus();
        }
    }

    private void OnUnexpectedExit(int code)
    {
        Log.Write("tray.log", $"OneArr stopped unexpectedly (exit code {code})");
        var now = DateTime.UtcNow;
        while (_recentRestarts.Count > 0 && now - _recentRestarts.Peek() > TimeSpan.FromMinutes(5)) _recentRestarts.Dequeue();
        if (_recentRestarts.Count >= MaxRestartsPerFiveMinutes)
        {
            Notify("OneArr keeps stopping. Check api.log in the logs folder.", ToolTipIcon.Error);
            RefreshStatus();
            return;
        }
        _recentRestarts.Enqueue(now);

        var retry = new System.Windows.Forms.Timer { Interval = 3000 };
        retry.Tick += (_, _) =>
        {
            retry.Stop();
            retry.Dispose();
            try { _api.Start(); } catch (Exception ex) { Log.Write("tray.log", ex.Message); }
            RefreshStatus();
        };
        retry.Start();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var state = _updating ? (_progress.Length > 0 ? _progress : "Updating…")
                  : _api.IsRunning ? "Running"
                  : "Stopped";
        _statusItem.Text = $"OneArr: {state}";
        _icon.Text = $"OneArr: {state}";  // NotifyIcon tooltips are limited to 63 characters
        if (_icon.Text.Length > 63) _icon.Text = _icon.Text[..63];
    }

    private void Notify(string text, ToolTipIcon icon) =>
        _icon.ShowBalloonTip(6000, "OneArr", text, icon);

    private static void OpenUi() =>
        Process.Start(new ProcessStartInfo(Paths.Url) { UseShellExecute = true });

    private void Quit()
    {
        _statusTimer.Stop();
        _updateTimer.Stop();
        _api.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        ExitThread();
    }
}
