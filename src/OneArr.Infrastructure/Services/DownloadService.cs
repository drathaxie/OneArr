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

/// <summary>One confirmed bulk approval: a specific job's specific preview. A preview that was
/// merely generated authorizes nothing; only a pair the operator confirmed does.</summary>
public sealed record BulkApproval(int JobId, int PreviewId);

public sealed record BulkApplyResult(int Applied, int SkippedStale, int Failed, string Message);

/// <summary>
/// Syncs qBittorrent state into job records and imports completed downloads through SafeImporter.
/// Queue identity rule: a job belongs to exactly one client (DownloadClientId, recorded at grab).
/// Only that client's list may update it or mark it missing, and only when that client is reachable.
/// An owned job whose client is disabled or deleted is NEVER matched against other clients: it is
/// left untouched (and surfaced as a client problem) rather than re-associated.
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
            // Candidate clients for this job: its owner only, when the owner is known. An owned
            // job whose owner is disabled, deleted, or unreachable is left untouched — it never
            // falls back to searching other clients, so it can never be re-associated.
            if (job.DownloadClientId is int owner)
            {
                if (!clientById.ContainsKey(owner) || !reachable.Contains(owner)) continue;
            }
            var candidateIds = job.DownloadClientId is int owned ? new List<int> { owned } : reachable.ToList();
            var candidateReachable = candidateIds.Where(reachable.Contains).ToList();
            if (candidateReachable.Count == 0) continue; // legacy job, no client reachable: leave untouched

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

    /// <summary>
    /// A deliberately conservative automatic importer, separate from the manual
    /// ImportCompletedAsync bulk engine. Every write passes through the SAME
    /// stored preview, fingerprint recheck and SafeImporter as manual Apply.
    /// Missing setting, dry-run setting, stale qBit observation and ambiguous
    /// media all fail closed. The torrent source is never moved or removed.
    /// </summary>
    public async Task<ImportRunSummary> AutoImportCompletedAsync(CancellationToken ct)
    {
        if (!await _settings.GetBoolAsync(SettingKeys.AutomaticImport, false, ct) ||
            await _settings.GetBoolAsync(SettingKeys.DryRunImport, true, ct))
            return new ImportRunSummary(0, 0, 0, 0, "Automatic import is disabled or dry-run mode is enabled");

        var now = _time.GetUtcNow();
        await using var db = await _db.CreateDbContextAsync(ct);
        // Bounded pass: no unbounded database or filesystem loop on startup.
        var pending = await db.DownloadJobs.AsNoTracking()
            .Where(j => j.Status == DownloadStatus.Completed)
            .OrderBy(j => j.Id)
            .Take(5)
            .Select(j => j.Id)
            .ToListAsync(ct);
        int considered = 0, imported = 0, deferred = 0, failed = 0;
        foreach (var id in pending)
        {
            ct.ThrowIfCancellationRequested();
            // Recheck immediately prior to each job; toggling OFF never starts
            // another import on the next job.
            if (!await _settings.GetBoolAsync(SettingKeys.AutomaticImport, false, ct) ||
                await _settings.GetBoolAsync(SettingKeys.DryRunImport, true, ct)) break;
            try
            {
                // Plan/inspect through the same code as the manual preview,
                // but do not generate a stored preview for ineligible jobs.
                await using var planDb = await _db.CreateDbContextAsync(ct);
                var (job, plans) = await PlanJobAsync(planDb, id, ct);
                var media = await planDb.MediaItems.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == job.MediaItemId, ct);
                var owner = job.DownloadClientId is int ownerId
                    ? await planDb.DownloadClients.AsNoTracking()
                        .FirstOrDefaultAsync(c => c.Id == ownerId, ct)
                    : null;
                Episode? target = job.EpisodeId is int epId
                    ? await planDb.Episodes.AsNoTracking().FirstOrDefaultAsync(e => e.Id == epId, ct)
                    : null;
                considered++;
                var why = AutoImportReviewReason(job, media, owner, plans, now, target);
                if (why is not null) { deferred++; continue; }

                // Store exactly the verified plans. Apply recomputes and
                // rechecks the fingerprint; a changed source fails closed.
                var preview = new ImportPreview
                {
                    DownloadJobId = id,
                    PlansJson = JsonSerializer.Serialize(plans),
                    Fingerprint = Fingerprint(plans),
                    CreatedAt = _time.GetUtcNow(),
                };
                planDb.ImportPreviews.Add(preview);
                await planDb.SaveChangesAsync(ct);
                if (!await _settings.GetBoolAsync(SettingKeys.AutomaticImport, false, ct) ||
                    await _settings.GetBoolAsync(SettingKeys.DryRunImport, true, ct)) break;

                await ApplyPreviewAsync(id, preview.Id, ct);
                await using var resultDb = await _db.CreateDbContextAsync(ct);
                var result = await resultDb.DownloadJobs.AsNoTracking().FirstAsync(j => j.Id == id, ct);
                if (result.Status == DownloadStatus.Imported) imported++;
                else if (result.Status == DownloadStatus.Failed) failed++;
                else deferred++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or IOException
                or InvalidOperationException or StalePreviewException)
            {
                // Never use the destructive older bulk engine as a fallback.
                // The job remains Completed for explicit review/retry.
                deferred++;
            }
        }
        return new ImportRunSummary(considered, imported, failed, deferred,
            $"Auto import: {imported} completed job(s) imported, {deferred} requiring review or retry, {failed} failed");
    }

    /// <summary>Pure fail-closed eligibility for fully completed, recently
    /// observed, unequivocally mapped media. Anything needing judgment is
    /// left for the existing manual preview/apply UI.</summary>
    internal static string? AutoImportReviewReason(
        DownloadJob job, MediaItem? media, DownloadClientConfig? owner,
        IReadOnlyList<ImportPlanDto> plans, DateTimeOffset now, Episode? episode)
    {
        if (job.Status != DownloadStatus.Completed || job.Progress < 0.999)
            return "Download is not completely finished";
        if (job.DownloadClientId is null || owner is null || !owner.Enabled ||
            string.IsNullOrWhiteSpace(job.Hash))
            return "Download client identity is not verified";
        var category = job.MediaType == MediaType.Movie ? owner.MovieCategory : owner.TvCategory;
        if (string.IsNullOrWhiteSpace(category) || !string.Equals(job.Category, category, StringComparison.OrdinalIgnoreCase))
            return "Download category does not match the owning client";
        if (job.CompletedAt is null || now - job.CompletedAt < TimeSpan.FromMinutes(1) ||
            now - job.CompletedAt > TimeSpan.FromDays(90))
            return "Completion has not been observed or is too old";
        if (job.LastSeenAt is null || now - job.LastSeenAt < TimeSpan.Zero ||
            now - job.LastSeenAt > TimeSpan.FromMinutes(3))
            return "Download client has not confirmed a recent completed state";
        if (media is null || media.Id != job.MediaItemId || media.MediaType != job.MediaType)
            return "Media identity is not verified";
        if (plans.Count == 0 || plans.Count > 32 ||
            plans.Any(p => p.PlannedAction is not ("hardlink" or "copy" or "skip") ||
                p.DestinationConflict || string.IsNullOrWhiteSpace(p.DestinationPath) ||
                p.SizeBytes <= 0))
            return "Files require a manual review or have an unsafe plan";

        var release = new ReleaseParser().Parse(job.ReleaseTitle);
        if (!string.Equals(ReleaseParser.NormalizeTitle(release.Title),
            ReleaseParser.NormalizeTitle(media.Title), StringComparison.Ordinal))
            return "Release title does not match the media item";
        if (media.MediaType == MediaType.Movie)
        {
            if (media.HasFile || plans.Count != 1 || job.EpisodeId is not null)
                return "Existing movie file, ambiguous movie, or multiple video files";
            if (release.Year is int year && media.Year is int wantedYear && Math.Abs(year - wantedYear) > 1)
                return "Movie year does not match";
            return null;
        }

        if (plans.Any(p => p.MatchedEpisodeIds.Count != 1))
            return "Multi-episode or unmatched files require manual review";
        var ids = plans.Select(p => p.MatchedEpisodeIds[0]).ToList();
        if (ids.Distinct().Count() != ids.Count)
            return "Multiple files claim the same episode";
        if (job.EpisodeId is int id && (episode is null || episode.MediaItemId != media.Id ||
            episode.HasFile || plans.Count != 1 || ids[0] != id))
            return "Episode association is ambiguous or already has a file";
        // A single-episode download may carry an unrelated video in its
        // directory. Do not trust job.EpisodeId without parsing the filename.
        foreach (var plan in plans)
        {
            var matched = LibraryMatcher.ParseEpisodes(Path.GetFileName(plan.SourcePath));
            if (matched.Count != 1) return "Episode filename is ambiguous";
            if (job.EpisodeId is int episodeId)
            {
                if (episode is null || matched[0].Season != episode.SeasonNumber ||
                    matched[0].Number != episode.EpisodeNumber)
                    return "Video filename does not match the grabbed episode";
            }
            else if (release.Season is int releaseSeason && matched[0].Season != releaseSeason)
                return "Video season disagrees with the release";
        }
        return null;
    }

    // ---- Import ----

    /// <summary>Bulk import engine. A dry run is strictly read-only: it plans and reports problems
    /// without changing any job, record, or file. Production bulk import (POST /api/import with
    /// dryRun=false) does NOT call this with dryRun=false; it applies only explicitly confirmed
    /// (job, preview) pairs via ApplySelectedAsync, so no production route imports a job on the
    /// strength of a preview that was merely generated.
    /// Completed jobs with a null content path are deliberately included: they must fail visibly
    /// (or report in a dry run), never be silently skipped by the query.</summary>
    public async Task<ImportRunSummary> ImportCompletedAsync(bool dryRun, CancellationToken ct)
    {
        var mediaRoot = await _settings.GetAsync(SettingKeys.MediaRoot, ct);
        var torrentRoot = await _settings.GetAsync(SettingKeys.TorrentRoot, ct);
        if (string.IsNullOrWhiteSpace(mediaRoot) || string.IsNullOrWhiteSpace(torrentRoot))
            return new ImportRunSummary(0, 0, 0, 0, "Set the media root and torrent root paths in Settings before importing");

        await using var db = await _db.CreateDbContextAsync(ct);
        var jobs = await db.DownloadJobs.Where(j => j.Status == DownloadStatus.Completed).ToListAsync(ct);
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

    /// <summary>Imports one Completed job (bulk import engine). Every file outcome is recorded,
    /// even when the importer itself throws, so a failure can never silently abort the run or
    /// lose the error. A dry run writes nothing at all: no job change, no record, no file.</summary>
    private async Task<ImportRunSummary> ImportJobAsync(OneArrDbContext db, DownloadJob job, bool dryRun, string mediaRoot, string torrentRoot, CancellationToken ct)
    {
        if (job.Status != DownloadStatus.Completed)
            return new ImportRunSummary(0, 0, 1, 0, "Only completed downloads can be imported");
        var media = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == job.MediaItemId, ct);
        if (media is null) return await FailJobOrReportAsync(db, job, "The media item for this download no longer exists", dryRun, ct);
        if (string.IsNullOrWhiteSpace(job.ContentPath)) return await FailJobOrReportAsync(db, job, "This download has no content path recorded", dryRun, ct);

        string destRoot;
        try
        {
            destRoot = SafeImporter.CanonicalUnder(mediaRoot, media.RootFolder);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return await FailJobOrReportAsync(db, job, "Root folder is outside the configured media root", dryRun, ct);
        }

        var relFolder = RelativeFolder(media, job, db);
        List<string> files;
        try
        {
            files = ListFiles(await ResolveContentPathAsync(job, torrentRoot, ct), torrentRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or IOException)
        {
            return await FailJobOrReportAsync(db, job, "Download path mapping: " + ex.Message, dryRun, ct);
        }
        if (files.Count == 0) return await FailJobOrReportAsync(db, job, "No media files found in the completed download", dryRun, ct);

        int considered = 0, imported = 0, failed = 0, planned = 0;
        var jobFailed = false;
        var jobDeferred = false;
        var matchedEpisodeIds = new HashSet<int>();
        var importedFiles = new List<(string Dest, List<int> Episodes)>();
        var unmatched = new List<string>();

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

            // Dry runs record nothing: the summary counts are the whole report.
            if (!dryRun)
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
                var matched = MatchedEpisodes(db, media, job, file);
                foreach (var ep in matched) matchedEpisodeIds.Add(ep);
                importedFiles.Add((outcome.DestinationPath, matched));
                // A series pack file that matched no episode is imported but stays unresolved:
                // it gets an Unmatched record and the job is never marked fully Imported.
                if (!dryRun && media.MediaType == MediaType.Series && job.EpisodeId is null && matched.Count == 0)
                {
                    unmatched.Add(file);
                    await AddUnmatchedRecordAsync(db, job.Id, file, outcome.DestinationPath, ct);
                }
            }
        }

        if (!dryRun && !jobDeferred)
        {
            var tracked = await db.DownloadJobs.FirstAsync(j => j.Id == job.Id, ct);
            tracked.UpdatedAt = _time.GetUtcNow();
            if (jobFailed)
            {
                tracked.Status = DownloadStatus.Failed;
                tracked.ImportFailed = true;
                tracked.Error = "One or more files failed to import; see import records";
            }
            else
            {
                await MarkHaveFileAsync(db, job, media, matchedEpisodeIds, ct);
                await LinkImportedFilesAsync(db, media, importedFiles, ct);
                if (unmatched.Count > 0)
                {
                    tracked.Status = DownloadStatus.Completed;
                    tracked.ImportFailed = false;
                    tracked.Error = $"{unmatched.Count} file(s) imported but unmatched to any episode; see import records";
                }
                else
                {
                    tracked.Status = DownloadStatus.Imported;
                    tracked.ImportFailed = false;
                    tracked.Error = null;
                }
            }
        }
        return new ImportRunSummary(considered, imported, failed, planned, "");
    }

    /// <summary>Real runs fail the job persistently; dry runs only report the problem.</summary>
    private async Task<ImportRunSummary> FailJobOrReportAsync(OneArrDbContext db, DownloadJob job, string error, bool dryRun, CancellationToken ct) =>
        dryRun ? new ImportRunSummary(0, 0, 1, 0, error) : await FailJobAsync(db, job, error, ct);

    /// <summary>Records an unmatched import once per (job, source). The file is in the library;
    /// the record is what keeps that fact visible instead of silently marking the job Imported.</summary>
    private async Task AddUnmatchedRecordAsync(OneArrDbContext db, int jobId, string source, string dest, CancellationToken ct)
    {
        var exists = await db.ImportRecords.AnyAsync(r => r.DownloadJobId == jobId && r.SourcePath == source && r.Action == ImportAction.Unmatched, ct);
        if (exists) return;
        db.ImportRecords.Add(new ImportRecord
        {
            DownloadJobId = jobId,
            SourcePath = source,
            DestinationPath = dest,
            Action = ImportAction.Unmatched,
            Success = false,
            Message = "Imported but unmatched to any episode; no episode was marked available",
            CreatedAt = _time.GetUtcNow(),
        });
    }

    /// <summary>Creates the MediaFile / MediaFileEpisode rows the Details → Files view reads, so
    /// availability is backed by verified file associations instead of bare HasFile flags. Rows
    /// are keyed by PathKey (the same key the library reconciler uses), so a rescan or a
    /// re-import reuses the row instead of duplicating it, and episode links are unioned.</summary>
    private async Task LinkImportedFilesAsync(OneArrDbContext db, MediaItem media, List<(string Dest, List<int> Episodes)> files, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var windows = OperatingSystem.IsWindows();
        foreach (var (dest, episodeIds) in files)
        {
            if (string.IsNullOrWhiteSpace(dest)) continue;
            var key = LibraryPaths.Key(dest, windows);
            var row = await db.MediaFiles.FirstOrDefaultAsync(f => f.PathKey == key, ct);
            long size = 0;
            try { if (File.Exists(dest)) size = new FileInfo(dest).Length; } catch { /* size stays unknown */ }
            if (row is null)
            {
                row = new MediaFile { Path = dest, PathKey = key, SizeBytes = size, Status = MediaFileStatus.Present, MediaItemId = media.Id, MatchMethod = "import", FirstSeenUtc = now, LastVerifiedUtc = now };
                db.MediaFiles.Add(row);
                foreach (var ep in episodeIds) row.EpisodeLinks.Add(new MediaFileEpisode { EpisodeId = ep });
            }
            else
            {
                row.Path = dest;
                row.SizeBytes = size;
                row.Status = MediaFileStatus.Present;
                row.MediaItemId = media.Id;
                row.MatchMethod = "import";
                row.LastVerifiedUtc = now;
                var have = (await db.MediaFileEpisodes.Where(l => l.MediaFileId == row.Id).Select(l => l.EpisodeId).ToListAsync(ct)).ToHashSet();
                foreach (var ep in episodeIds.Where(e => !have.Contains(e)))
                    db.MediaFileEpisodes.Add(new MediaFileEpisode { MediaFileId = row.Id, EpisodeId = ep });
            }
        }
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

    /// <summary>Retries a failed import by applying its latest stored preview. A retry without a
    /// preview is refused: the operator previews first, then retries, so a retry can never import
    /// around the preview-and-confirmation model. A stale preview surfaces as
    /// StalePreviewException (the endpoint turns that into a 409 with a fresh preview).
    /// Any status other than Failed + ImportFailed is refused.</summary>
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
        var preview = await db.ImportPreviews.Where(p => p.DownloadJobId == jobId).OrderByDescending(p => p.Id).FirstOrDefaultAsync(ct)
            ?? throw new ArgumentException("Generate an import preview for this download first; retry applies the latest preview");
        var before = (await db.ImportRecords.Where(r => r.DownloadJobId == jobId).Select(r => r.Id).ToListAsync(ct)).ToHashSet();
        await ApplyPreviewAsync(jobId, preview.Id, ct);
        await using var afterDb = await _db.CreateDbContextAsync(ct);
        var fresh = await afterDb.ImportRecords.Where(r => r.DownloadJobId == jobId && !before.Contains(r.Id)).ToListAsync(ct);
        var imported = fresh.Count(r => r.Success && r.Action is ImportAction.Hardlink or ImportAction.Copy or ImportAction.Skipped);
        var failed = fresh.Count(r => !r.Success && r.Action == ImportAction.Failed);
        var unmatched = fresh.Count(r => r.Action == ImportAction.Unmatched);
        var deferred = fresh.Any(r => r.Action == ImportAction.Deferred);
        var msg = $"Retried import: {imported} file(s) imported, {failed} failed";
        if (unmatched > 0) msg += $", {unmatched} imported but unmatched to any episode";
        if (deferred) msg += "; some files deferred";
        return new ImportRunSummary(0, imported, failed, 0, msg);
    }

    /// <summary>Bulk import for production: applies ONLY the explicitly confirmed (job, preview)
    /// pairs. A stored preview that was merely inspected is never applied by this path — without
    /// confirmation and at least one pair, nothing is applied at all. Each pair goes through the
    /// same stale-checked apply as the per-job endpoint, so a changed plan is refused, not run.</summary>
    public async Task<BulkApplyResult> ApplySelectedAsync(bool confirmed, List<BulkApproval> approvals, CancellationToken ct)
    {
        if (!confirmed)
            throw new ArgumentException("Bulk import requires explicit confirmation with specific approved previews");
        if (approvals is null || approvals.Count == 0)
            throw new ArgumentException("Bulk import requires at least one approved preview (jobId + previewId pair); previewing alone never authorizes an import");
        var mediaRoot = await _settings.GetAsync(SettingKeys.MediaRoot, ct);
        var torrentRoot = await _settings.GetAsync(SettingKeys.TorrentRoot, ct);
        if (string.IsNullOrWhiteSpace(mediaRoot) || string.IsNullOrWhiteSpace(torrentRoot))
            throw new ArgumentException("Set the media root and torrent root paths in Settings before importing");
        int applied = 0, stale = 0, failed = 0;
        foreach (var approval in approvals)
        {
            try
            {
                await ApplyPreviewAsync(approval.JobId, approval.PreviewId, ct);
                await using var check = await _db.CreateDbContextAsync(ct);
                var after = await check.DownloadJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == approval.JobId, ct);
                if (after is not null && after.Status == DownloadStatus.Failed && after.ImportFailed) failed++;
                else applied++;
            }
            catch (StalePreviewException) { stale++; }
            catch (ArgumentException) { failed++; }
        }
        var msg = $"Applied {applied} approved preview(s); {stale} skipped (preview stale — re-preview first); {failed} failed";
        return new BulkApplyResult(applied, stale, failed, msg);
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
        if (stored.Count == 0) throw new ArgumentException("Cannot apply an empty import preview");
        var (job, fresh) = await PlanJobAsync(db, jobId, ct);
        var freshBySource = fresh.ToDictionary(p => p.SourcePath, StringComparer.OrdinalIgnoreCase);

        // Stale check: every previewed file must still plan to the same destination with the same
        // size, last-write time (via the fingerprint), and action. New files since the preview are
        // deliberately NOT imported by this apply; they need their own preview.
        var mismatch = false;
        foreach (var plan in stored)
        {
            if (!freshBySource.TryGetValue(plan.SourcePath, out var now) || now.DestinationPath != plan.DestinationPath || now.SizeBytes != plan.SizeBytes || now.LastWriteUtcTicks != plan.LastWriteUtcTicks || now.PlannedAction != plan.PlannedAction || !now.MatchedEpisodeIds.SequenceEqual(plan.MatchedEpisodeIds))
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
        int imported = 0, failed = 0, unmatched = 0;
        var jobFailed = false;
        var jobDeferred = false;
        var matchedEpisodeIds = new HashSet<int>();
        var importedFiles = new List<(string Dest, List<int> Episodes)>();
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
                if (string.IsNullOrWhiteSpace(plan.DestinationPath))
                    throw new ArgumentException("The approved import plan has no destination");
                outcome = await _importer.ImportAsync(new ImportRequest(plan.SourcePath, destRoot, relFolder,
                    torrentRoot, DryRun: false, job.Id, Path.GetFileName(plan.DestinationPath)), ct);
                if (!string.Equals(outcome.DestinationPath, plan.DestinationPath,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new ArgumentException("Importer destination no longer matches the approved preview");
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
                foreach (var ep in plan.MatchedEpisodeIds) matchedEpisodeIds.Add(ep);
                importedFiles.Add((outcome.DestinationPath, plan.MatchedEpisodeIds));
                if (media.MediaType == MediaType.Series && job.EpisodeId is null && plan.MatchedEpisodeIds.Count == 0)
                {
                    unmatched++;
                    await AddUnmatchedRecordAsync(db, job.Id, plan.SourcePath, outcome.DestinationPath, ct);
                }
            }
        }
        if (!jobDeferred)
        {
            var tracked = await db.DownloadJobs.FirstAsync(j => j.Id == job.Id, ct);
            tracked.UpdatedAt = _time.GetUtcNow();
            if (jobFailed)
            {
                tracked.Status = DownloadStatus.Failed;
                tracked.ImportFailed = true;
                tracked.Error = "One or more files failed to import; see import records";
            }
            else
            {
                await MarkHaveFileAsync(db, job, media, matchedEpisodeIds, ct);
                await LinkImportedFilesAsync(db, media, importedFiles, ct);
                if (unmatched > 0)
                {
                    tracked.Status = DownloadStatus.Completed;
                    tracked.ImportFailed = false;
                    tracked.Error = $"{unmatched} file(s) imported but unmatched to any episode; see import records";
                }
                else
                {
                    tracked.Status = DownloadStatus.Imported;
                    tracked.ImportFailed = false;
                    tracked.Error = null;
                }
            }
        }
        await db.SaveChangesAsync(ct);
        return new ImportPreviewDto(preview.Id, job.Id, preview.CreatedAt, stored, preview.Fingerprint);
    }

    private async Task<(DownloadJob Job, List<ImportPlanDto> Plans)> PlanJobAsync(OneArrDbContext db, int jobId, CancellationToken ct)
    {
        var job = await db.DownloadJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw new ArgumentException("Download job not found");
        // Only a download that completed can be previewed: Completed, or Failed with ImportFailed
        // (a completed download whose import failed, previewed for retry). Queued, downloading,
        // missing, download-failed, and already-imported jobs are refused.
        if (job.Status != DownloadStatus.Completed && !(job.Status == DownloadStatus.Failed && job.ImportFailed))
            throw new ArgumentException("Only a completed download can be previewed");
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
        var files = ListFiles(await ResolveContentPathAsync(job, torrentRoot, ct), torrentRoot);
        var plans = new List<ImportPlanDto>();
        var episodes = media.MediaType == MediaType.Series
            ? await db.Episodes.AsNoTracking().Where(e => e.MediaItemId == media.Id).ToListAsync(ct)
            : new List<Episode>();
        var rename = await _settings.GetBoolAsync(SettingKeys.RenameOnImport, false, ct);
        var template = rename
            ? await _settings.GetAsync(media.MediaType == MediaType.Movie
                ? SettingKeys.MovieNamingTemplate : SettingKeys.SeriesNamingTemplate, ct)
            : null;
        template ??= media.MediaType == MediaType.Movie ? MediaNaming.MovieTemplate : MediaNaming.EpisodeTemplate;
        foreach (var file in files)
        {
            plans.Add(await PlanFileAsync(file, destRoot, relFolder, torrentRoot,
                media, job, episodes, rename, template, ct));
        }
        // Two distinct source files must never target one filename, even when
        // the destination did not exist during preview.
        var conflicts = plans.Where(p => p.DestinationPath is not null)
            .GroupBy(p => p.DestinationPath!, OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Where(g => g.Count() > 1).SelectMany(g => g).Select(p => p.SourcePath).ToHashSet();
        if (conflicts.Count > 0)
            plans = plans.Select(p => conflicts.Contains(p.SourcePath)
                ? Reject(p.SourcePath, p.DestinationPath, p.SizeBytes, p.LastWriteUtcTicks,
                    p.MatchedEpisodeIds, p.MatchedEpisodeLabels,
                    "Multiple source files would resolve to the same destination; change the naming template", conflict: true)
                : p).ToList();
        if (plans.Count == 0)
            throw new ArgumentException("No media files found in the completed download; nothing to preview");
        return (job, plans);
    }

    private async Task<ImportPlanDto> PlanFileAsync(string file, string destRoot, string relFolder,
        string torrentRoot, MediaItem media, DownloadJob job, List<Episode> episodes,
        bool rename, string template, CancellationToken ct)
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
        // A series pack file with no episode match must say so in the preview: it will import
        // without linking an episode, and the job will stay unresolved rather than fully Imported.
        var unmatchedNote = media.MediaType == MediaType.Series && job.EpisodeId is null && labels.Count == 0
            ? " (no episode matched; will import without linking an episode)" : "";
        string? dest = null;
        try
        {
            var source = SafeImporter.CanonicalUnder(torrentRoot, file);
            SafeImporter.RequireNoLinksBelow(torrentRoot, source);
            var fileName = Path.GetFileName(source);
            if (rename)
            {
                if (media.MediaType == MediaType.Series && labels.Count != 1)
                    throw new ArgumentException("Episode naming requires exactly one matched episode; review this pack manually");
                var episode = media.MediaType == MediaType.Series
                    ? episodes.Single(e => e.Id == labels[0]) : null;
                var parsed = new ReleaseParser().Parse(job.ReleaseTitle);
                var quality = parsed.Resolution is int resolution ? $"{resolution}p" : null;
                fileName = MediaNaming.Preview(new MediaNaming.Request(
                    media.MediaType == MediaType.Movie ? "movie" : "series",
                    template, media.Title, fileName, media.Year,
                    episode?.SeasonNumber, episode?.EpisodeNumber, episode?.Title, quality));
            }
            dest = SafeImporter.PlanDestination(destRoot, relFolder, fileName);
            SafeImporter.RequireNoLinksBelow(destRoot, dest);
            var info = new FileInfo(source);
            if (!info.Exists) return Reject(file, dest, 0, 0, labels, labelTexts, "Source file does not exist");
            if (info.LinkTarget is not null) return Reject(file, dest, info.Length, info.LastWriteTimeUtc.Ticks, labels, labelTexts, "Refusing to import a symbolic link");
            if (_quietPeriod > TimeSpan.Zero && DateTime.UtcNow - info.LastWriteTimeUtc < _quietPeriod)
                return new ImportPlanDto(file, dest, info.Length, info.LastWriteTimeUtc.Ticks, Path.GetExtension(file), "defer", $"Source changed within the last {_quietPeriod.TotalSeconds:0} s; will retry", false, SafeImporter.IsSameVolume(source, dest), labels, labelTexts);
            if (File.Exists(dest))
            {
                if (await SafeImporter.SameContentAsync(source, dest, ct))
                    return new ImportPlanDto(file, dest, info.Length, info.LastWriteTimeUtc.Ticks, Path.GetExtension(file), "skip", "Already imported with identical content" + unmatchedNote, false, SafeImporter.IsSameVolume(source, dest), labels, labelTexts);
                return Reject(file, dest, info.Length, info.LastWriteTimeUtc.Ticks, labels, labelTexts, "Destination exists with different content; not overwriting", conflict: true);
            }
            var sameVolume = SafeImporter.IsSameVolume(source, dest);
            if (!sameVolume && CopySpaceGuard.CopyProblem(dest, info.Length) is string spaceError)
                return Reject(file, dest, info.Length, info.LastWriteTimeUtc.Ticks,
                    labels, labelTexts, spaceError);
            return new ImportPlanDto(file, dest, info.Length, info.LastWriteTimeUtc.Ticks, Path.GetExtension(file), sameVolume ? "hardlink" : "copy", (sameVolume ? "Hardlink on the same volume" : "Copy to a different volume (verified, SHA-256)") + unmatchedNote, false, sameVolume, labels, labelTexts);
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
            .Select(p => $"{p.SourcePath}|{p.SizeBytes}|{p.LastWriteUtcTicks}|{p.DestinationPath}|{p.PlannedAction}|{string.Join(",", p.MatchedEpisodeIds)}");
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

    private async Task<string> ResolveContentPathAsync(DownloadJob job, string torrentRoot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.ContentPath))
            throw new ArgumentException("Download content path is empty");
        if (job.DownloadClientId is not int clientId)
            return job.ContentPath;
        var json = await _settings.GetAsync(DownloadPathMapper.SettingsKey(clientId), ct);
        return string.IsNullOrWhiteSpace(json)
            ? job.ContentPath
            : DownloadPathMapper.Map(DownloadPathMapper.Read(json), job.ContentPath, torrentRoot);
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
