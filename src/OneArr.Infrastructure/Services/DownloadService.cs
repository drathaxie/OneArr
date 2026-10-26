using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Files;
using OneArr.Infrastructure.Library;
using OneArr.Infrastructure.Persistence;
using OneArr.Infrastructure.Providers;

namespace OneArr.Infrastructure.Services;

public sealed record ImportRunSummary(int FilesConsidered, int Imported, int Failed, int DryRun, string Message);

/// <summary>One file in an import preview. PlannedAction is hardlink, copy, skip, defer, or reject.
/// Nothing in a preview moves, links, copies, renames, or deletes anything.</summary>
public sealed record ImportPlanDto(
    string SourcePath,
    string? DestinationPath,
    long SizeBytes,
    long LastWriteUtcTicks,
    string MediaType,
    string PlannedAction,
    string Reason,
    bool DestinationConflict,
    bool SameVolume,
    List<int> MatchedEpisodeIds,
    List<string> MatchedEpisodeLabels);

public sealed record ImportPreviewDto(int PreviewId, int JobId, DateTimeOffset CreatedAt, List<ImportPlanDto> Plans, string Fingerprint);

/// <summary>Thrown when an import apply no longer matches its preview. The endpoint turns this into
/// a 409 carrying a fresh preview, so the user reviews the changed plan instead of applying stale one.</summary>
public sealed class StalePreviewException : Exception
{
    public StalePreviewException(ImportPreviewDto fresh) : base("The import plan changed since the preview; review the fresh preview before applying.") { Fresh = fresh; }
    public ImportPreviewDto Fresh { get; }
}

/// <summary>
/// Syncs qBittorrent state into job records and imports completed downloads through SafeImporter.
/// Queue identity rule: a job belongs to exactly one client (DownloadClientId, recorded at grab).
/// Only that client's list may update it or mark it missing, and only when that client is reachable.
/// Legacy jobs without a client are matched conservatively (hash preferred; name only when unique
/// across all reachable clients) and are marked missing only when no reachable client reports them.
/// An unreachable client is recorded as client health, never as a missing download.
/// </summary>
public sealed class DownloadService
{
    private static readonly TimeSpan MissingGracePeriod = TimeSpan.FromMinutes(15);
    private readonly IDbContextFactory<OneArrDbContext> _db;
    private readonly IDownloadClient _client;
    private readonly IImporter _importer;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly TimeSpan _quietPeriod;

    public DownloadService(IDbContextFactory<OneArrDbContext> db, IDownloadClient client, IImporter importer, SettingsStore settings, TimeProvider time)
    {
        _db = db;
        _client = client;
        _importer = importer;
        _settings = settings;
        _time = time;
        // Preview planning must defer exactly like the real import does. In production the
        // importer uses the 30 s default; tests use a zero period for speed.
        _quietPeriod = (importer as SafeImporter)?.QuietPeriod ?? SafeImporter.DefaultQuietPeriod;
    }

    public async Task<int> SyncQueueAsync(CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var now = _time.GetUtcNow();
        var clients = await db.DownloadClients.AsNoTracking().Where(c => c.Enabled).ToListAsync(ct);
        var clientById = clients.ToDictionary(c => c.Id);

        // One fetch per client, each failure isolated: a down client must not abort the sync for
        // the others, and must not make any job look missing.
        var itemsByClient = new Dictionary<int, List<DownloadClientItem>>();
        var reachable = new HashSet<int>();
        foreach (var client in clients)
        {
            try
            {
                var items = (await _client.ListAsync(client, client.MovieCategory, ct))
                    .Concat(await _client.ListAsync(client, client.TvCategory, ct)).ToList();
                itemsByClient[client.Id] = items;
                reachable.Add(client.Id);
                await UpsertHealthAsync(db, client.Id, now, null, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await UpsertHealthAsync(db, client.Id, null, Truncate(ex.Message, 500), now, ct);
            }
        }

        // Jobs eligible for polling: everything except Imported (terminal) and import-failed jobs
        // (operator-retryable). Download-side Failed jobs keep polling so recovery is possible.
        var active = await db.DownloadJobs
            .Where(j => j.Status != DownloadStatus.Imported && !(j.Status == DownloadStatus.Failed && j.ImportFailed))
            .ToListAsync(ct);
        var updated = 0;

        foreach (var job in active)
        {
            // Candidate clients for this job: its owner only, when the owner is known and enabled.
            // A job whose owner was disabled or deleted falls back to legacy behaviour (any client).
            List<int> candidateIds;
            if (job.DownloadClientId is int owner && clientById.ContainsKey(owner))
                candidateIds = [owner];
            else
                candidateIds = reachable.ToList();
            var candidateReachable = candidateIds.Where(reachable.Contains).ToList();
            if (candidateReachable.Count == 0) continue; // owner unreachable/disabled: leave untouched

            var candidates = candidateReachable.SelectMany(id => itemsByClient[id]).ToList();
            var match = FindMatch(job, candidates);
            if (match is not null)
            {
                var wasCompleted = job.Status == DownloadStatus.Completed;
                job.Hash ??= match.Hash;
                job.Status = QBittorrentClient.MapState(match.State, match.Progress);
                job.Progress = match.Progress;
                job.ClientState = match.State;
                job.ContentPath = match.ContentPath;
                job.DownloadSpeedBps = match.DownloadSpeedBps;
                job.EtaSeconds = match.EtaSeconds;
                job.LastSeenAt = now;
                job.Error = null;
                if (job.Status == DownloadStatus.Completed && !wasCompleted && job.CompletedAt is null)
                    job.CompletedAt = now;
                job.UpdatedAt = now;
                updated++;
                continue;
            }

            // Unmatched: missing only after the grace period, and only when a candidate client was
            // reachable and still does not report the job.
            if (job.Status is DownloadStatus.Missing) continue;
            var lastSeen = job.LastSeenAt ?? job.CreatedAt;
            if (now - lastSeen > MissingGracePeriod)
            {
                job.Status = DownloadStatus.Missing;
                job.UpdatedAt = now;
                updated++;
            }
        }

        await db.SaveChangesAsync(ct);
        return updated;
    }

    /// <summary>Hash match wins. Name match only when exactly one candidate shares the name, so two
    /// clients (or two torrents in one client) with the same torrent name can never be confused.</summary>
    private static DownloadClientItem? FindMatch(DownloadJob job, List<DownloadClientItem> candidates)
    {
        if (job.Hash is { Length: > 0 } h)
        {
            var byHash = candidates.FirstOrDefault(i => i.Hash == h);
            if (byHash is not null) return byHash;
            // A hash-keyed job that is not reported under its hash is not the same as an unnamed
            // torrent: never fall back to name matching for it.
            return null;
        }
        if (string.IsNullOrEmpty(job.ReleaseTitle)) return null;
        var named = candidates.Where(i => string.Equals(i.Name, job.ReleaseTitle, StringComparison.OrdinalIgnoreCase)).ToList();
        return named.Count == 1 ? named[0] : null;
    }

    private static async Task UpsertHealthAsync(OneArrDbContext db, int clientId, DateTimeOffset? seenAt, string? error, DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.DownloadClientHealth.FindAsync([clientId], ct);
        if (row is null)
        {
            db.DownloadClientHealth.Add(new DownloadClientHealth { DownloadClientId = clientId, LastSeenAt = seenAt, LastError = error, UpdatedAt = now });
        }
        else
        {
            row.LastSeenAt = seenAt ?? row.LastSeenAt;
            row.LastError = error;
            row.UpdatedAt = now;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // ---- Import ----

    public async Task<ImportRunSummary> ImportCompletedAsync(bool dryRun, CancellationToken ct)
    {
        var mediaRoot = await _settings.GetAsync(SettingKeys.MediaRoot, ct);
        var torrentRoot = await _settings.GetAsync(SettingKeys.TorrentRoot, ct);
        if (string.IsNullOrWhiteSpace(mediaRoot) || string.IsNullOrWhiteSpace(torrentRoot))
            return new ImportRunSummary(0, 0, 0, 0, "Set the media root and torrent root paths in Settings before importing");

        await using var db = await _db.CreateDbContextAsync(ct);
        var jobs = await db.DownloadJobs.Where(j => j.Status == DownloadStatus.Completed && j.ContentPath != null).ToListAsync(ct);
        var total = new ImportRunSummary(0, 0, 0, 0, "");
        foreach (var job in jobs)
        {
            var one = await ImportJobAsync(db, job, dryRun, mediaRoot, torrentRoot, ct);
            total = new ImportRunSummary(total.FilesConsidered + one.FilesConsidered, total.Imported + one.Imported, total.Failed + one.Failed, total.DryRun + one.DryRun, "");
            await db.SaveChangesAsync(ct);
        }
        var msg = dryRun ? $"Dry run: {total.DryRun} file(s) planned, {total.Failed} problem(s)" : $"Imported {total.Imported} file(s), {total.Failed} failed";
        return total with { Message = msg };
    }

    /// <summary>Imports one Completed job (bulk import, retry, and preview-apply all funnel here).
    /// Every file outcome is recorded, even when the importer itself throws, so a failure can never
    /// silently abort the run or lose the error.</summary>
    private async Task<ImportRunSummary> ImportJobAsync(OneArrDbContext db, DownloadJob job, bool dryRun, string mediaRoot, string torrentRoot, CancellationToken ct)
    {
        var media = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == job.MediaItemId, ct);
        if (media is null) return await FailJobAsync(db, job, "The media item for this download no longer exists", ct);
        if (string.IsNullOrWhiteSpace(job.ContentPath)) return await FailJobAsync(db, job, "This download has no content path recorded", ct);

        string destRoot;
        try
        {
            destRoot = SafeImporter.CanonicalUnder(mediaRoot, media.RootFolder);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return await FailJobAsync(db, job, "Root folder is outside the configured media root", ct);
        }

        var relFolder = RelativeFolder(media, job, db);
        var files = ListFiles(job.ContentPath!, torrentRoot);
        if (files.Count == 0) return await FailJobAsync(db, job, "No media files found in the completed download", ct);

        int considered = 0, imported = 0, failed = 0, planned = 0;
        var jobFailed = false;
        var jobDeferred = false;
        var matchedEpisodeIds = new HashSet<int>();

        foreach (var file in files)
        {
            considered++;
            ImportOutcome outcome;
            try
            {
                outcome = await _importer.ImportAsync(new ImportRequest(file, destRoot, relFolder, torrentRoot, dryRun, job.Id), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A locked file, an offline drive, or a copy failure must not kill the run or lose
                // the error: it becomes a Failed record for this file and the job is Failed.
                outcome = new ImportOutcome(ImportAction.Failed, "", false, ex.GetType().Name + ": " + Truncate(ex.Message, 500));
            }

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

            if (outcome.Action == ImportAction.Deferred) { jobDeferred = true; continue; }
            if (!outcome.Success) { jobFailed = true; failed++; continue; }
            if (outcome.Action == ImportAction.DryRun) { planned++; continue; }
            if (outcome.Action is ImportAction.Hardlink or ImportAction.Copy or ImportAction.Skipped)
            {
                imported++;
                foreach (var ep in MatchedEpisodes(db, media, job, file)) matchedEpisodeIds.Add(ep);
            }
        }

        if (!dryRun && !jobDeferred)
        {
            var tracked = await db.DownloadJobs.FirstAsync(j => j.Id == job.Id, ct);
            tracked.Status = jobFailed ? DownloadStatus.Failed : DownloadStatus.Imported;
            tracked.ImportFailed = jobFailed;
            tracked.Error = jobFailed ? "One or more files failed to import; see import records" : null;
            tracked.UpdatedAt = _time.GetUtcNow();
            if (!jobFailed) await MarkHaveFileAsync(db, job, media, matchedEpisodeIds, ct);
        }
        return new ImportRunSummary(considered, imported, failed, planned, "");
    }

    private async Task<ImportRunSummary> FailJobAsync(OneArrDbContext db, DownloadJob job, string error, CancellationToken ct)
    {
        var tracked = await db.DownloadJobs.FirstAsync(j => j.Id == job.Id, ct);
        tracked.Status = DownloadStatus.Failed;
        tracked.ImportFailed = true;
        tracked.Error = error;
        tracked.UpdatedAt = _time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new ImportRunSummary(0, 0, 1, 0, error);
    }

    /// <summary>Retries a failed import: Failed + ImportFailed goes back to Completed and the job is
    /// imported again (real import, never dry-run for a retry — the operator asked for the import).
    /// Any other status is refused.</summary>
    public async Task<ImportRunSummary> RetryImportAsync(int jobId, CancellationToken ct)
    {
        var mediaRoot = await _settings.GetAsync(SettingKeys.MediaRoot, ct);
        var torrentRoot = await _settings.GetAsync(SettingKeys.TorrentRoot, ct);
        if (string.IsNullOrWhiteSpace(mediaRoot) || string.IsNullOrWhiteSpace(torrentRoot))
            throw new ArgumentException("Set the media root and torrent root paths in Settings before importing");
        await using var db = await _db.CreateDbContextAsync(ct);
        var job = await db.DownloadJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw new ArgumentException("Download job not found");
        if (job.Status != DownloadStatus.Failed || !job.ImportFailed)
            throw new ArgumentException("Only a failed import can be retried");
        job.Status = DownloadStatus.Completed;
        job.Error = null;
        job.UpdatedAt = _time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        var summary = await ImportJobAsync(db, job, dryRun: false, mediaRoot, torrentRoot, ct);
        await db.SaveChangesAsync(ct);
        return summary with { Message = $"Imported {summary.Imported} file(s), {summary.Failed} failed" };
    }

    private async Task MarkHaveFileAsync(OneArrDbContext db, DownloadJob job, MediaItem media, HashSet<int> matchedEpisodeIds, CancellationToken ct)
    {
        if (media.MediaType == MediaType.Movie)
        {
            var item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == media.Id, ct);
            if (item is not null) item.HasFile = true;
            return;
        }
        // Series: mark exactly the episodes the imported files actually cover. A single-episode job
        // marks its episode even when the file name does not parse (the job is the association);
        // a pack marks every parsed episode that exists. Nothing else is ever marked, and
        // Monitored flags are never touched here.
        var ids = matchedEpisodeIds.Count > 0 ? matchedEpisodeIds : job.EpisodeId is int eid ? new HashSet<int> { eid } : new HashSet<int>();
        if (ids.Count == 0) return;
        var episodes = await db.Episodes.Where(e => ids.Contains(e.Id)).ToListAsync(ct);
        foreach (var ep in episodes) ep.HasFile = true;
    }

    /// <summary>Episodes a file covers, by filename (S01E02, ranges, 1x02). Only episodes that exist
    /// for this series count; anything else is unmatched, never guessed.</summary>
    private static List<int> MatchedEpisodes(OneArrDbContext db, MediaItem media, DownloadJob job, string file)
    {
        if (media.MediaType != MediaType.Series) return [];
        if (job.EpisodeId is int eid) return [eid];
        var parsed = LibraryMatcher.ParseEpisodes(Path.GetFileName(file));
        if (parsed.Count == 0) return [];
        var episodes = db.Episodes.AsNoTracking().Where(e => e.MediaItemId == media.Id).ToList();
        return parsed
            .Select(p => episodes.FirstOrDefault(e => e.SeasonNumber == p.Season && e.EpisodeNumber == p.Number)?.Id)
            .Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
    }

    // ---- Import preview ----

    /// <summary>Plans an import without touching disk beyond reads (sizes, times, dest-exists hash
    /// checks). The returned preview is stored so an apply can prove it is still current.</summary>
    public async Task<ImportPreviewDto> PreviewImportAsync(int jobId, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var (job, plans) = await PlanJobAsync(db, jobId, ct);
        var fingerprint = Fingerprint(plans);
        var preview = new ImportPreview { DownloadJobId = job.Id, Fingerprint = fingerprint, PlansJson = JsonSerializer.Serialize(plans), CreatedAt = _time.GetUtcNow() };
        db.ImportPreviews.Add(preview);
        await db.SaveChangesAsync(ct);
        return new ImportPreviewDto(preview.Id, job.Id, preview.CreatedAt, plans, fingerprint);
    }

    public async Task<ImportPreviewDto> ApplyPreviewAsync(int jobId, int previewId, CancellationToken ct)
    {
        var mediaRoot = await _settings.GetAsync(SettingKeys.MediaRoot, ct);
        var torrentRoot = await _settings.GetAsync(SettingKeys.TorrentRoot, ct);
        if (string.IsNullOrWhiteSpace(mediaRoot) || string.IsNullOrWhiteSpace(torrentRoot))
            throw new ArgumentException("Set the media root and torrent root paths in Settings before importing");
        await using var db = await _db.CreateDbContextAsync(ct);
        var preview = await db.ImportPreviews.FirstOrDefaultAsync(p => p.Id == previewId && p.DownloadJobId == jobId, ct)
            ?? throw new ArgumentException("Import preview not found for this download");
        var stored = JsonSerializer.Deserialize<List<ImportPlanDto>>(preview.PlansJson) ?? [];
        var (job, fresh) = await PlanJobAsync(db, jobId, ct);
        var freshBySource = fresh.ToDictionary(p => p.SourcePath, StringComparer.OrdinalIgnoreCase);

        // Stale check: every previewed file must still plan to the same destination with the same
        // size, last-write time (via the fingerprint), and action. New files since the preview are
        // deliberately NOT imported by this apply; they need their own preview.
        var mismatch = false;
        foreach (var plan in stored)
        {
            if (!freshBySource.TryGetValue(plan.SourcePath, out var now) || now.DestinationPath != plan.DestinationPath || now.SizeBytes != plan.SizeBytes || now.LastWriteUtcTicks != plan.LastWriteUtcTicks || now.PlannedAction != plan.PlannedAction)
            {
                mismatch = true;
                break;
            }
        }
        if (mismatch || Fingerprint(stored) != preview.Fingerprint)
        {
            var freshDto = new ImportPreviewDto(0, job.Id, _time.GetUtcNow(), fresh, Fingerprint(fresh));
            throw new StalePreviewException(freshDto);
        }

        // Execute exactly the previewed plans. Rejected/deferred plans are recorded, not executed.
        var media = await db.MediaItems.AsNoTracking().FirstAsync(m => m.Id == job.MediaItemId, ct);
        var relFolder = RelativeFolder(media, job, db);
        string destRoot;
        try { destRoot = SafeImporter.CanonicalUnder(mediaRoot, media.RootFolder); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ArgumentException("Root folder is outside the configured media root");
        }
        int imported = 0, failed = 0;
        var jobFailed = false;
        var jobDeferred = false;
        var matchedEpisodeIds = new HashSet<int>();
        foreach (var plan in stored)
        {
            if (plan.PlannedAction is "reject" or "defer")
            {
                db.ImportRecords.Add(new ImportRecord
                {
                    DownloadJobId = job.Id, SourcePath = plan.SourcePath, DestinationPath = plan.DestinationPath ?? "",
                    Action = plan.PlannedAction == "defer" ? ImportAction.Deferred : ImportAction.Failed,
                    Success = false, Message = plan.Reason, CreatedAt = _time.GetUtcNow(),
                });
                if (plan.PlannedAction == "defer") jobDeferred = true; else { jobFailed = true; failed++; }
                continue;
            }
            ImportOutcome outcome;
            try
            {
                outcome = await _importer.ImportAsync(new ImportRequest(plan.SourcePath, destRoot, relFolder, torrentRoot, dryRun: false, job.Id), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcome = new ImportOutcome(ImportAction.Failed, "", false, ex.GetType().Name + ": " + Truncate(ex.Message, 500));
            }
            db.ImportRecords.Add(new ImportRecord
            {
                DownloadJobId = job.Id, SourcePath = plan.SourcePath, DestinationPath = outcome.DestinationPath,
                Action = outcome.Action, Success = outcome.Success, Message = outcome.Message, CreatedAt = _time.GetUtcNow(),
            });
            if (outcome.Action == ImportAction.Deferred) { jobDeferred = true; continue; }
            if (!outcome.Success) { jobFailed = true; failed++; continue; }
            if (outcome.Action is ImportAction.Hardlink or ImportAction.Copy or ImportAction.Skipped)
            {
                imported++;
                foreach (var ep in MatchedEpisodes(db, media, job, plan.SourcePath)) matchedEpisodeIds.Add(ep);
            }
        }
        if (!jobDeferred)
        {
            var tracked = await db.DownloadJobs.FirstAsync(j => j.Id == job.Id, ct);
            tracked.Status = jobFailed ? DownloadStatus.Failed : DownloadStatus.Imported;
            tracked.ImportFailed = jobFailed;
            tracked.Error = jobFailed ? "One or more files failed to import; see import records" : null;
            tracked.UpdatedAt = _time.GetUtcNow();
            if (!jobFailed) await MarkHaveFileAsync(db, job, media, matchedEpisodeIds, ct);
        }
        await db.SaveChangesAsync(ct);
        return new ImportPreviewDto(preview.Id, job.Id, preview.CreatedAt, stored, preview.Fingerprint);
    }

    private async Task<(DownloadJob Job, List<ImportPlanDto> Plans)> PlanJobAsync(OneArrDbContext db, int jobId, CancellationToken ct)
    {
        var job = await db.DownloadJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw new ArgumentException("Download job not found");
        var mediaRoot = await _settings.GetAsync(SettingKeys.MediaRoot, ct);
        var torrentRoot = await _settings.GetAsync(SettingKeys.TorrentRoot, ct);
        if (string.IsNullOrWhiteSpace(mediaRoot) || string.IsNullOrWhiteSpace(torrentRoot))
            throw new ArgumentException("Set the media root and torrent root paths in Settings before importing");
        var media = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == job.MediaItemId, ct)
            ?? throw new ArgumentException("The media item for this download no longer exists");
        if (string.IsNullOrWhiteSpace(job.ContentPath))
            throw new ArgumentException("This download has no content path recorded");
        string destRoot;
        try { destRoot = SafeImporter.CanonicalUnder(mediaRoot, media.RootFolder); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ArgumentException("Root folder is outside the configured media root");
        }
        var relFolder = RelativeFolder(media, job, db);
        var files = ListFiles(job.ContentPath!, torrentRoot);
        var plans = new List<ImportPlanDto>();
        var episodes = media.MediaType == MediaType.Series
            ? await db.Episodes.AsNoTracking().Where(e => e.MediaItemId == media.Id).ToListAsync(ct)
            : new List<Episode>();
        foreach (var file in files)
        {
            plans.Add(await PlanFileAsync(file, destRoot, relFolder, torrentRoot, media, job, episodes, ct));
        }
        return (job, plans);
    }

    private async Task<ImportPlanDto> PlanFileAsync(string file, string destRoot, string relFolder, string torrentRoot, MediaItem media, DownloadJob job, List<Episode> episodes, CancellationToken ct)
    {
        var labels = new List<int>();
        var labelTexts = new List<string>();
        if (media.MediaType == MediaType.Series)
        {
            if (job.EpisodeId is int eid)
            {
                var ep = episodes.FirstOrDefault(e => e.Id == eid);
                if (ep is not null) { labels.Add(ep.Id); labelTexts.Add($"S{ep.SeasonNumber:00}E{ep.EpisodeNumber:00}"); }
            }
            else
            {
                foreach (var p in LibraryMatcher.ParseEpisodes(Path.GetFileName(file)))
                {
                    var ep = episodes.FirstOrDefault(e => e.SeasonNumber == p.Season && e.EpisodeNumber == p.Number);
                    if (ep is not null) { labels.Add(ep.Id); labelTexts.Add($"S{ep.SeasonNumber:00}E{ep.EpisodeNumber:00}"); }
                }
            }
        }
        string? dest = null;
        try
        {
            var source = SafeImporter.CanonicalUnder(torrentRoot, file);
            SafeImporter.RequireNoLinksBelow(torrentRoot, source);
            dest = SafeImporter.PlanDestination(destRoot, relFolder, Path.GetFileName(source));
            SafeImporter.RequireNoLinksBelow(destRoot, dest);
            var info = new FileInfo(source);
            if (!info.Exists) return Reject(file, dest, 0, 0, labels, labelTexts, "Source file does not exist");
            if (info.LinkTarget is not null) return Reject(file, dest, info.Length, info.LastWriteTimeUtc.Ticks, labels, labelTexts, "Refusing to import a symbolic link");
            if (_quietPeriod > TimeSpan.Zero && DateTime.UtcNow - info.LastWriteTimeUtc < _quietPeriod)
                return new ImportPlanDto(file, dest, info.Length, info.LastWriteTimeUtc.Ticks, Path.GetExtension(file), "defer", $"Source changed within the last {_quietPeriod.TotalSeconds:0} s; will retry", false, SafeImporter.IsSameVolume(source, dest), labels, labelTexts);
            if (File.Exists(dest))
            {
                if (await SafeImporter.SameContentAsync(source, dest, ct))
                    return new ImportPlanDto(file, dest, info.Length, info.LastWriteTimeUtc.Ticks, Path.GetExtension(file), "skip", "Already imported with identical content", false, SafeImporter.IsSameVolume(source, dest), labels, labelTexts);
                return Reject(file, dest, info.Length, info.LastWriteTimeUtc.Ticks, labels, labelTexts, "Destination exists with different content; not overwriting", conflict: true);
            }
            var sameVolume = SafeImporter.IsSameVolume(source, dest);
            return new ImportPlanDto(file, dest, info.Length, info.LastWriteTimeUtc.Ticks, Path.GetExtension(file), sameVolume ? "hardlink" : "copy", sameVolume ? "Hardlink on the same volume" : "Copy to a different volume (verified, SHA-256)", false, sameVolume, labels, labelTexts);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or NotSupportedException or IOException)
        {
            return Reject(file, dest, 0, 0, labels, labelTexts, ex.Message);
        }
    }

    private static ImportPlanDto Reject(string file, string? dest, long size, long ticks, List<int> labels, List<string> labelTexts, string reason, bool conflict = false) =>
        new(file, dest, size, ticks, Path.GetExtension(file), "reject", reason, conflict, false, labels, labelTexts);

    private static string Fingerprint(List<ImportPlanDto> plans)
    {
        var lines = plans.OrderBy(p => p.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(p => $"{p.SourcePath}|{p.SizeBytes}|{p.LastWriteUtcTicks}|{p.DestinationPath}|{p.PlannedAction}");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
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
        // Boundary-correct containment: a file under D:\Downloads2 must not pass a D:\Downloads
        // root. SafeImporter.CanonicalUnder appends the separator before comparing; a raw
        // StartsWith here would accept the sibling directory.
        return Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(SafeImporter.IsMediaFile)
            .Where(f =>
            {
                try { SafeImporter.CanonicalUnder(torrentRoot, f); return true; }
                catch { return false; }
            })
            .ToList();
    }
}
