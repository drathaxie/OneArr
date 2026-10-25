using System.Diagnostics;
using OneArr.Updates;

namespace OneArr.Tray;

/// <summary>
/// Notification-area icon. Shows status, opens the UI, checks for and (on request) installs releases, restarts or rolls
/// back OneArr, and quits gracefully. Runs without any window.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private const int Port = 8780;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private const int MaxRestartsPerFiveMinutes = 3;

    private readonly InstallLayout _layout;
    private readonly TraySettings _settings;
    private readonly ApiProcess _api;
    private readonly TrayUpdateService _update;
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 5000 };
    private readonly System.Windows.Forms.Timer _checkTimer = new() { Interval = (int)CheckInterval.TotalMilliseconds };
    private readonly System.Windows.Forms.Timer _commandTimer = new() { Interval = 2000 };
    private DateTime? _lastCheckUtc;
    private string? _lastResult;
    private string? _lastError;
    private readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _installItem = new() { Visible = false };
    private readonly ToolStripMenuItem _rollbackItem = new("Roll back to previous version") { Visible = false };
    private readonly ToolStripMenuItem _autostartItem = new("Start with Windows") { CheckOnClick = true };
    private readonly ToolStripMenuItem _checkAutoItem = new("Check for updates automatically") { CheckOnClick = true };
    private readonly ToolStripMenuItem _installAutoItem = new("Install updates automatically") { CheckOnClick = true };
    private readonly Queue<DateTime> _recentRestarts = new();
    private ReleaseInfo? _available;
    private bool _busy;
    private string _progress = "";

    public TrayApp(InstallLayout layout, bool backgroundStart)
    {
        _layout = layout;
        _settings = TraySettings.Load(Path.Combine(layout.Root, "tray.json"));
        _api = new ApiProcess(layout, Port);
        _update = new TrayUpdateService(layout, _api, new ReleaseClient(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, _settings.UpdateApi));
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        _icon = new NotifyIcon { Icon = SystemIcons.Application, Text = "OneArr", Visible = true, ContextMenuStrip = BuildMenu() };
        _icon.DoubleClick += (_, _) => OpenUi();

        _api.UnexpectedExit += code => _ui.Post(_ => OnUnexpectedExit(code), null);
        TrayCommands.Take(layout); // discard a command left before this tray started
        _commandTimer.Tick += (_, _) => { var command = TrayCommands.Take(_layout); if (command is not null) _ = RunCommandAsync(command); };
        _commandTimer.Start();
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _checkTimer.Tick += async (_, _) => await CheckAsync(notifyIfCurrent: false);
        _statusTimer.Start();
        _checkTimer.Start();

        _ = StartupAsync(backgroundStart);
    }

    private ContextMenuStrip BuildMenu()
    {
        _autostartItem.Checked = Installer.AutostartEnabled;
        _autostartItem.CheckedChanged += (_, _) => Installer.SetAutostart(_layout, _autostartItem.Checked);
        _checkAutoItem.Checked = _settings.CheckForUpdates;
        _checkAutoItem.CheckedChanged += (_, _) => SaveSettings(s => s.CheckForUpdates = _checkAutoItem.Checked);
        _installAutoItem.Checked = _settings.InstallUpdatesAutomatically;
        _installAutoItem.CheckedChanged += (_, _) => SaveSettings(s => s.InstallUpdatesAutomatically = _installAutoItem.Checked);

        _installItem.Click += async (_, _) => { if (_available is not null) await InstallAsync(_available); };
        _rollbackItem.Click += async (_, _) => await RollBackAsync();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open OneArr", null, (_, _) => OpenUi());
        menu.Items.Add("Restart OneArr", null, async (_, _) => await RestartAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_installItem);
        menu.Items.Add("Check for updates now", null, async (_, _) => await CheckAsync(notifyIfCurrent: true));
        menu.Items.Add(_rollbackItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autostartItem);
        menu.Items.Add(_checkAutoItem);
        menu.Items.Add(_installAutoItem);
        menu.Items.Add("Open logs folder", null, (_, _) => { Directory.CreateDirectory(_layout.Logs); Process.Start("explorer.exe", _layout.Logs); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        return menu;
    }

    private async Task StartupAsync(bool backgroundStart)
    {
        if (!File.Exists(_layout.ApiExe))
        {
            Notify("OneArr is not installed correctly. Reinstall from the release folder.", ToolTipIcon.Error);
            return;
        }
        // Adopt a backend that is already running (for example after a tray restart) rather than starting a second one.
        await _api.RefreshHealthAsync();
        if (!_api.IsRunning)
        {
            try { _api.Start(); }
            catch (Exception ex) { Log.Write("tray.log", ex.Message); Notify("OneArr could not start. See the logs folder.", ToolTipIcon.Error); }
        }
        WriteStatus("started");
        await Task.Delay(TimeSpan.FromMinutes(2));
        if (_settings.CheckForUpdates) await CheckAsync(notifyIfCurrent: false);
    }

    private async Task RunCommandAsync(string command)
    {
        Log.Write("tray.log", $"Command received: {command}");
        switch (command)
        {
            case "check": await CheckAsync(notifyIfCurrent: true); break;
            case "install":
                await CheckAsync(notifyIfCurrent: false);
                if (_available is not null) await InstallAsync(_available);
                break;
            case "rollback": await RollBackAsync(confirm: false); break;
            case "quit": Quit(); break;
        }
    }

    /// <summary>Writes tray-status.json. Holds versions and times only, never secrets.</summary>
    private void WriteStatus(string state)
    {
        try
        {
            TrayStatus.Write(_layout, new TrayStatus(
                State: state,
                InstalledVersion: _layout.InstalledVersion()?.ToString(3),
                LatestVersion: _available?.Version.ToString(3),
                LastCheckUtc: _lastCheckUtc,
                LastResult: _lastResult,
                LastError: _lastError,
                PreviousVersionAvailable: _update.HasPreviousVersion));
        }
        catch (IOException ex)
        {
            Log.Write("tray.log", $"Could not write status: {ex.Message}");
        }
    }

    private async Task CheckAsync(bool notifyIfCurrent)
    {
        if (_busy) return;
        _busy = true;
        SetProgress("Checking for updates…");
        try
        {
            var release = await _update.CheckAsync(CancellationToken.None);
            _lastCheckUtc = DateTime.UtcNow;
            _lastError = null;
            _lastResult = release is null ? "up to date" : $"{release.Version.ToString(3)} available";
            if (release is null)
            {
                _available = null;
                _installItem.Visible = false;
                if (notifyIfCurrent) Notify("OneArr is up to date.", ToolTipIcon.Info);
                return;
            }
            _available = release;
            _installItem.Text = $"Install OneArr {release.Version.ToString(3)}";
            _installItem.Visible = true;
            _busy = false;
            if (_settings.InstallUpdatesAutomatically) await InstallAsync(release);
            else Notify($"OneArr {release.Version.ToString(3)} is available. Use \"Install\" in the tray menu.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Write("tray.log", $"Update check failed: {ex.Message}");
            _lastCheckUtc = DateTime.UtcNow;
            _lastError = ex.Message;
            if (notifyIfCurrent) Notify($"Could not check for updates: {ex.Message}", ToolTipIcon.Warning);
        }
        finally
        {
            _busy = false;
            SetProgress("");
            WriteStatus("idle");
        }
    }

    private async Task InstallAsync(ReleaseInfo release)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var outcome = await _update.InstallAsync(release, s => SetProgress(s), CancellationToken.None);
            _available = null;
            _installItem.Visible = false;
            _lastError = null;
            _lastResult = outcome == UpdateOutcome.Updated ? $"updated to {release.Version.ToString(3)}" : $"{release.Version.ToString(3)} rolled back (failed startup)";
            Notify(outcome == UpdateOutcome.Updated
                ? $"Updated to OneArr {release.Version.ToString(3)}."
                : $"OneArr {release.Version.ToString(3)} failed its startup check; the previous version was restored.",
                outcome == UpdateOutcome.Updated ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            Log.Write("tray.log", $"Install failed: {ex.Message}");
            _lastError = ex.Message;
            _lastResult = "update not applied";
            Notify($"Update not applied: {ex.Message}", ToolTipIcon.Error);
        }
        finally
        {
            _busy = false;
            SetProgress("");
            RefreshRollbackItem();
            WriteStatus("idle");
        }
    }

    private async Task RollBackAsync(bool confirm = true)
    {
        if (_busy || !_update.HasPreviousVersion) return;
        if (confirm && MessageBox.Show("Go back to the previous OneArr version? The database is restored from the backup taken before the last update. Anything written since then is kept in backups\\discarded-*.db, not deleted.",
                "OneArr", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _busy = true;
        try
        {
            await _update.RollBackAsync(CancellationToken.None);
            _lastError = null;
            _lastResult = "rolled back to previous version";
            Notify("Rolled back to the previous version.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Write("tray.log", $"Rollback failed: {ex.Message}");
            _lastError = ex.Message;
            Notify($"Rollback failed: {ex.Message}", ToolTipIcon.Error);
        }
        finally
        {
            _busy = false;
            RefreshRollbackItem();
            WriteStatus("idle");
        }
    }

    private async Task RestartAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!await _api.StopGracefullyAsync(TimeSpan.FromSeconds(20), CancellationToken.None))
            {
                Notify("OneArr is still running and was not stopped, so it was not restarted.", ToolTipIcon.Warning);
                return;
            }
            _api.Start();
        }
        catch (Exception ex)
        {
            Log.Write("tray.log", $"Restart failed: {ex.Message}");
            Notify($"Restart failed: {ex.Message}", ToolTipIcon.Error);
        }
        finally
        {
            _busy = false;
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
            return;
        }
        _recentRestarts.Enqueue(now);

        var retry = new System.Windows.Forms.Timer { Interval = 3000 };
        retry.Tick += (_, _) =>
        {
            retry.Stop();
            retry.Dispose();
            try { _api.Start(); }
            catch (Exception ex) { Log.Write("tray.log", ex.Message); }
        };
        retry.Start();
    }

    private async Task RefreshStatusAsync()
    {
        await _api.RefreshHealthAsync();
        _ui.Post(_ => UpdateStatusText(), null);
    }

    private void UpdateStatusText()
    {
        var state = _busy ? (_progress.Length > 0 ? _progress : "Working…") : _api.IsRunning ? "Running" : "Stopped";
        _statusItem.Text = $"OneArr: {state}";
        _icon.Text = $"OneArr: {state}";
        if (_icon.Text.Length > 63) _icon.Text = _icon.Text[..63];
        RefreshRollbackItem();
    }

    private void RefreshRollbackItem() => _rollbackItem.Visible = _update.HasPreviousVersion;

    private void SetProgress(string text)
    {
        _progress = text;
        _ui.Post(_ => UpdateStatusText(), null);
    }

    private void SaveSettings(Action<TraySettings> change)
    {
        change(_settings);
        _settings.Save(Path.Combine(_layout.Root, "tray.json"));
    }

    private void Notify(string text, ToolTipIcon icon) =>
        _ui.Post(_ => _icon.ShowBalloonTip(6000, "OneArr", text, icon), null);

    private void OpenUi() =>
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{Port}") { UseShellExecute = true });

    private void Quit()
    {
        WriteStatus("stopped");
        _statusTimer.Stop();
        _checkTimer.Stop();
        _commandTimer.Stop();
        _api.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        ExitThread();
    }
}
