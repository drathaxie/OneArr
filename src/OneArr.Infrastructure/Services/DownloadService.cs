using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Files;
using OneArr.Infrastructure.Persistence;
using OneArr.Infrastructure.Providers;

namespace OneArr.Infrastructure.Services;

public sealed record ImportRunSummary(int FilesConsidered, int Imported, int Failed, int DryRun, string Message);

/// <summary>Syncs qBittorrent state into job records and imports completed downloads through SafeImporter.</summary>
public sealed class DownloadService
{
    private readonly IDbContextFactory<OneArrDbContext> _db;
    private readonly IDownloadClient _client;
    private readonly IImporter _importer;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;

    public DownloadService(IDbContextFactory<OneArrDbContext> db, IDownloadClient client, IImporter importer, SettingsStore settings, TimeProvider time)
    {
        _db = db;
        _client = client;
        _importer = importer;
        _settings = settings;
        _time = time;
    }

    public async Task<int> SyncQueueAsync(CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var clients = await db.DownloadClients.AsNoTracking().Where(c => c.Enabled).ToListAsync(ct);
        var active = await db.DownloadJobs.Where(j => j.Status != DownloadStatus.Imported && j.Status != DownloadStatus.Failed).ToListAsync(ct);
        var updated = 0;
        foreach (var client in clients)
        {
            var items = await _client.ListAsync(client, client.MovieCategory, ct);
            items = items.Concat(await _client.ListAsync(client, client.TvCategory, ct)).ToList();
            foreach (var job in active.Where(j => j.Category.Length > 0))
            {
                var match = job.Hash is { } h
                    ? items.FirstOrDefault(i => i.Hash == h)
                    : items.FirstOrDefault(i => i.Name == job.ReleaseTitle);
                if (match is null)
                {
                    if (job.Status is DownloadStatus.Queued or DownloadStatus.Downloading && job.UpdatedAt < _time.GetUtcNow().AddMinutes(-15))
                    {
                        job.Status = DownloadStatus.Missing;
                        job.UpdatedAt = _time.GetUtcNow();
                        updated++;
                    }
                    continue;
                }
                job.Hash ??= match.Hash;
                job.Status = QBittorrentClient.MapState(match.State, match.Progress);
                job.Progress = match.Progress;
                job.ClientState = match.State;
                job.ContentPath = match.ContentPath;
                job.UpdatedAt = _time.GetUtcNow();
                updated++;
            }
        }
        await db.SaveChangesAsync(ct);
        return updated;
    }

    public async Task<ImportRunSummary> ImportCompletedAsync(bool dryRun, CancellationToken ct)
    {
        var mediaRoot = await _settings.GetAsync(SettingKeys.MediaRoot, ct);
        var torrentRoot = await _settings.GetAsync(SettingKeys.TorrentRoot, ct);
        if (string.IsNullOrWhiteSpace(mediaRoot) || string.IsNullOrWhiteSpace(torrentRoot))
            return new ImportRunSummary(0, 0, 0, 0, "Set the media root and torrent root paths in Settings before importing");

        await using var db = await _db.CreateDbContextAsync(ct);
        var jobs = await db.DownloadJobs.Where(j => j.Status == DownloadStatus.Completed && j.ContentPath != null).ToListAsync(ct);
        int considered = 0, imported = 0, failed = 0, planned = 0;

        foreach (var job in jobs)
        {
            var media = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == job.MediaItemId, ct);
            if (media is null) continue;

            // The item's root folder must sit inside the configured media mount; otherwise fail closed.
            string destRoot;
            try
            {
                destRoot = SafeImporter.CanonicalUnder(mediaRoot, media.RootFolder);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                failed++;
                if (!dryRun)
                {
                    var failedJob = await db.DownloadJobs.FirstAsync(j => j.Id == job.Id, ct);
                    failedJob.Status = DownloadStatus.Failed;
                    failedJob.Error = "Root folder is outside the configured media root";
                    failedJob.UpdatedAt = _time.GetUtcNow();
                    await db.SaveChangesAsync(ct);
                }
                continue;
            }

            var relFolder = RelativeFolder(media, job, db);
            var files = ListFiles(job.ContentPath!, torrentRoot);
            var jobFailed = false;
            var jobDeferred = false;

            foreach (var file in files)
            {
                considered++;
                var outcome = await _importer.ImportAsync(new ImportRequest(file, destRoot, relFolder, torrentRoot, dryRun, job.Id), ct);
                var exists = await db.ImportRecords.AnyAsync(r => r.SourcePath == file && r.DestinationPath == outcome.DestinationPath && r.Action == outcome.Action && r.Success == outcome.Success && r.DownloadJobId == job.Id, ct);
                if (!exists || !dryRun)
                {
                    db.ImportRecords.Add(new ImportRecord
                    {
                        DownloadJobId = job.Id,
                        SourcePath = file,
                        DestinationPath = outcome.DestinationPath,
                        Action = outcome.Action,
                        Success = outcome.Success,
                        Message = outcome.Message,
                        CreatedAt = _time.GetUtcNow(),
                    });
                }
                if (outcome.Action == ImportAction.Deferred)
                {
                    // Still changing on disk: leave the job Completed so the next pass retries it.
                    jobDeferred = true;
                    continue;
                }
                if (!outcome.Success) jobFailed = true;
                if (outcome.Action == ImportAction.DryRun) planned++;
                if (outcome.Success && outcome.Action is ImportAction.Hardlink or ImportAction.Copy or ImportAction.Skipped) imported++;
                if (!outcome.Success) failed++;
            }

            if (!dryRun && files.Count > 0 && !jobDeferred)
            {
                var tracked = await db.DownloadJobs.FirstAsync(j => j.Id == job.Id, ct);
                tracked.Status = jobFailed ? DownloadStatus.Failed : DownloadStatus.Imported;
                tracked.Error = jobFailed ? "One or more files failed to import; see import records" : null;
                tracked.UpdatedAt = _time.GetUtcNow();
                if (!jobFailed) await MarkHaveFileAsync(db, job, ct);
            }
            await db.SaveChangesAsync(ct);
        }

        var msg = dryRun ? $"Dry run: {planned} file(s) planned, {failed} problem(s)" : $"Imported {imported} file(s), {failed} failed";
        return new ImportRunSummary(considered, imported, failed, planned, msg);
    }

    private async Task MarkHaveFileAsync(OneArrDbContext db, DownloadJob job, CancellationToken ct)
    {
        if (job.EpisodeId is int eid)
        {
            var ep = await db.Episodes.FirstOrDefaultAsync(e => e.Id == eid, ct);
            if (ep is not null) ep.HasFile = true;
        }
        else if (job.MediaItemId is int mid)
        {
            var item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == mid, ct);
            if (item is not null) item.HasFile = true;
        }
    }

    private static string RelativeFolder(MediaItem media, DownloadJob job, OneArrDbContext db)
    {
        var title = MediaService.SafeFolderName(media.Title);
        if (media.MediaType == MediaType.Movie)
            return media.Year is int y ? $"{title} ({y})" : title;

        var season = job.EpisodeId is int eid
            ? db.Episodes.AsNoTracking().Where(e => e.Id == eid).Select(e => e.SeasonNumber).FirstOrDefault()
            : new ReleaseParser().Parse(job.ReleaseTitle).Season ?? 1;
        return Path.Combine(title, $"Season {season:00}");
    }

    private static List<string> ListFiles(string contentPath, string torrentRoot)
    {
        var full = Path.GetFullPath(contentPath);
        if (File.Exists(full)) return SafeImporter.IsMediaFile(full) ? [full] : [];
        if (!Directory.Exists(full)) return [];
        return Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(SafeImporter.IsMediaFile)
            .Where(f => f.StartsWith(Path.GetFullPath(torrentRoot), StringComparison.Ordinal))
            .ToList();
    }
}
