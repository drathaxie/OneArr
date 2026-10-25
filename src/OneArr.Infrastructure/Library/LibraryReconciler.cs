using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using OneArr.Domain;
using OneArr.Infrastructure.Persistence;

namespace OneArr.Infrastructure.Library;

/// <summary>One folder as seen by a scan. <see cref="Complete"/> is false when any part of it could not be read.</summary>
public sealed record RootReport(int Id, MediaType MediaType, string Path, LibraryRootState State, string? Message, int FilesFound,
    int SkippedReparsePoints, int UnreadableFolders, bool Complete, string? IncompleteReason);

/// <summary>A file to link. <see cref="MediaFileId"/> is null for a new file, or the existing row whose links change.</summary>
public sealed record PlannedLink(string Path, string Key, long SizeBytes, MediaType MediaType, int MediaItemId, List<int> EpisodeIds, string Method, int? Resolution, int? MediaFileId);

/// <summary>A previously linked file confirmed absent from a readable folder.</summary>
public sealed record PlannedMissing(string Path, string Key, MediaType MediaType, int MediaItemId, int MediaFileId);

/// <summary>A previously linked file whose status cannot be decided. Its availability is kept as it was, and automation pauses.</summary>
public sealed record PlannedUnknown(string Path, string Key, MediaType MediaType, int MediaItemId, int MediaFileId, string Reason);

public sealed record ReviewItem(string Path, MediaType MediaType, string Reason, List<int> CandidateIds);

/// <summary>The reviewed result of a scan. Storing it is the only write a preview makes, and it is never applied without revalidation.</summary>
public sealed record ScanPlan(
    DateTimeOffset CreatedUtc,
    IReadOnlyList<RootReport> Roots,
    IReadOnlyList<PlannedLink> Links,
    IReadOnlyList<PlannedMissing> Missing,
    IReadOnlyList<PlannedUnknown> Unknown,
    IReadOnlyList<ReviewItem> Ambiguous,
    IReadOnlyList<ReviewItem> Unmatched,
    int AlreadyLinked,
    int FilesUnderUnavailableRoots,
    bool Complete,
    IReadOnlyList<int> EvaluatedItemIds,
    IReadOnlyList<string> RootKeys,
    string MappingsHash,
    IReadOnlyList<string> Warnings,
    int AmbiguousCount,
    int UnmatchedCount);

public sealed record ScanPlanResponse(int PlanId, ScanPlan Plan);

/// <summary>What the last apply established. Automatic grabbing reads this, so it is written only by apply.</summary>
public sealed record ReviewState(DateTimeOffset? CompleteApplyUtc, int AmbiguousCount, int UnmatchedCount, bool UnmatchedAcknowledged, string MappingsHash, IReadOnlyList<string> RootKeys);

public sealed record ApplyResult(int Linked, int Relinked, int MarkedMissing, int MarkedUnknown, int MarkedInaccessible, int ReviewedItems, bool Complete,
    IReadOnlyList<string> Skipped, AutomationGate Automation);

public sealed record AutomationGate(bool Allowed, string Reason);

/// <summary>An existing file row as a scan needs it. Read without change tracking or navigation graphs.</summary>
internal sealed record KnownFile(int Id, string Path, string PathKey, long SizeBytes, int? MediaItemId, MediaFileStatus Status, List<int> EpisodeIds);

/// <summary>
/// Connects files on disk to catalog items. Preview is read-only for media: it evaluates the disk and the catalog, and stores the reviewed result.
/// Apply runs a fresh evaluation and writes only entries that still match what was reviewed. It never moves, renames, copies, links or deletes files.
/// Automatic grabbing is allowed only after a complete apply has reviewed every monitored catalog entry, and only while the folders are still reachable.
/// </summary>
public sealed class LibraryReconciler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int KeptPlans = 3;
    private const int Batch = 500;
    /// <summary>Review lists are capped so a large library cannot produce a huge plan. The counts stay exact.</summary>
    public const int DefaultReviewListLimit = 1000;

    private readonly IDbContextFactory<OneArrDbContext> _db;
    private readonly ILibraryFileSystem _fs;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly int _reviewListLimit;

    public LibraryReconciler(IDbContextFactory<OneArrDbContext> db, ILibraryFileSystem fs, TimeProvider time, int reviewListLimit = DefaultReviewListLimit)
    {
        _db = db;
        _fs = fs;
        _time = time;
        _reviewListLimit = reviewListLimit;
    }

    public async Task<ScanPlanResponse> PreviewAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // The item-id list is needed only by apply, which evaluates again. Keep it out of the stored plan and the response.
            var plan = (await EvaluateAsync(ct)) with { EvaluatedItemIds = [] };
            await using var db = await _db.CreateDbContextAsync(ct);
            var row = new LibraryScanPlan { CreatedUtc = plan.CreatedUtc, PlanJson = JsonSerializer.Serialize(plan, Json) };
            db.LibraryScanPlans.Add(row);
            await SaveAsync(db, ct);
            await db.LibraryScanPlans.Where(p => p.Id <= row.Id - KeptPlans).ExecuteDeleteAsync(ct);
            return new ScanPlanResponse(row.Id, plan);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ApplyResult> ApplyAsync(int planId, IReadOnlyList<string>? excludePaths, bool acknowledgeUnmatched, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var db = await _db.CreateDbContextAsync(ct);
            // Adding thousands of entities with change detection on re-scans everything tracked on each Add, which is quadratic.
            // SaveChanges still detects changes itself, so the writes are unchanged.
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            var stored = await db.LibraryScanPlans.FirstOrDefaultAsync(p => p.Id == planId, ct)
                ?? throw new KeyNotFoundException("Scan preview not found. Run a new preview.");
            if (stored.AppliedUtc is not null) throw new InvalidOperationException("This preview was already applied. Run a new preview.");

            var reviewed = JsonSerializer.Deserialize<ScanPlan>(stored.PlanJson, Json)!;
            var excluded = (excludePaths ?? []).Select(p => LibraryPaths.Key(p, _fs.Windows)).ToHashSet();
            var fresh = await EvaluateAsync(ct);
            var freshLinks = fresh.Links.ToDictionary(l => l.Key);
            var freshMissing = fresh.Missing.ToDictionary(m => m.Key);
            var freshUnknown = fresh.Unknown.ToDictionary(u => u.Key);
            var skipped = new List<string>();

            var toLink = new List<PlannedLink>();
            foreach (var link in reviewed.Links)
            {
                if (excluded.Contains(link.Key)) { skipped.Add($"{link.Path}: excluded"); continue; }
                if (!freshLinks.TryGetValue(link.Key, out var current) || !SameLink(link, current))
                {
                    skipped.Add($"{link.Path}: changed since the preview. Run a new preview");
                    continue;
                }
                toLink.Add(current);
            }

            var toMiss = new List<PlannedMissing>();
            foreach (var missing in reviewed.Missing)
            {
                if (excluded.Contains(missing.Key)) { skipped.Add($"{missing.Path}: excluded"); continue; }
                if (!freshMissing.TryGetValue(missing.Key, out var current) || current.MediaFileId != missing.MediaFileId)
                {
                    skipped.Add($"{missing.Path}: found again or changed since the preview");
                    continue;
                }
                toMiss.Add(current);
            }

            var toUnknown = new List<PlannedUnknown>();
            foreach (var unknown in reviewed.Unknown)
            {
                if (!freshUnknown.TryGetValue(unknown.Key, out var current) || current.MediaFileId != unknown.MediaFileId) continue;
                toUnknown.Add(current);
            }

            var now = _time.GetUtcNow();
            var affectedItems = new HashSet<int>();
            var affectedEpisodes = new HashSet<int>();
            // Items and episodes that lost a link during this apply. They are re-checked even when no link row remains.
            var formerlyLinkedItems = new HashSet<int>();
            var formerlyLinkedEpisodes = new HashSet<int>();
            var enabledRoots = await db.LibraryRoots.Where(r => r.Enabled).ToListAsync(ct);

            var rowIds = toLink.Where(l => l.MediaFileId is not null).Select(l => l.MediaFileId!.Value)
                .Concat(toMiss.Select(m => m.MediaFileId)).Concat(toUnknown.Select(u => u.MediaFileId)).ToList();
            var rows = await LoadRowsAsync(db, rowIds, ct);

            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var relinked = 0;
            // Written in batches. Each batch is saved and its new entities are detached, so tracked memory stays bounded by the batch.
            foreach (var chunk in toLink.Chunk(Batch))
            {
                var batchEntities = new List<object>();
                foreach (var link in chunk)
                {
                    MediaFile row;
                    if (link.MediaFileId is int existingId)
                    {
                        row = rows[existingId];
                        if (row.MediaItemId is int oldItem && oldItem != link.MediaItemId)
                        {
                            affectedItems.Add(oldItem);
                            formerlyLinkedItems.Add(oldItem);
                        }
                        affectedEpisodes.UnionWith(row.EpisodeLinks.Select(l => l.EpisodeId));
                        // Only the difference changes. Removing and re-adding the same (file, episode) key in one save is not safe.
                        var wanted = link.EpisodeIds.ToHashSet();
                        var kept = row.EpisodeLinks.Select(l => l.EpisodeId).Where(wanted.Contains).ToHashSet();
                        var dropped = row.EpisodeLinks.Where(l => !wanted.Contains(l.EpisodeId)).ToList();
                        formerlyLinkedEpisodes.UnionWith(dropped.Select(l => l.EpisodeId));
                        db.MediaFileEpisodes.RemoveRange(dropped);
                        foreach (var episodeId in wanted.Where(e => !kept.Contains(e)))
                            row.EpisodeLinks.Add(new MediaFileEpisode { EpisodeId = episodeId });
                        affectedEpisodes.UnionWith(wanted);
                        relinked++;
                    }
                    else
                    {
                        row = new MediaFile { Path = link.Path, PathKey = link.Key, FirstSeenUtc = now };
                        db.MediaFiles.Add(row);
                        batchEntities.Add(row);
                        foreach (var episodeId in link.EpisodeIds)
                        {
                            var join = new MediaFileEpisode { EpisodeId = episodeId };
                            row.EpisodeLinks.Add(join);
                            batchEntities.Add(join);
                            affectedEpisodes.Add(episodeId);
                        }
                    }

                    row.SizeBytes = link.SizeBytes;
                    row.Status = MediaFileStatus.Present;
                    row.MediaItemId = link.MediaItemId;
                    row.LibraryRootId = RootIdFor(link.Key, enabledRoots, _fs.Windows);
                    row.MatchMethod = link.Method;
                    row.Resolution = link.Resolution;
                    row.LastVerifiedUtc = now;
                    affectedItems.Add(link.MediaItemId);
                }

                await SaveAsync(db, ct);
                foreach (var entity in batchEntities) db.Entry(entity).State = EntityState.Detached;
            }

            foreach (var missing in toMiss)
            {
                var row = rows[missing.MediaFileId];
                row.Status = MediaFileStatus.Missing;
                row.LastVerifiedUtc = now;
                affectedItems.Add(missing.MediaItemId);
                affectedEpisodes.UnionWith(row.EpisodeLinks.Select(l => l.EpisodeId));
            }

            // Undecidable files keep their HasFile. Only the status is recorded, so the next complete scan can settle it.
            foreach (var unknown in toUnknown)
            {
                var row = rows[unknown.MediaFileId];
                row.Status = MediaFileStatus.Unknown;
                affectedItems.Add(unknown.MediaItemId);
                affectedEpisodes.UnionWith(row.EpisodeLinks.Select(l => l.EpisodeId));
            }

            // Files under a root that is offline are shown as inaccessible. They are never treated as missing.
            var unavailableKeys = fresh.Roots.Where(r => r.State != LibraryRootState.Accessible)
                .Select(r => LibraryPaths.Key(r.Path, _fs.Windows)).ToList();
            var markedInaccessible = 0;
            if (unavailableKeys.Count > 0)
            {
                var offlineRows = await db.MediaFiles.Where(f => f.Status != MediaFileStatus.Inaccessible).ToListAsync(ct);
                foreach (var row in offlineRows.Where(r => unavailableKeys.Any(k => LibraryPaths.IsWithin(r.PathKey, k, _fs.Windows))))
                {
                    row.Status = MediaFileStatus.Inaccessible;
                    markedInaccessible++;
                }
            }

            foreach (var root in enabledRoots)
            {
                var report = fresh.Roots.FirstOrDefault(r => r.Id == root.Id);
                if (report is null) continue;
                root.LastState = report.State;
                root.LastMessage = report.Message ?? report.IncompleteReason;
                root.LastCheckedUtc = now;
                root.LastScanComplete = report.Complete && report.State == LibraryRootState.Accessible;
                if (root.LastScanComplete) root.LastSuccessfulScanUtc = now;
            }

            await SaveAsync(db, ct);
            // The bulk writes are saved. Drop them from tracking so later change scans cover only what recalculation touches.
            db.ChangeTracker.Clear();
            await RecalculateAsync(db, affectedItems, affectedEpisodes, formerlyLinkedItems, formerlyLinkedEpisodes, ct);

            // Items are reviewed only when every enabled folder was read completely. Zero folders never counts as reviewed.
            var reviewedItems = 0;
            if (fresh.Complete)
            {
                foreach (var batch in fresh.EvaluatedItemIds.Chunk(Batch))
                {
                    reviewedItems += await db.MediaItems.Where(m => batch.Contains(m.Id) && m.LibraryReviewedUtc == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.LibraryReviewedUtc, now), ct);
                }
            }

            await SaveReviewStateAsync(db, new ReviewState(
                fresh.Complete ? now : null,
                fresh.AmbiguousCount,
                fresh.UnmatchedCount,
                acknowledgeUnmatched && fresh.UnmatchedCount > 0,
                fresh.MappingsHash,
                fresh.RootKeys), ct);

            await db.LibraryScanPlans.Where(p => p.Id == planId).ExecuteUpdateAsync(s => s.SetProperty(p => p.AppliedUtc, now), ct);
            await SaveAsync(db, ct);
            await tx.CommitAsync(ct);

            var gate = await CheckAutomationAsync(ct);
            return new ApplyResult(toLink.Count - relinked, relinked, toMiss.Count, toUnknown.Count, markedInaccessible, reviewedItems, fresh.Complete, skipped, gate);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Automatic grabbing is allowed only when every monitored catalog entry has been reviewed by a complete apply, every enabled folder
    /// was read completely by that apply, nothing is ambiguous, unmatched files were acknowledged, the folders and path mappings are unchanged,
    /// and every folder is reachable right now. Anything else fails closed. A migration never satisfies these conditions.
    /// </summary>
    public async Task<AutomationGate> CheckAutomationAsync(CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var monitoredCount = await db.MediaItems.CountAsync(m => m.Monitored, ct);
        var roots = await db.LibraryRoots.AsNoTracking().Where(r => r.Enabled).OrderBy(r => r.Id).ToListAsync(ct);

        if (roots.Count == 0)
        {
            return monitoredCount == 0
                ? new AutomationGate(true, "No monitored catalog entries")
                : new AutomationGate(false, "Add your library folders and apply a complete scan. Catalog entries have not been checked against your disk, so existing media would be downloaded again.");
        }

        var unreviewed = await db.MediaItems.CountAsync(m => m.Monitored && m.LibraryReviewedUtc == null, ct);
        if (unreviewed > 0)
            return new AutomationGate(false, $"{unreviewed} monitored catalog entries have not been checked against your library. Preview and apply a complete scan first.");

        foreach (var root in roots)
        {
            if (!root.LastScanComplete || root.LastSuccessfulScanUtc is null)
                return new AutomationGate(false, $"'{root.Path}' has no complete scan. Preview and apply a scan that reads the whole folder");
        }

        var state = await LoadReviewStateAsync(db, ct);
        if (state?.CompleteApplyUtc is null)
            return new AutomationGate(false, "No complete library scan has been applied yet");

        var currentKeys = roots.Select(r => LibraryPaths.Key(r.Path, _fs.Windows)).Order(StringComparer.Ordinal).ToList();
        if (!currentKeys.SequenceEqual(state.RootKeys.Order(StringComparer.Ordinal)))
            return new AutomationGate(false, "The library folders changed since the last complete scan. Preview and apply again");

        var mappings = await db.PathMappings.AsNoTracking().ToListAsync(ct);
        if (state.MappingsHash != MappingsHash(mappings))
            return new AutomationGate(false, "The path mappings changed since the last complete scan. Preview and apply again");

        if (state.AmbiguousCount > 0)
            return new AutomationGate(false, $"{state.AmbiguousCount} file(s) match more than one catalog entry. Resolve them before automatic grabbing starts");

        if (state.UnmatchedCount > 0 && !state.UnmatchedAcknowledged)
            return new AutomationGate(false, $"{state.UnmatchedCount} existing file(s) are not matched to a catalog entry. Review them and apply with acknowledgement");

        foreach (var root in roots)
        {
            var check = await _fs.CheckRootAsync(root.Path, ct);
            if (check.State != LibraryRootState.Accessible)
                return new AutomationGate(false, $"'{root.Path}' is not reachable ({check.Message}). Automatic searching is paused so nothing is treated as missing");
        }
        return new AutomationGate(true, "Library reconciled and every folder is reachable");
    }

    private async Task<ScanPlan> EvaluateAsync(CancellationToken ct)
    {
        var windows = _fs.Windows;
        var now = _time.GetUtcNow();
        var warnings = new List<string>();
        await using var db = await _db.CreateDbContextAsync(ct);

        var roots = await db.LibraryRoots.AsNoTracking().Where(r => r.Enabled).OrderBy(r => r.Id).ToListAsync(ct);
        var mappings = await db.PathMappings.AsNoTracking().ToListAsync(ct);
        var movieRows = await db.MediaItems.AsNoTracking().Where(m => m.MediaType == MediaType.Movie).ToListAsync(ct);
        var seriesRows = await db.MediaItems.AsNoTracking().Where(m => m.MediaType == MediaType.Series).Include(m => m.Episodes).ToListAsync(ct);
        // Read as flat records, not entities with their navigation graph, so a large library stays small and cancellation is quicker.
        var knownRows = await db.MediaFiles.AsNoTracking()
            .Select(f => new { f.Id, f.Path, f.PathKey, f.SizeBytes, f.MediaItemId, f.Status }).ToListAsync(ct);
        var linkRows = await db.MediaFileEpisodes.AsNoTracking().Select(l => new { l.MediaFileId, l.EpisodeId }).ToListAsync(ct);
        var episodesByFile = linkRows.GroupBy(l => l.MediaFileId).ToDictionary(g => g.Key, g => g.Select(l => l.EpisodeId).ToList());
        var known = knownRows.Select(f => new KnownFile(f.Id, f.Path, f.PathKey, f.SizeBytes, f.MediaItemId, f.Status, episodesByFile.GetValueOrDefault(f.Id) ?? [])).ToList();

        var unmapped = 0;
        string? LegacyKey(string? legacy)
        {
            if (legacy is null) return null;
            var mapped = LibraryPaths.MapLegacy(legacy, mappings, windows);
            if (mapped is null) { unmapped++; return null; }
            return LibraryPaths.Key(mapped, windows);
        }

        // Long synchronous stretches below do not await, so check for cancellation before and after them.
        ct.ThrowIfCancellationRequested();
        var movies = movieRows.Select(m => new CatalogMovie(m.Id, m.Title, m.Year, m.Provider == "tmdb" ? m.ProviderId : null, LegacyKey(m.LegacyPath))).ToList();
        var series = seriesRows.Select(s => new CatalogSeries(
            s.Id, s.Title, s.Year, s.Provider == "tmdb" ? s.ProviderId : null, LegacyKey(s.LegacyPath),
            s.Episodes.Select(e => new CatalogEpisode(e.Id, e.SeasonNumber, e.EpisodeNumber)).ToList())).ToList();
        var index = new CatalogIndex(movies, series, windows);
        ct.ThrowIfCancellationRequested();
        var typeById = movieRows.ToDictionary(m => m.Id, _ => MediaType.Movie);
        foreach (var s in seriesRows) typeById[s.Id] = MediaType.Series;

        if (unmapped > 0)
            warnings.Add($"{unmapped} catalog entr{(unmapped == 1 ? "y has a Sonarr/Radarr path" : "ies have Sonarr/Radarr paths")} with no path mapping. They are matched by title and year only until you add a mapping.");

        var reports = new List<RootReport>();
        var accessible = new List<(string Path, string Key, List<string> Unreadable)>();
        var discovered = new Dictionary<string, (CandidateFile File, MediaType Type)>();
        var filesUnderUnavailable = 0;
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            var rootKey = LibraryPaths.Key(root.Path, windows);
            var check = await _fs.CheckRootAsync(root.Path, ct);
            if (check.State != LibraryRootState.Accessible)
            {
                reports.Add(new RootReport(root.Id, root.MediaType, root.Path, check.State, check.Message, 0, 0, 0, Complete: false, IncompleteReason: check.Message));
                filesUnderUnavailable += known.Count(f => LibraryPaths.IsWithin(f.PathKey, rootKey, windows));
                warnings.Add($"'{root.Path}' is unavailable ({check.Message}). Nothing under it was treated as missing.");
                continue;
            }

            var scan = await _fs.EnumerateAsync(root.Path, ct);
            reports.Add(new RootReport(root.Id, root.MediaType, root.Path, LibraryRootState.Accessible, null, scan.Files.Count,
                scan.SkippedReparsePoints, scan.UnreadableFolders.Count, scan.Complete, scan.IncompleteReason));
            accessible.Add((root.Path, rootKey, scan.UnreadableFolders.Select(f => LibraryPaths.Key(f, windows)).ToList()));
            if (!scan.Complete)
                warnings.Add($"'{root.Path}' was only partly read: {scan.IncompleteReason}. Files in the unread part keep their last known status and automatic grabbing waits for a complete scan.");
            if (scan.SkippedReparsePoints > 0)
                warnings.Add($"Skipped {scan.SkippedReparsePoints} junction or symlink entries under '{root.Path}'. Their contents are not followed.");
            foreach (var file in scan.Files)
                discovered.TryAdd(LibraryPaths.Key(file.FullPath, windows), (file, root.MediaType));
        }

        ct.ThrowIfCancellationRequested();
        var knownByKey = known.ToDictionary(f => f.PathKey);
        var links = new List<PlannedLink>();
        var ambiguous = new List<ReviewItem>();
        var unmatched = new List<ReviewItem>();
        var alreadyLinked = 0;
        var ambiguousCount = 0;
        var unmatchedCount = 0;
        foreach (var (key, (file, type)) in discovered)
        {
            ct.ThrowIfCancellationRequested();
            var match = LibraryMatcher.Match(file, type, index);
            switch (match.Outcome)
            {
                case MatchOutcome.Linked:
                    var episodeIds = match.EpisodeIds?.ToList() ?? [];
                    knownByKey.TryGetValue(key, out var existing);
                    if (existing is null)
                    {
                        links.Add(new PlannedLink(file.FullPath, key, file.SizeBytes, type, match.MediaItemId!.Value, episodeIds, match.Method!, match.Resolution, null));
                    }
                    else if (existing.MediaItemId == match.MediaItemId && existing.Status == MediaFileStatus.Present
                             && existing.SizeBytes == file.SizeBytes && existing.EpisodeIds.OrderBy(x => x).SequenceEqual(episodeIds.OrderBy(x => x)))
                    {
                        alreadyLinked++;
                    }
                    else
                    {
                        links.Add(new PlannedLink(file.FullPath, key, file.SizeBytes, type, match.MediaItemId!.Value, episodeIds, match.Method!, match.Resolution, existing.Id));
                    }
                    break;
                case MatchOutcome.Ambiguous:
                    ambiguousCount++;
                    if (ambiguous.Count < _reviewListLimit) ambiguous.Add(new ReviewItem(file.FullPath, type, match.Reason, match.CandidateIds!.ToList()));
                    break;
                default:
                    unmatchedCount++;
                    if (unmatched.Count < _reviewListLimit) unmatched.Add(new ReviewItem(file.FullPath, type, match.Reason, []));
                    break;
            }
        }

        var missing = new List<PlannedMissing>();
        var unknown = new List<PlannedUnknown>();
        foreach (var row in known)
        {
            ct.ThrowIfCancellationRequested();
            if (row.MediaItemId is not int itemId || !typeById.TryGetValue(itemId, out var type)) continue;
            var scope = accessible.FirstOrDefault(a => LibraryPaths.IsWithin(row.PathKey, a.Key, windows));
            if (scope.Key is null) continue;
            if (discovered.ContainsKey(row.PathKey) || row.Status == MediaFileStatus.Missing) continue;

            // A file inside a folder that could not be read is undecidable, not missing.
            var unreadable = scope.Unreadable.Any(u => row.PathKey == u || LibraryPaths.IsWithin(row.PathKey, u, windows));
            if (unreadable)
            {
                if (row.Status != MediaFileStatus.Unknown)
                    unknown.Add(new PlannedUnknown(row.Path, row.PathKey, type, itemId, row.Id, "Its folder could not be read during this scan"));
                continue;
            }

            var probe = await _fs.ProbeFileAsync(row.Path, ct);
            switch (probe.State)
            {
                case FileProbeState.Exists:
                    // Present but not enumerated, for example behind a junction. Not missing.
                    break;
                case FileProbeState.Missing:
                    missing.Add(new PlannedMissing(row.Path, row.PathKey, type, itemId, row.Id));
                    break;
                default:
                    if (row.Status != MediaFileStatus.Unknown)
                        unknown.Add(new PlannedUnknown(row.Path, row.PathKey, type, itemId, row.Id, probe.Message ?? "The disk could not decide"));
                    break;
            }
        }

        foreach (var scope in accessible)
        {
            ct.ThrowIfCancellationRequested();
            var recheck = await _fs.CheckRootAsync(scope.Path, ct);
            if (recheck.State == LibraryRootState.Accessible) continue;

            // The folder went offline during the scan. Files already seen there stay linked, but nothing is judged missing or unknown under it.
            var root = roots.First(r => LibraryPaths.Key(r.Path, windows) == scope.Key);
            var reportIndex = reports.FindIndex(r => r.Id == root.Id);
            reports[reportIndex] = reports[reportIndex] with { State = recheck.State, Message = recheck.Message, Complete = false, IncompleteReason = "went offline during the scan" };
            missing.RemoveAll(m => LibraryPaths.IsWithin(m.Key, scope.Key, windows));
            unknown.RemoveAll(u => LibraryPaths.IsWithin(u.Key, scope.Key, windows));
            filesUnderUnavailable += known.Count(f => LibraryPaths.IsWithin(f.PathKey, scope.Key, windows));
            warnings.Add($"'{root.Path}' went offline during the scan ({recheck.Message}). Nothing under it was treated as missing.");
        }

        var itemIds = movieRows.Select(m => m.Id).Concat(seriesRows.Select(s => s.Id)).ToList();
        return new ScanPlan(now, reports, links, missing, unknown, ambiguous, unmatched, alreadyLinked, filesUnderUnavailable,
            Complete: roots.Count > 0 && reports.All(r => r.State == LibraryRootState.Accessible && r.Complete),
            EvaluatedItemIds: itemIds,
            RootKeys: roots.Select(r => LibraryPaths.Key(r.Path, windows)).ToList(),
            MappingsHash: MappingsHash(mappings),
            Warnings: warnings,
            AmbiguousCount: ambiguousCount,
            UnmatchedCount: unmatchedCount);
    }

    private static async Task<Dictionary<int, MediaFile>> LoadRowsAsync(OneArrDbContext db, IEnumerable<int> ids, CancellationToken ct)
    {
        var result = new Dictionary<int, MediaFile>();
        foreach (var batch in ids.Distinct().Chunk(Batch))
        {
            foreach (var row in await db.MediaFiles.Include(f => f.EpisodeLinks).Where(f => batch.Contains(f.Id)).ToListAsync(ct))
                result[row.Id] = row;
        }
        return result;
    }

    /// <summary>
    /// HasFile turns on when a present file is linked. It turns off only when every linked file is confirmed missing and no completed download
    /// import exists. Unknown or inaccessible files leave HasFile as it is, so a disk fault can never make media look missing.
    /// </summary>
    private static async Task RecalculateAsync(OneArrDbContext db, HashSet<int> itemIds, HashSet<int> episodeIds,
        HashSet<int> formerlyLinkedItems, HashSet<int> formerlyLinkedEpisodes, CancellationToken ct)
    {
        foreach (var batch in episodeIds.Chunk(Batch))
        {
            var episodes = await db.Episodes.Where(e => batch.Contains(e.Id)).ToListAsync(ct);
            var links = await db.MediaFileEpisodes.AsNoTracking().Include(l => l.MediaFile)
                .Where(l => batch.Contains(l.EpisodeId)).ToListAsync(ct);
            var imported = (await db.DownloadJobs.AsNoTracking()
                .Where(j => j.Status == DownloadStatus.Imported && j.EpisodeId != null && batch.Contains(j.EpisodeId.Value))
                .Select(j => j.EpisodeId!.Value).ToListAsync(ct)).ToHashSet();
            foreach (var ep in episodes)
            {
                var statuses = links.Where(l => l.EpisodeId == ep.Id).Select(l => l.MediaFile!.Status).ToList();
                if (statuses.Contains(MediaFileStatus.Present)) ep.HasFile = true;
                else if (HasUndecidedStatus(statuses)) { /* keep the current value */ }
                else if ((statuses.Count > 0 || formerlyLinkedEpisodes.Contains(ep.Id)) && !imported.Contains(ep.Id)) ep.HasFile = false;
                itemIds.Add(ep.MediaItemId);
            }
            // Save first so the series check below reads the new episode flags.
            await SaveAsync(db, ct);
            db.ChangeTracker.Clear();
        }

        if (itemIds.Count == 0) return;
        foreach (var batch in itemIds.Chunk(Batch))
        {
            var items = await db.MediaItems.Where(m => batch.Contains(m.Id)).ToListAsync(ct);
            var movieIds = items.Where(m => m.MediaType == MediaType.Movie).Select(m => m.Id).ToHashSet();
            if (movieIds.Count > 0)
            {
                var rows = await db.MediaFiles.AsNoTracking().Where(f => f.MediaItemId != null && movieIds.Contains(f.MediaItemId.Value))
                    .Select(f => new { f.MediaItemId, f.Status }).ToListAsync(ct);
                var imported = (await db.DownloadJobs.AsNoTracking()
                    .Where(j => j.Status == DownloadStatus.Imported && j.MediaItemId != null && movieIds.Contains(j.MediaItemId.Value) && j.MediaType == MediaType.Movie)
                    .Select(j => j.MediaItemId!.Value).ToListAsync(ct)).ToHashSet();
                foreach (var movie in items.Where(m => m.MediaType == MediaType.Movie))
                {
                    var statuses = rows.Where(r => r.MediaItemId == movie.Id).Select(r => r.Status).ToList();
                    if (statuses.Contains(MediaFileStatus.Present)) movie.HasFile = true;
                    else if (HasUndecidedStatus(statuses)) { /* keep the current value */ }
                    else if ((statuses.Count > 0 || formerlyLinkedItems.Contains(movie.Id)) && !imported.Contains(movie.Id)) movie.HasFile = false;
                }
            }

            foreach (var series in items.Where(m => m.MediaType == MediaType.Series))
                series.HasFile = await db.Episodes.AnyAsync(e => e.MediaItemId == series.Id && e.HasFile, ct);
            await SaveAsync(db, ct);
            // This batch is saved. Stop tracking it, or every later save would re-scan all earlier batches.
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Saves after an explicit change scan. Automatic detection is switched off during apply for speed, and SaveChanges
    /// does not scan on its own when it is off, so the scan has to be requested here.
    /// </summary>
    private static Task<int> SaveAsync(OneArrDbContext db, CancellationToken ct)
    {
        db.ChangeTracker.DetectChanges();
        return db.SaveChangesAsync(ct);
    }

    private static bool HasUndecidedStatus(IEnumerable<MediaFileStatus> statuses) =>
        statuses.Any(s => s is MediaFileStatus.Unknown or MediaFileStatus.Inaccessible);

    private static async Task SaveReviewStateAsync(OneArrDbContext db, ReviewState state, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(state, Json);
        var row = await db.Settings.FindAsync([SettingKeys.LibraryReviewState], ct);
        if (row is null) db.Settings.Add(new AppSetting { Key = SettingKeys.LibraryReviewState, Value = json });
        else row.Value = json;
    }

    private static async Task<ReviewState?> LoadReviewStateAsync(OneArrDbContext db, CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKeys.LibraryReviewState, ct);
        return row is null ? null : JsonSerializer.Deserialize<ReviewState>(row.Value, Json);
    }

    /// <summary>Changes when any mapping changes, so a mapping edit forces a new complete scan.</summary>
    private static string MappingsHash(IEnumerable<PathMapping> mappings)
    {
        var text = string.Join("\n", mappings.OrderBy(m => m.MediaType).ThenBy(m => m.SourcePrefix, StringComparer.Ordinal)
            .Select(m => $"{m.MediaType}|{m.SourcePrefix}|{m.TargetPrefix}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The most specific enabled root that contains the file, so overlapping roots pick the deeper one.</summary>
    private static int? RootIdFor(string key, IEnumerable<LibraryRoot> roots, bool windows)
    {
        LibraryRoot? best = null;
        var bestLength = -1;
        foreach (var root in roots)
        {
            var rootKey = LibraryPaths.Key(root.Path, windows);
            if (!LibraryPaths.IsWithin(key, rootKey, windows) || rootKey.Length <= bestLength) continue;
            best = root;
            bestLength = rootKey.Length;
        }
        return best?.Id;
    }

    private static bool SameLink(PlannedLink a, PlannedLink b) =>
        a.MediaItemId == b.MediaItemId
        && a.MediaFileId == b.MediaFileId
        && a.SizeBytes == b.SizeBytes
        && a.EpisodeIds.OrderBy(x => x).SequenceEqual(b.EpisodeIds.OrderBy(x => x));
}
