using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OneArr.Infrastructure.Services;

/// <summary>
/// Single bounded loop: queue sync every minute, completed-download imports every fifth minute,
/// wanted search every fifteenth minute. Physical imports are strictly opt-in and fail closed
/// while the separate dry-run setting is enabled. Import uses the stored preview/revalidation
/// engine and never deletes or moves qBittorrent source files.
/// </summary>
public sealed class BackgroundScheduler : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    private readonly JobRunner _runner;
    private readonly DownloadService _downloads;
    private readonly SettingsStore _settings;
    private readonly WantedSearchService _search;
    private readonly ILogger<BackgroundScheduler> _log;

    public BackgroundScheduler(JobRunner runner, DownloadService downloads, SettingsStore settings, WantedSearchService search, ILogger<BackgroundScheduler> log)
    {
        _runner = runner;
        _downloads = downloads;
        _settings = settings;
        _search = search;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting and migrations settle before the first pass.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        using var timer = new PeriodicTimer(Tick);
        var tick = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(tick, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning("Scheduler pass failed: {Message}", ex.Message);
            }
            tick++;
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }

    /// <summary>Queue state is refreshed before import decisions. Import is
    /// read-only unless both AutomaticImport is true and DryRunImport is false.
    /// Manual approvals always remain explicit and independent.</summary>
    public async Task RunPassAsync(int tick, CancellationToken ct)
    {
        await _runner.TryRunAsync("queue-sync", async t => $"Updated {await _downloads.SyncQueueAsync(t)} job(s)", ct);

        // Wanted search runs every 15 minutes, and only when automatic grabbing is switched on.
        if (tick % 15 == 0 && await _settings.GetBoolAsync(OneArr.Domain.SettingKeys.AutomaticGrab, false, ct))
            await _runner.TryRunAsync("wanted-search", async t => (await _search.RunOnceAsync(t)).ToString(), ct);

        if (tick % 5 == 0)
        {
            var autoImport = await _settings.GetBoolAsync(OneArr.Domain.SettingKeys.AutomaticImport, false, ct);
            var dryRun = await _settings.GetBoolAsync(OneArr.Domain.SettingKeys.DryRunImport, true, ct);
            await _runner.TryRunAsync("import",
                async t => autoImport && !dryRun
                    ? (await _downloads.AutoImportCompletedAsync(t)).Message
                    : (await _downloads.ImportCompletedAsync(true, t)).Message, ct);
        }
    }
}
