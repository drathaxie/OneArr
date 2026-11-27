using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OneArr.Domain;
using OneArr.Infrastructure.Diagnostics;
using OneArr.Infrastructure.Http;
using OneArr.Infrastructure.Library;
using OneArr.Infrastructure.Persistence;
using OneArr.Infrastructure.Security;
using OneArr.Infrastructure.Services;

namespace OneArr.Api;

/// <summary>
/// Settings > Logs &amp; diagnostics: recent log messages, the tail of the tray's log files, scheduled task results, and a
/// one-file diagnostics download for sharing. Nothing here returns an API key, password, token or full indexer URL.
/// </summary>
public static class DiagnosticsEndpoints
{
    private const int FileTailLines = 300;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/logs", (LogBuffer buffer, string? level, int? limit) =>
        {
            var min = Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsed) ? parsed : LogLevel.Information;
            return Results.Ok(buffer.Recent(Math.Clamp(limit ?? 500, 1, LogBuffer.Capacity), min));
        });

        api.MapGet("/logs/files", () => Results.Ok(new { folder = LogFolder(), files = LogFiles() }));

        api.MapGet("/diagnostics", async (LogBuffer buffer, SettingsStore settings, LibraryReconciler library,
            IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            var indexers = await c.Indexers.AsNoTracking().ToListAsync(ct);
            var indexerHealth = await c.IndexerHealth.AsNoTracking().ToListAsync(ct);
            var clients = await c.DownloadClients.AsNoTracking().ToListAsync(ct);
            var clientHealth = await c.DownloadClientHealth.AsNoTracking().ToListAsync(ct);
            var jobs = await c.DownloadJobs.AsNoTracking().ToListAsync(ct);
            var attempts = await c.SearchAttempts.AsNoTracking().ToListAsync(ct);
            var history = await c.JobHistories.AsNoTracking().OrderByDescending(h => h.Id).Take(300).ToListAsync(ct);
            var report = new
            {
                generatedUtc = DateTimeOffset.UtcNow,
                version = Program.BuildVersion,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                settings = new
                {
                    automaticGrab = await settings.GetBoolAsync(SettingKeys.AutomaticGrab, false, ct),
                    automaticImport = await settings.GetBoolAsync(SettingKeys.AutomaticImport, false, ct),
                    dryRunImport = await settings.GetBoolAsync(SettingKeys.DryRunImport, true, ct),
                    renameOnImport = await settings.GetBoolAsync(SettingKeys.RenameOnImport, false, ct),
                    torrentRoot = await settings.GetAsync(SettingKeys.TorrentRoot, ct),
                    mediaRoot = await settings.GetAsync(SettingKeys.MediaRoot, ct),
                    recycleBin = await settings.GetAsync(SettingKeys.RecycleBin, ct),
                    tmdbConfigured = !string.IsNullOrEmpty(await settings.GetAsync(SettingKeys.TmdbApiKey, ct)),
                },
                automation = await library.CheckAutomationAsync(ct),
                libraryRoots = await c.LibraryRoots.AsNoTracking().Select(r => new { r.MediaType, r.Path, r.Enabled }).ToListAsync(ct),
                catalog = new
                {
                    movies = await c.MediaItems.CountAsync(m => m.MediaType == MediaType.Movie, ct),
                    series = await c.MediaItems.CountAsync(m => m.MediaType == MediaType.Series, ct),
                    episodes = await c.Episodes.CountAsync(ct),
                    missingMonitoredEpisodes = await c.Episodes.CountAsync(e => e.Monitored && !e.HasFile, ct),
                },
                qualityProfiles = await c.QualityProfiles.AsNoTracking().ToListAsync(ct),
                indexers = indexers.Select(i => new
                {
                    i.Id, i.Name, host = Host(i.BaseUrl), kind = Kind(i), i.Enabled, i.MoviesEnabled, i.TvEnabled, i.Priority, i.RssSync,
                    health = indexerHealth.Where(h => h.IndexerId == i.Id).Select(h => new { h.LastStatus, h.LastTestedAt, lastError = Redact(h.LastError) }).FirstOrDefault(),
                }),
                downloadClients = clients.Select(d => new
                {
                    d.Id, d.Name, host = Host(d.BaseUrl), d.Enabled, d.MovieCategory, d.TvCategory,
                    health = clientHealth.Where(h => h.DownloadClientId == d.Id).Select(h => new { h.LastSeenAt, lastError = Redact(h.LastError), h.UpdatedAt }).FirstOrDefault(),
                }),
                queue = new
                {
                    byStatus = jobs.GroupBy(j => j.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()),
                    open = jobs.Where(j => j.Status != DownloadStatus.Imported).OrderByDescending(j => j.UpdatedAt).Take(100).Select(j => new
                    {
                        j.Id, j.ReleaseTitle, status = j.Status.ToString(), j.ClientState, j.Progress, error = Redact(j.Error),
                        j.ImportHoldReason, j.ImportCheckedAt, j.Blocklisted, j.CreatedAt, j.UpdatedAt, j.CompletedAt, j.LastSeenAt,
                    }),
                },
                recentImports = await c.ImportRecords.AsNoTracking().OrderByDescending(r => r.Id).Take(100)
                    .Select(r => new { r.DownloadJobId, r.SourcePath, r.DestinationPath, action = r.Action.ToString(), r.Success, r.Message, r.CreatedAt }).ToListAsync(ct),
                recentSearches = attempts.OrderByDescending(a => a.LastAttemptAt).Take(150)
                    .Select(a => new { a.ItemKey, outcome = a.LastOutcome.ToString(), message = Redact(a.LastMessage), a.LastAttemptAt, a.NextAttemptAt, a.ConsecutiveFailures }),
                blocklisted = await c.Blocklist.CountAsync(ct),
                scheduledTasks = history.Select(h => new { h.Name, h.StartedAt, h.FinishedAt, h.Success, message = Redact(h.Message) }),
                log = buffer.Recent(1000, LogLevel.Information),
                logFiles = LogFiles(),
            };
            return new AttachmentResult(Results.Json(report), $"onearr-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        });
    }

    private static string? Redact(string? s) => s is null ? null : OutboundHttp.Redact(s);
    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
    private static string Kind(IndexerConfig i) => TrackerSecretBox.IsNative(i.ApiKey) ? "native" :
        OneArr.Infrastructure.Providers.PublicRssClient.IsPublicRss(i.ApiKey) ? "rss" : "torznab";

    private static string? LogFolder()
    {
        var dir = Environment.GetEnvironmentVariable("ONEARR_LOG_DIR");
        return string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir) ? null : dir;
    }

    /// <summary>The last lines of each .log file the tray writes (api.log, tray.log, update logs), redacted.</summary>
    private static IReadOnlyList<object> LogFiles()
    {
        if (LogFolder() is not string dir) return [];
        var list = new List<object>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.log").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(10))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var lines = new Queue<string>();
                while (reader.ReadLine() is string line)
                {
                    lines.Enqueue(OutboundHttp.Redact(line));
                    if (lines.Count > FileTailLines) lines.Dequeue();
                }
                list.Add(new { name = Path.GetFileName(path), lines });
            }
            catch (IOException) { list.Add(new { name = Path.GetFileName(path), lines = new[] { "(could not be read)" } }); }
        }
        return list;
    }

    /// <summary>Serves the inner result as a download.</summary>
    private sealed class AttachmentResult(IResult inner, string fileName) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
            return inner.ExecuteAsync(httpContext);
        }
    }
}
