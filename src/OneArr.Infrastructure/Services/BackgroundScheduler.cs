using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OneArr.Infrastructure.Services;

/// <summary>
/// Single bounded loop: queue sync every minute, completed-download import every fifth minute, wanted search every 15th. Each job goes through
/// JobRunner's per-job lock, so scheduled and manual runs cannot overlap. Automatic grabbing is intentionally absent.
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
                await _runner.TryRunAsync("queue-sync", async t => $"Updated {await _downloads.SyncQueueAsync(t)} job(s)", stoppingToken);

                // Wanted search runs every 15 minutes, and only when automatic grabbing is switched on.
                if (tick % 15 == 0 && await _settings.GetBoolAsync(OneArr.Domain.SettingKeys.AutomaticGrab, false, stoppingToken))
                    await _runner.TryRunAsync("wanted-search", async t => (await _search.RunOnceAsync(t)).ToString(), stoppingToken);

                if (tick % 5 == 0)
                {
                    var dry = await _settings.GetBoolAsync(OneArr.Domain.SettingKeys.DryRunImport, true, stoppingToken);
                    await _runner.TryRunAsync("import", async t => (await _downloads.ImportCompletedAsync(dry, t)).Message, stoppingToken);
                }
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
}
