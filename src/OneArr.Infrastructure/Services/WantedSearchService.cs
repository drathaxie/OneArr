using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
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

    public WantedSearchService(IDbContextFactory<OneArrDbContext> db, ReleaseService releases, SettingsStore settings, TimeProvider time, SearchPolicy policy)
    {
        _db = db;
        _releases = releases;
        _settings = settings;
        _time = time;
        _policy = policy;
    }

    public async Task<WantedSearchSummary> RunOnceAsync(CancellationToken ct)
    {
        if (!await _settings.GetBoolAsync(SettingKeys.AutomaticGrab, defaultValue: false, ct))
            return new WantedSearchSummary(false, 0, 0, 0, 0, "Automatic grabbing is off");

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

            searched++;
            var outcome = await SearchAndGrabAsync(c, ct);
            if (outcome.Outcome == SearchOutcome.Grabbed) grabbed++;
            await RecordAsync(c, outcome.Outcome, outcome.Message, _time.GetUtcNow(), ct, outcome.NextDelay);
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

    private sealed record Candidate(int MediaItemId, int? EpisodeId, string Key, bool HasActiveDownload, int ConsecutiveFailures, string Title, DateTimeOffset NextAttemptAt);

    private async Task<List<Candidate>> DueCandidatesAsync(DateOnly today, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);

        var movies = await db.MediaItems.AsNoTracking()
            .Where(m => m.MediaType == MediaType.Movie && m.Monitored && !m.HasFile)
            .Select(m => new { m.Id, m.Title })
            .ToListAsync(ct);
        var episodes = await (from e in db.Episodes.AsNoTracking()
                              join m in db.MediaItems.AsNoTracking() on e.MediaItemId equals m.Id
                              where m.Monitored && e.Monitored && !e.HasFile && e.AirDate != null && e.AirDate <= today
                              select new { EpisodeId = e.Id, MediaItemId = m.Id, m.Title, e.SeasonNumber, e.EpisodeNumber }).ToListAsync(ct);

        var states = await db.SearchAttempts.AsNoTracking().ToDictionaryAsync(s => s.ItemKey, ct);
        var active = (await db.DownloadJobs.AsNoTracking()
            .Where(j => ActiveStatuses.Contains(j.Status))
            .Select(j => new ValueTuple<int?, int?>(j.MediaItemId, j.EpisodeId)).ToListAsync(ct))
            .ToHashSet();

        var list = new List<Candidate>();
        foreach (var m in movies)
        {
            var key = $"movie:{m.Id}";
            if (!IsDue(states, key, now)) continue;
            list.Add(new Candidate(m.Id, null, key, active.Contains(((int?)m.Id, (int?)null)), FailuresOf(states, key), m.Title, NextOf(states, key, now)));
        }
        foreach (var e in episodes)
        {
            var key = $"episode:{e.EpisodeId}";
            if (!IsDue(states, key, now)) continue;
            list.Add(new Candidate(e.MediaItemId, e.EpisodeId, key, active.Contains(((int?)e.MediaItemId, (int?)e.EpisodeId)), FailuresOf(states, key), $"{e.Title} S{e.SeasonNumber:00}E{e.EpisodeNumber:00}", NextOf(states, key, now)));
        }

        // Never-searched items first, then the ones that have waited longest.
        return list.OrderBy(c => c.NextAttemptAt).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToList();
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
