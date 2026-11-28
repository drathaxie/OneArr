using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Library;
using OneArr.Infrastructure.Persistence;

namespace OneArr.Infrastructure.Services;

/// <summary>Bounds and retry timing for automated searches. Defaults are conservative to protect indexers.</summary>
public sealed record SearchPolicy
{
    /// <summary>Items searched in one scheduled pass, across all wanted movies and episodes.</summary>
    public int MaxSearchesPerRun { get; init; } = 10;
    /// <summary>Wait before searching again when no acceptable release was found.</summary>
    public TimeSpan NoReleaseInterval { get; init; } = TimeSpan.FromHours(6);
    /// <summary>First retry delay after a failed search or grab. Doubles per consecutive failure.</summary>
    public TimeSpan ErrorBaseDelay { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Cap for error backoff.</summary>
    public TimeSpan ErrorMaxDelay { get; init; } = TimeSpan.FromHours(24);
    /// <summary>Wait after a successful grab before the item is looked at again (it should be downloading by then).</summary>
    public TimeSpan AfterGrabInterval { get; init; } = TimeSpan.FromHours(24);
    /// <summary>Wait before reconsidering an item whose previous download is missing from qBittorrent.</summary>
    public TimeSpan MissingDownloadInterval { get; init; } = TimeSpan.FromHours(1);
    /// <summary>
    /// Episodes whose air date is within this many days are "recently aired": searched before the backlog, and retried on
    /// <see cref="RecentRetryInterval"/> instead of <see cref="NoReleaseInterval"/>. TMDb gives an air date but no air time,
    /// so an evening episode counts as aired hours before its release exists.
    /// </summary>
    public int RecentAirDays { get; init; } = 2;
    /// <summary>Retry spacing for a recently aired episode that has no acceptable release yet.</summary>
    public TimeSpan RecentRetryInterval { get; init; } = TimeSpan.FromHours(1);
    /// <summary>Wait before looking again for a better release of an item that already has a file.</summary>
    public TimeSpan UpgradeInterval { get; init; } = TimeSpan.FromHours(24);
}

public sealed record WantedSearchSummary(bool Enabled, int Considered, int Searched, int Grabbed, int Skipped, string Message)
{
    public override string ToString() => Message;
}

/// <summary>
/// Scheduled search for wanted movies and aired episodes. Grabs the best accepted release through the same
/// ReleaseService used for manual grabs, so quality rules, hash de-duplication and the active-download check all apply.
/// Search state is persisted per item, so restarts keep backoff.
/// </summary>
public sealed class WantedSearchService
{
    private static readonly HashSet<DownloadStatus> ActiveStatuses = [DownloadStatus.Queued, DownloadStatus.Downloading, DownloadStatus.Completed];
    private readonly IDbContextFactory<OneArrDbContext> _db;
    private readonly ReleaseService _releases;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly SearchPolicy _policy;
    private readonly LibraryReconciler _library;

    public WantedSearchService(IDbContextFactory<OneArrDbContext> db, ReleaseService releases, SettingsStore settings, TimeProvider time, SearchPolicy policy, LibraryReconciler library)
    {
        _db = db;
        _releases = releases;
        _settings = settings;
        _time = time;
        _policy = policy;
        _library = library;
    }

    public async Task<WantedSearchSummary> RunOnceAsync(CancellationToken ct)
    {
        if (!await _settings.GetBoolAsync(SettingKeys.AutomaticGrab, defaultValue: false, ct))
            return new WantedSearchSummary(false, 0, 0, 0, 0, "Automatic grabbing is off");

        // Existing media must be scanned and applied first, or it would look missing and be downloaded again.
        var gate = await _library.CheckAutomationAsync(ct);
        if (!gate.Allowed) return new WantedSearchSummary(true, 0, 0, 0, 0, "Paused: " + gate.Reason);

        var now = _time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var candidates = await DueCandidatesAsync(today, now, ct);
        var batch = candidates.Take(_policy.MaxSearchesPerRun).ToList();

        int searched = 0, grabbed = 0, skipped = 0;
        foreach (var c in batch)
        {
            ct.ThrowIfCancellationRequested();
            if (c.HasActiveDownload)
            {
                skipped++;
                await RecordAsync(c, SearchOutcome.SkippedActiveDownload, "A download for this item is already queued or in progress", now, ct, schedule: _policy.AfterGrabInterval);
                continue;
            }

            // A torrent that disappeared from qBittorrent is not silently re-grabbed: it may only
            // have been paused, moved, or temporarily unreachable. The operator clears it in Queue.
            if (c.HasMissingDownload)
            {
                skipped++;
                await RecordAsync(c, SearchOutcome.SkippedMissingDownload, "A previous download for this item is missing from qBittorrent. Clear it in Queue before it can be searched again", now, ct, schedule: _policy.MissingDownloadInterval);
                continue;
            }

            searched++;
            var outcome = await SearchAndGrabAsync(c, ct);
            if (outcome.Outcome == SearchOutcome.Grabbed) grabbed++;
            // A just-aired episode usually has no release for a few hours. Look again soon rather than in six hours.
            var delay = outcome.Outcome == SearchOutcome.NoAcceptableRelease && IsRecentlyAired(c, today) && !c.Upgrade
                ? TimeSpan.FromTicks(Math.Min(outcome.NextDelay.Ticks, _policy.RecentRetryInterval.Ticks))
                : outcome.NextDelay;
            // Upgrades are a nicety: look once a day, not every six hours.
            if (c.Upgrade && outcome.Outcome is SearchOutcome.NoAcceptableRelease or SearchOutcome.Rejected && delay < _policy.UpgradeInterval)
                delay = _policy.UpgradeInterval;
            await RecordAsync(c, outcome.Outcome, outcome.Message, _time.GetUtcNow(), ct, delay);
        }

        var message = $"Considered {candidates.Count}, searched {searched}, grabbed {grabbed}, skipped {skipped}";
        return new WantedSearchSummary(true, candidates.Count, searched, grabbed, skipped, message);
    }

    private async Task<(SearchOutcome Outcome, string Message, TimeSpan NextDelay)> SearchAndGrabAsync(Candidate c, CancellationToken ct)
    {
        ReleaseSearchResult result;
        try
        {
            result = await _releases.SearchAsync(c.MediaItemId, c.EpisodeId, ct);
        }
        catch (ArgumentException ex)
        {
            return (SearchOutcome.Rejected, ex.Message, _policy.NoReleaseInterval);
        }

        var best = result.Decisions.FirstOrDefault(d => d.Accepted);
        if (best is null)
        {
            // Every configured indexer failing is an indexer problem; otherwise the releases simply did not qualify.
            if (result.IndexerErrors.Count > 0 && result.Decisions.Count == 0)
                return (SearchOutcome.IndexerError, string.Join("; ", result.IndexerErrors.Select(e => $"{e.Indexer}: {e.Message}")), Backoff(c.ConsecutiveFailures + 1));
            return (SearchOutcome.NoAcceptableRelease, $"No acceptable release among {result.Decisions.Count} result(s)", _policy.NoReleaseInterval);
        }

        try
        {
            var job = await _releases.GrabAsync(new GrabRequest(c.MediaItemId, c.EpisodeId, best.Candidate.Title, best.Candidate.Link, best.Candidate.InfoHash), ct);
            return (SearchOutcome.Grabbed, $"Sent {job.ReleaseTitle} to the download client", _policy.AfterGrabInterval);
        }
        catch (ArgumentException ex)
        {
            // Duplicate or rejected at grab time. Waiting the normal interval avoids a loop on the same release.
            return (SearchOutcome.Rejected, ex.Message, _policy.NoReleaseInterval);
        }
        catch (ExternalServiceException ex)
        {
            return (SearchOutcome.DownloadClientError, ex.Message, Backoff(c.ConsecutiveFailures + 1));
        }
    }

    private TimeSpan Backoff(int failures)
    {
        // failures=1 -> base, 2 -> 2x base, ... capped at ErrorMaxDelay. Bounded exponent avoids overflow.
        var exponent = Math.Min(Math.Max(failures - 1, 0), 16);
        var delay = TimeSpan.FromTicks(_policy.ErrorBaseDelay.Ticks * (1L << exponent));
        return delay > _policy.ErrorMaxDelay ? _policy.ErrorMaxDelay : delay;
    }

    private sealed record Candidate(int MediaItemId, int? EpisodeId, string Key, bool HasActiveDownload, bool HasMissingDownload, int ConsecutiveFailures, string Title, DateTimeOffset NextAttemptAt, DateOnly? AirDate, bool NeverSearched, bool Upgrade = false);

    private bool IsRecentlyAired(Candidate c, DateOnly today) =>
        c.AirDate is DateOnly aired && aired >= today.AddDays(-_policy.RecentAirDays);

    private async Task<List<Candidate>> DueCandidatesAsync(DateOnly today, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var tomorrow = today.AddDays(1);

        var movies = await db.MediaItems.AsNoTracking()
            .Where(m => m.MediaType == MediaType.Movie && m.Monitored && !m.HasFile && m.LibraryReviewedUtc != null)
            .Select(m => new { m.Id, m.Title, m.TheatricalReleaseDate, m.DigitalReleaseDate, m.PhysicalReleaseDate })
            .ToListAsync(ct);
        // A movie still only in cinemas has nothing but cam copies; it is searched once it is out digitally or on disc.
        movies = movies.Where(m => MediaItem.MovieAvailable(m.TheatricalReleaseDate, m.DigitalReleaseDate, m.PhysicalReleaseDate, today)).ToList();
        var episodes = await (from e in db.Episodes.AsNoTracking()
                              join m in db.MediaItems.AsNoTracking() on e.MediaItemId equals m.Id
                              // An entry not yet checked against the disk may already have this file: it waits for the next scan.
                              where m.Monitored && m.LibraryReviewedUtc != null && e.Monitored && !e.HasFile && e.AirDate != null && e.AirDate <= tomorrow
                              select new { EpisodeId = e.Id, MediaItemId = m.Id, m.Title, e.SeasonNumber, e.EpisodeNumber, e.AirDate, e.AirDateUtc }).ToListAsync(ct);
        // An exact air time decides "aired" when known (an evening episode is not searched that morning); otherwise the date does.
        // Tomorrow's date is loaded because a show airing in Asia or Australia is already out the previous day in UTC.
        episodes = episodes.Where(e => Episode.HasAired(e.AirDate, e.AirDateUtc, now)).ToList();

        var states = await db.SearchAttempts.AsNoTracking().ToDictionaryAsync(s => s.ItemKey, ct);
        var active = (await db.DownloadJobs.AsNoTracking()
            .Where(j => ActiveStatuses.Contains(j.Status))
            .Select(j => new ValueTuple<int?, int?>(j.MediaItemId, j.EpisodeId)).ToListAsync(ct))
            .ToHashSet();
        var missing = (await db.DownloadJobs.AsNoTracking()
            .Where(j => j.Status == DownloadStatus.Missing)
            .Select(j => new ValueTuple<int?, int?>(j.MediaItemId, j.EpisodeId)).ToListAsync(ct))
            .ToHashSet();

        var list = new List<Candidate>();
        foreach (var m in movies)
        {
            var key = $"movie:{m.Id}";
            if (!IsDue(states, key, now)) continue;
            list.Add(new Candidate(m.Id, null, key, active.Contains(((int?)m.Id, (int?)null)), missing.Contains(((int?)m.Id, (int?)null)), FailuresOf(states, key), m.Title, NextOf(states, key, now), null, !states.ContainsKey(key)));
        }
        foreach (var e in episodes)
        {
            var key = $"episode:{e.EpisodeId}";
            if (!IsDue(states, key, now)) continue;
            list.Add(new Candidate(e.MediaItemId, e.EpisodeId, key, active.Contains(((int?)e.MediaItemId, (int?)e.EpisodeId)), missing.Contains(((int?)e.MediaItemId, (int?)e.EpisodeId)), FailuresOf(states, key), $"{e.Title} S{e.SeasonNumber:00}E{e.EpisodeNumber:00}", NextOf(states, key, now), e.AirDate, !states.ContainsKey(key)));
        }

        await AddUpgradeCandidatesAsync(db, list, states, active, missing, now, ct);

        // Recently aired episodes first, then items never searched, then the backlog by how long it has waited; upgrades last.
        // Ordering only by due time put a just-aired episode behind every overdue backlog item, so with more than
        // MaxSearchesPerRun overdue items a new episode could wait hours.
        return list
            .OrderBy(c => c.Upgrade ? 3 : IsRecentlyAired(c, today) ? 0 : c.NeverSearched ? 1 : 2)
            .ThenBy(c => c.NextAttemptAt)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Items that have a file below their profile's cutoff, for profiles with upgrades switched on. Only when a recycle bin is set:
    /// without one the old file could not be moved aside, and the upgrade would sit next to it.
    /// </summary>
    private async Task AddUpgradeCandidatesAsync(OneArrDbContext db, List<Candidate> list, Dictionary<string, SearchAttempt> states,
        HashSet<(int?, int?)> active, HashSet<(int?, int?)> missing, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(await _settings.GetAsync(SettingKeys.RecycleBin, ct))) return;
        var profiles = await db.QualityProfiles.AsNoTracking().Where(p => p.UpgradesAllowed).ToDictionaryAsync(p => p.Id, p => p.CutoffResolution, ct);
        if (profiles.Count == 0) return;
        var ids = profiles.Keys.ToList();
        var items = await db.MediaItems.AsNoTracking().Where(m => m.Monitored && m.LibraryReviewedUtc != null && ids.Contains(m.QualityProfileId))
            .Select(m => new { m.Id, m.Title, m.MediaType, m.QualityProfileId, m.HasFile }).ToListAsync(ct);
        var itemIds = items.Select(i => i.Id).ToList();
        var files = await db.MediaFiles.AsNoTracking().Include(f => f.EpisodeLinks)
            .Where(f => f.Status == MediaFileStatus.Present && f.MediaItemId != null && itemIds.Contains(f.MediaItemId!.Value)).ToListAsync(ct);
        var episodes = await db.Episodes.AsNoTracking().Where(e => itemIds.Contains(e.MediaItemId) && e.Monitored && e.HasFile)
            .Select(e => new { e.Id, e.MediaItemId, e.SeasonNumber, e.EpisodeNumber }).ToListAsync(ct);
        var byId = items.ToDictionary(i => i.Id);

        void Add(int itemId, int? episodeId, string title, int best)
        {
            var cutoff = profiles[byId[itemId].QualityProfileId];
            if (best <= 0 || best >= cutoff) return; // unknown quality is never "upgraded" blindly
            var key = episodeId is int e ? $"episode:{e}" : $"movie:{itemId}";
            if (!IsDue(states, key, now)) return;
            list.Add(new Candidate(itemId, episodeId, key, active.Contains((itemId, episodeId)), missing.Contains((itemId, episodeId)),
                FailuresOf(states, key), title + " (upgrade)", NextOf(states, key, now), null, false, Upgrade: true));
        }

        foreach (var m in items.Where(i => i.MediaType == MediaType.Movie && i.HasFile))
            Add(m.Id, null, m.Title, files.Where(f => f.MediaItemId == m.Id).Select(f => f.Resolution ?? 0).DefaultIfEmpty(0).Max());
        var bestByEpisode = files.SelectMany(f => f.EpisodeLinks.Select(l => (l.EpisodeId, Res: f.Resolution ?? 0)))
            .GroupBy(x => x.EpisodeId).ToDictionary(g => g.Key, g => g.Max(x => x.Res));
        foreach (var e in episodes)
            Add(e.MediaItemId, e.Id, $"{byId[e.MediaItemId].Title} S{e.SeasonNumber:00}E{e.EpisodeNumber:00}", bestByEpisode.GetValueOrDefault(e.Id));
    }

    private static bool IsDue(Dictionary<string, SearchAttempt> states, string key, DateTimeOffset now) =>
        !states.TryGetValue(key, out var s) || s.NextAttemptAt <= now;

    private static int FailuresOf(Dictionary<string, SearchAttempt> states, string key) =>
        states.TryGetValue(key, out var s) ? s.ConsecutiveFailures : 0;

    private static DateTimeOffset NextOf(Dictionary<string, SearchAttempt> states, string key, DateTimeOffset now) =>
        states.TryGetValue(key, out var s) ? s.NextAttemptAt : now;

    private async Task RecordAsync(Candidate c, SearchOutcome outcome, string message, DateTimeOffset now, CancellationToken ct, TimeSpan schedule)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.SearchAttempts.FirstOrDefaultAsync(s => s.ItemKey == c.Key, ct);
        if (row is null)
        {
            row = new SearchAttempt { ItemKey = c.Key, MediaItemId = c.MediaItemId, EpisodeId = c.EpisodeId };
            db.SearchAttempts.Add(row);
        }
        row.LastAttemptAt = now;
        row.Attempts++;
        row.LastOutcome = outcome;
        row.LastMessage = Truncate(message);
        row.NextAttemptAt = now + schedule;
        row.ConsecutiveFailures = outcome is SearchOutcome.IndexerError or SearchOutcome.DownloadClientError ? row.ConsecutiveFailures + 1 : 0;
        await db.SaveChangesAsync(ct);
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
}
