using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OneArr.Domain;
using OneArr.Infrastructure.Persistence;

namespace OneArr.Infrastructure.Library;

public sealed record RootReport(int Id, MediaType MediaType, string Path, LibraryRootState State, string? Message, int FilesFound, int SkippedReparsePoints, int InaccessibleEntries);

/// <summary>A file to link. <see cref="MediaFileId"/> is null for a new file, or the existing row whose links change.</summary>
public sealed record PlannedLink(string Path, string Key, long SizeBytes, MediaType MediaType, int MediaItemId, List<int> EpisodeIds, string Method, int? Resolution, int? MediaFileId);

public sealed record PlannedMissing(string Path, string Key, MediaType MediaType, int MediaItemId, int MediaFileId);

public sealed record ReviewItem(string Path, MediaType MediaType, string Reason, List<int> CandidateIds);

/// <summary>The reviewed result of a scan. Storing it is the only write a preview makes, and it is never applied without revalidation.</summary>
public sealed record ScanPlan(
    DateTimeOffset CreatedUtc,
    IReadOnlyList<RootReport> Roots,
    IReadOnlyList<PlannedLink> Links,
    IReadOnlyList<PlannedMissing> Missing,
    IReadOnlyList<ReviewItem> Ambiguous,
    IReadOnlyList<ReviewItem> Unmatched,
    int AlreadyLinked,
    int FilesUnderUnavailableRoots,
    IReadOnlyList<string> Warnings);

public sealed record ScanPlanResponse(int PlanId, ScanPlan Plan);

public sealed record ApplyResult(int Linked, int Relinked, int MarkedMissing, int MarkedInaccessible, IReadOnlyList<string> Skipped);

public sealed record AutomationGate(bool Allowed, string Reason);

/// <summary>
/// Connects files on disk to catalog items. Preview is read-only for media: it evaluates the disk and the catalog, and stores the reviewed result.
/// Apply runs a fresh evaluation and writes only entries that still match what was reviewed. It never moves, renames, copies, links or deletes files.
/// </summary>
public sealed class LibraryReconciler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int KeptPlans = 20;

    private readonly IDbContextFactory<OneArrDbContext> _db;
    private readonly ILibraryFileSystem _fs;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LibraryReconciler(IDbContextFactory<OneArrDbContext> db, ILibraryFileSystem fs, TimeProvider time)
    {
        _db = db;
        _fs = fs;
        _time = time;
    }

    public async Task<ScanPlanResponse> PreviewAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var plan = await EvaluateAsync(ct);
            await using var db = await _db.CreateDbContextAsync(ct);
            var row = new LibraryScanPlan { CreatedUtc = plan.CreatedUtc, PlanJson = JsonSerializer.Serialize(plan, Json) };
            db.LibraryScanPlans.Add(row);
            await db.SaveChangesAsync(ct);
            await db.LibraryScanPlans.Where(p => p.Id <= row.Id - KeptPlans).ExecuteDeleteAsync(ct);
            return new ScanPlanResponse(row.Id, plan);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ApplyResult> ApplyAsync(int planId, IReadOnlyList<string>? excludePaths, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var db = await _db.CreateDbContextAsync(ct);
            var stored = await db.LibraryScanPlans.FirstOrDefaultAsync(p => p.Id == planId, ct)
                ?? throw new KeyNotFoundException("Scan preview not found. Run a new preview.");
            if (stored.AppliedUtc is not null) throw new InvalidOperationException("This preview was already applied. Run a new preview.");

            var reviewed = JsonSerializer.Deserialize<ScanPlan>(stored.PlanJson, Json)!;
            var excluded = (excludePaths ?? []).Select(p => LibraryPaths.Key(p, _fs.Windows)).ToHashSet();
            var fresh = await EvaluateAsync(ct);
            var freshLinks = fresh.Links.ToDictionary(l => l.Key);
            var freshMissing = fresh.Missing.ToDictionary(m => m.Key);
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

            var now = _time.GetUtcNow();
            var affectedItems = new HashSet<int>();
            var affectedEpisodes = new HashSet<int>();
            // Items and episodes that lost a link during this apply. They are re-checked even when no link row remains.
            var formerlyLinkedItems = new HashSet<int>();
            var formerlyLinkedEpisodes = new HashSet<int>();
            var enabledRoots = await db.LibraryRoots.Where(r => r.Enabled).ToListAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var relinked = 0;
            foreach (var link in toLink)
            {
                MediaFile row;
                if (link.MediaFileId is int existingId)
                {
                    row = await db.MediaFiles.Include(f => f.EpisodeLinks).FirstAsync(f => f.Id == existingId, ct);
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
                }

                row.SizeBytes = link.SizeBytes;
                row.Status = MediaFileStatus.Present;
                row.MediaItemId = link.MediaItemId;
                row.LibraryRootId = RootIdFor(link.Key, enabledRoots, _fs.Windows);
                row.MatchMethod = link.Method;
                row.Resolution = link.Resolution;
                row.LastVerifiedUtc = now;
                foreach (var episodeId in link.EpisodeIds)
                {
                    if (link.MediaFileId is null) row.EpisodeLinks.Add(new MediaFileEpisode { EpisodeId = episodeId });
                    affectedEpisodes.Add(episodeId);
                }
                affectedItems.Add(link.MediaItemId);
            }

            foreach (var missing in toMiss)
            {
                var row = await db.MediaFiles.Include(f => f.EpisodeLinks).FirstAsync(f => f.Id == missing.MediaFileId, ct);
                row.Status = MediaFileStatus.Missing;
                row.LastVerifiedUtc = now;
                affectedItems.Add(missing.MediaItemId);
                affectedEpisodes.UnionWith(row.EpisodeLinks.Select(l => l.EpisodeId));
            }

            // Files under a root that is offline are shown as inaccessible. They are never treated as missing.
            var unavailableKeys = fresh.Roots.Where(r => r.State != LibraryRootState.Accessible)
                .Select(r => LibraryPaths.Key(r.Path, _fs.Windows)).ToList();
            var markedInaccessible = 0;
            if (unavailableKeys.Count > 0)
            {
                var rows = await db.MediaFiles.Where(f => f.Status != MediaFileStatus.Inaccessible).ToListAsync(ct);
                foreach (var row in rows.Where(r => unavailableKeys.Any(k => LibraryPaths.IsWithin(r.PathKey, k, _fs.Windows))))
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
                root.LastMessage = report.Message;
                root.LastCheckedUtc = now;
                if (report.State == LibraryRootState.Accessible) root.LastSuccessfulScanUtc = now;
            }

            await db.SaveChangesAsync(ct);
            await RecalculateAsync(db, affectedItems, affectedEpisodes, formerlyLinkedItems, formerlyLinkedEpisodes, ct);
            stored.AppliedUtc = now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new ApplyResult(toLink.Count - relinked, relinked, toMiss.Count, markedInaccessible, skipped);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Automatic grabbing waits until every enabled library folder has been scanned and applied, and is reachable right now.
    /// Otherwise existing media that OneArr has not recognised yet would look missing and be downloaded again.
    /// </summary>
    public async Task<AutomationGate> CheckAutomationAsync(CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var roots = await db.LibraryRoots.AsNoTracking().Where(r => r.Enabled).OrderBy(r => r.Id).ToListAsync(ct);
        if (roots.Count == 0)
        {
            var hasLegacy = await db.MediaItems.AnyAsync(m => m.LegacyPath != null, ct);
            return hasLegacy
                ? new AutomationGate(false, "Add your library folders and run a scan first. Existing media is not recognised yet, so it would be downloaded again.")
                : new AutomationGate(true, "No library folders configured");
        }

        foreach (var root in roots)
        {
            if (root.LastSuccessfulScanUtc is null)
                return new AutomationGate(false, $"Scan '{root.Path}' and apply the result before automatic grabbing starts");
        }

        foreach (var root in roots)
        {
            var check = await _fs.CheckRootAsync(root.Path, ct);
            if (check.State != LibraryRootState.Accessible)
                return new AutomationGate(false, $"'{root.Path}' is not reachable ({check.Message}). Automatic searching is paused so nothing is treated as missing");
        }
        return new AutomationGate(true, "All library folders are reachable and scanned");
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
        var known = await db.MediaFiles.AsNoTracking().Include(f => f.EpisodeLinks).ToListAsync(ct);

        var unmapped = 0;
        string? LegacyKey(string? legacy)
        {
            if (legacy is null) return null;
            var mapped = LibraryPaths.MapLegacy(legacy, mappings, windows);
            if (mapped is null) { unmapped++; return null; }
            return LibraryPaths.Key(mapped, windows);
        }

        var movies = movieRows.Select(m => new CatalogMovie(m.Id, m.Title, m.Year, m.Provider == "tmdb" ? m.ProviderId : null, LegacyKey(m.LegacyPath))).ToList();
        var series = seriesRows.Select(s => new CatalogSeries(
            s.Id, s.Title, s.Year, s.Provider == "tmdb" ? s.ProviderId : null, LegacyKey(s.LegacyPath),
            s.Episodes.Select(e => new CatalogEpisode(e.Id, e.SeasonNumber, e.EpisodeNumber)).ToList())).ToList();
        var typeById = movieRows.ToDictionary(m => m.Id, _ => MediaType.Movie);
        foreach (var s in seriesRows) typeById[s.Id] = MediaType.Series;

        if (unmapped > 0)
            warnings.Add($"{unmapped} catalog entr{(unmapped == 1 ? "y has a Sonarr/Radarr path" : "ies have Sonarr/Radarr paths")} with no path mapping. They are matched by title and year only until you add a mapping.");

        var reports = new List<RootReport>();
        var accessible = new List<(LibraryRoot Root, string Key)>();
        var discovered = new Dictionary<string, (CandidateFile File, MediaType Type)>();
        var filesUnderUnavailable = 0;
        foreach (var root in roots)
        {
            var check = await _fs.CheckRootAsync(root.Path, ct);
            if (check.State != LibraryRootState.Accessible)
            {
                reports.Add(new RootReport(root.Id, root.MediaType, root.Path, check.State, check.Message, 0, 0, 0));
                filesUnderUnavailable += known.Count(f => LibraryPaths.IsWithin(f.PathKey, LibraryPaths.Key(root.Path, windows), windows));
                warnings.Add($"'{root.Path}' is unavailable ({check.Message}). Nothing under it was treated as missing.");
                continue;
            }

            var scan = await _fs.EnumerateAsync(root.Path, ct);
            reports.Add(new RootReport(root.Id, root.MediaType, root.Path, LibraryRootState.Accessible, null, scan.Files.Count, scan.SkippedReparsePoints, scan.InaccessibleEntries));
            accessible.Add((root, LibraryPaths.Key(root.Path, windows)));
            if (scan.SkippedReparsePoints > 0)
                warnings.Add($"Skipped {scan.SkippedReparsePoints} junction or symlink entries under '{root.Path}'. Their contents are not followed.");
            if (scan.InaccessibleEntries > 0)
                warnings.Add($"{scan.InaccessibleEntries} folders under '{root.Path}' could not be read.");
            foreach (var file in scan.Files)
                discovered.TryAdd(LibraryPaths.Key(file.FullPath, windows), (file, root.MediaType));
        }

        var knownByKey = known.ToDictionary(f => f.PathKey);
        var links = new List<PlannedLink>();
        var ambiguous = new List<ReviewItem>();
        var unmatched = new List<ReviewItem>();
        var alreadyLinked = 0;
        foreach (var (key, (file, type)) in discovered)
        {
            var match = LibraryMatcher.Match(file, type, movies, series, windows);
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
                             && existing.SizeBytes == file.SizeBytes && existing.EpisodeLinks.Select(l => l.EpisodeId).OrderBy(x => x).SequenceEqual(episodeIds.OrderBy(x => x)))
                    {
                        alreadyLinked++;
                    }
                    else
                    {
                        links.Add(new PlannedLink(file.FullPath, key, file.SizeBytes, type, match.MediaItemId!.Value, episodeIds, match.Method!, match.Resolution, existing.Id));
                    }
                    break;
                case MatchOutcome.Ambiguous:
                    ambiguous.Add(new ReviewItem(file.FullPath, type, match.Reason, match.CandidateIds!.ToList()));
                    break;
                default:
                    unmatched.Add(new ReviewItem(file.FullPath, type, match.Reason, []));
                    break;
            }
        }

        var missing = new List<PlannedMissing>();
        foreach (var row in known)
        {
            if (row.MediaItemId is not int itemId || !typeById.TryGetValue(itemId, out var type)) continue;
            if (!accessible.Any(a => LibraryPaths.IsWithin(row.PathKey, a.Key, windows))) continue;
            if (discovered.ContainsKey(row.PathKey) || row.Status == MediaFileStatus.Missing) continue;
            // A file can exist yet not be enumerated, for example behind a junction. It is not missing in that case.
            if (await _fs.FileExistsAsync(row.Path, ct)) continue;
            missing.Add(new PlannedMissing(row.Path, row.PathKey, type, itemId, row.Id));
        }

        return new ScanPlan(now, reports, links, missing, ambiguous, unmatched, alreadyLinked, filesUnderUnavailable, warnings);
    }

    /// <summary>
    /// HasFile follows verified links. It turns on when a present file is linked, and turns off only when every linked file is confirmed missing
    /// and no completed download import exists for that item. Items with no linked rows (for example, downloads imported by OneArr) are left alone.
    /// </summary>
    private static async Task RecalculateAsync(OneArrDbContext db, HashSet<int> itemIds, HashSet<int> episodeIds,
        HashSet<int> formerlyLinkedItems, HashSet<int> formerlyLinkedEpisodes, CancellationToken ct)
    {
        if (episodeIds.Count > 0)
        {
            var episodes = await db.Episodes.Where(e => episodeIds.Contains(e.Id)).ToListAsync(ct);
            var links = await db.MediaFileEpisodes.AsNoTracking().Include(l => l.MediaFile)
                .Where(l => episodeIds.Contains(l.EpisodeId)).ToListAsync(ct);
            var imported = (await db.DownloadJobs.AsNoTracking()
                .Where(j => j.Status == DownloadStatus.Imported && j.EpisodeId != null && episodeIds.Contains(j.EpisodeId.Value))
                .Select(j => j.EpisodeId!.Value).ToListAsync(ct)).ToHashSet();
            foreach (var ep in episodes)
            {
                var rows = links.Where(l => l.EpisodeId == ep.Id).Select(l => l.MediaFile!.Status).ToList();
                if (rows.Contains(MediaFileStatus.Present)) ep.HasFile = true;
                else if ((rows.Count > 0 || formerlyLinkedEpisodes.Contains(ep.Id)) && !imported.Contains(ep.Id)) ep.HasFile = false;
                itemIds.Add(ep.MediaItemId);
            }
            // Save first so the series check below reads the new episode flags.
            await db.SaveChangesAsync(ct);
        }

        if (itemIds.Count == 0) return;
        var items = await db.MediaItems.Where(m => itemIds.Contains(m.Id)).ToListAsync(ct);
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
                else if ((statuses.Count > 0 || formerlyLinkedItems.Contains(movie.Id)) && !imported.Contains(movie.Id)) movie.HasFile = false;
            }
        }

        foreach (var series in items.Where(m => m.MediaType == MediaType.Series))
            series.HasFile = await db.Episodes.AnyAsync(e => e.MediaItemId == series.Id && e.HasFile, ct);
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
