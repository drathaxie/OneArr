using Microsoft.Extensions.Hosting;

namespace OneArr.Api;

/// <summary>
/// Graceful stop without a window or a token. Whoever wants OneArr to stop (the tray app, an update, an
/// operator) creates config\stop.request. This loop notices it, removes it and stops the host, so the database
/// connections and background jobs shut down normally. Force termination is only a fallback for the caller.
/// </summary>
public sealed class StopRequestWatcher : BackgroundService
{
    private readonly string _stopFile;
    private readonly IHostApplicationLifetime _lifetime;

    public StopRequestWatcher(string configDir, IHostApplicationLifetime lifetime)
    {
        _stopFile = Path.Combine(configDir, "stop.request");
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!File.Exists(_stopFile)) continue;
            try { File.Delete(_stopFile); } catch (IOException) { }
            _lifetime.StopApplication();
            return;
        }
    }
}
