using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OneArr.Infrastructure.Services;

/// <summary>
/// Single bounded loop: queue sync every minute, completed-download import planning (dry run) every fifth minute, wanted search every 15th.
/// Each job goes through JobRunner's per-job lock, so scheduled and manual runs cannot overlap. Automatic grabbing is intentionally absent,
/// and the scheduled import step is read-only by construction: it never performs a physical import.
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

    /// <summary>One scheduled pass: queue sync every tick, wanted search every 15th tick (only
    /// when automatic grabbing is on), and import planning every 5th tick. The import step is
    /// ALWAYS a dry run: the scheduler may preview or inspect only, never perform physical
    /// writes. Physical imports happen only through the confirmed preview/apply workflow
    /// (per-job import-apply or bulk import with confirmed pairs) — never via the DryRunImport
    /// setting, so flipping that setting cannot silently enable scheduled imports. Automatic
    /// grabbing being off is not what prevents this; the hardcoded dry run is.</summary>
    public async Task RunPassAsync(int tick, CancellationToken ct)
    {
        await _runner.TryRunAsync("queue-sync", async t => $"Updated {await _downloads.SyncQueueAsync(t)} job(s)", ct);

        // Wanted search runs every 15 minutes, and only when automatic grabbing is switched on.
        if (tick % 15 == 0 && await _settings.GetBoolAsync(OneArr.Domain.SettingKeys.AutomaticGrab, false, ct))
            await _runner.TryRunAsync("wanted-search", async t => (await _search.RunOnceAsync(t)).ToString(), ct);

        if (tick % 5 == 0)
            await _runner.TryRunAsync("import", async t => (await _downloads.ImportCompletedAsync(true, t)).Message, ct);
    }
}
