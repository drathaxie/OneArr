using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Http;
using OneArr.Infrastructure.Library;
using OneArr.Infrastructure.Persistence;

namespace OneArr.Infrastructure.Services;

public sealed record RssSyncSummary(bool Enabled, int Indexers, int Releases, int Matched, int Grabbed, IReadOnlyList<string> Errors, string Message)
{
    public override string ToString() => Message;
}

/// <summary>
/// RSS sync: reads the newest releases of each indexer the user opted in (<see cref="IndexerConfig.RssSync"/>) and grabs
/// the ones that match a wanted movie or aired episode. One feed request per indexer finds new episodes within minutes,
/// instead of waiting for that episode's turn in the wanted search. Matching uses the same ranker and the same
/// <see cref="ReleaseService.GrabAsync"/> as a manual grab, so quality rules, duplicate hashes and the one-active-download
/// rule all apply. Gated exactly like wanted search: automatic grabbing on, and the library reviewed.
/// </summary>
public sealed class RssSyncService
{
    private static readonly HashSet<DownloadStatus> Blocking = [DownloadStatus.Queued, DownloadStatus.Downloading, DownloadStatus.Completed, DownloadStatus.Missing];
    private readonly IDbContextFactory<OneArrDbContext> _db;
    private readonly IIndexerClient _indexer;
    private readonly IReleaseParser _parser;
    private readonly ReleaseService _releases;
    private readonly SettingsStore _settings;
    private readonly LibraryReconciler _library;
    private readonly TimeProvider _time;
    private readonly SearchPolicy _policy;

    /// <summary>Upper bound on grabs from one sync, so a misparsed feed cannot flood the download client.</summary>
    public int MaxGrabsPerRun { get; init; } = 10;

    public RssSyncService(IDbContextFactory<OneArrDbContext> db, IIndexerClient indexer, IReleaseParser parser, ReleaseService releases,
        SettingsStore settings, LibraryReconciler library, TimeProvider time, SearchPolicy policy)
    {
        _db = db;
        _indexer = indexer;
        _parser = parser;
        _releases = releases;
        _settings = settings;
        _library = library;
        _time = time;
        _policy = policy;
    }

    public async Task<RssSyncSummary> RunOnceAsync(CancellationToken ct)
    {
        if (!await _settings.GetBoolAsync(SettingKeys.AutomaticGrab, defaultValue: false, ct))
            return new RssSyncSummary(false, 0, 0, 0, 0, [], "Automatic grabbing is off");
        var gate = await _library.CheckAutomationAsync(ct);
        if (!gate.Allowed) return new RssSyncSummary(true, 0, 0, 0, 0, [], "Paused: " + gate.Reason);

        await using var db = await _db.CreateDbContextAsync(ct);
        var indexers = await db.Indexers.AsNoTracking().Where(i => i.Enabled && i.RssSync)
            .OrderBy(i => i.Priority).ThenBy(i => i.Id).ToListAsync(ct);
        if (indexers.Count == 0)
            return new RssSyncSummary(true, 0, 0, 0, 0, [], "No enabled indexer has RSS sync turned on");

        var wanted = await WantedAsync(db, ct);
        if (wanted.Count == 0)
            return new RssSyncSummary(true, indexers.Count, 0, 0, 0, [], "Nothing is wanted");

        var errors = new List<string>();
        var releases = 0;
        // Wanted item -> releases that name it. Keyed by (media item, episode).
        var matches = new Dictionary<(int MediaItemId, int? EpisodeId), List<ReleaseCandidate>>();
        foreach (var indexer in indexers)
        {
            IReadOnlyList<ReleaseCandidate>? feed;
            try
            {
                feed = await _indexer.RecentAsync(indexer, ct);
            }
            catch (Exception ex) when (ex is ExternalServiceException or ArgumentException or UriFormatException or ProviderNotConfiguredException)
            {
                errors.Add($"{indexer.Name}: {OutboundHttp.Redact(ex.Message)}");
                continue;
            }
            if (feed is null) { errors.Add($"{indexer.Name}: this indexer type has no recent-releases feed"); continue; }
            releases += feed.Count;
            foreach (var release in feed)
            {
                var item = Match(release, indexer, wanted);
                if (item is null) continue;
                if (!matches.TryGetValue(item.Value, out var list)) matches[item.Value] = list = [];
                list.Add(release);
            }
        }

        var grabbed = new List<string>();
        var now = _time.GetUtcNow();
        foreach (var ((mediaItemId, episodeId), candidates) in matches)
        {
            if (grabbed.Count >= MaxGrabsPerRun) break;
            ct.ThrowIfCancellationRequested();
            try
            {
                var best = (await _releases.EvaluateAsync(mediaItemId, episodeId, candidates, ct)).FirstOrDefault(d => d.Accepted);
                if (best is null) continue;
                var job = await _releases.GrabAsync(new GrabRequest(mediaItemId, episodeId, best.Candidate.Title, best.Candidate.Link, best.Candidate.InfoHash), ct);
                grabbed.Add(job.ReleaseTitle);
                await RecordGrabAsync(mediaItemId, episodeId, job.ReleaseTitle, now, ct);
            }
            catch (ArgumentException ex)
            {
                // Duplicate or rejected at grab time: leave it for the next sync or the wanted search.
                errors.Add(ex.Message);
            }
            catch (ExternalServiceException ex)
            {
                errors.Add("Download client: " + OutboundHttp.Redact(ex.Message));
            }
        }

        var message = $"Read {releases} release(s) from {indexers.Count} indexer(s), {matches.Count} wanted item(s) matched, grabbed {grabbed.Count}";
        if (grabbed.Count > 0) message += ": " + string.Join(", ", grabbed);
        if (errors.Count > 0) message += ". Problems: " + string.Join("; ", errors.Distinct().Take(5));
        return new RssSyncSummary(true, indexers.Count, releases, matches.Count, grabbed.Count, errors, message);
    }

    /// <summary>Cheap pre-filter: normalized title, then season/episode (or year for movies). The ranker makes the real decision.</summary>
    private (int MediaItemId, int? EpisodeId)? Match(ReleaseCandidate release, IndexerConfig indexer, Wanted wanted)
    {
        var parsed = _parser.Parse(release.Title);
        if (parsed.IsUnsafeName) return null;
        var title = ReleaseParser.NormalizeTitle(parsed.Title);
        if (parsed.Season is null && parsed.AbsoluteEpisodes is { Count: 1 } abs)
            return indexer.TvEnabled && wanted.Absolute.TryGetValue((title, abs[0]), out var anime) ? anime : null;
        if (parsed.Season is int season)
        {
            // Single episodes only. Season packs and multi-episode releases are left to the wanted search, which can
            // look at the whole season instead of grabbing a pack for one missing episode.
            if (!indexer.TvEnabled || parsed.IsSeasonPack || parsed.Episodes.Count != 1) return null;
            return wanted.Episodes.TryGetValue((title, season, parsed.Episodes[0]), out var ep) ? ep : null;
        }
        if (!indexer.MoviesEnabled || !wanted.Movies.TryGetValue(title, out var movies)) return null;
        var movie = movies.FirstOrDefault(m => parsed.Year is not int y || m.Year is not int my || Math.Abs(y - my) <= 1);
        return movie.Id == 0 ? null : (movie.Id, null);
    }

    private sealed record Wanted(
        Dictionary<(string Title, int Season, int Episode), (int MediaItemId, int? EpisodeId)> Episodes,
        Dictionary<string, List<(int Id, int? Year)>> Movies,
        Dictionary<(string Title, int Absolute), (int MediaItemId, int? EpisodeId)> Absolute)
    {
        public int Count => Episodes.Count + Movies.Sum(m => m.Value.Count) + Absolute.Count;
    }

    private async Task<Wanted> WantedAsync(OneArrDbContext db, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var tomorrow = DateOnly.FromDateTime(now.UtcDateTime).AddDays(1);
        var busy = (await db.DownloadJobs.AsNoTracking().Where(j => Blocking.Contains(j.Status))
            .Select(j => new ValueTuple<int?, int?>(j.MediaItemId, j.EpisodeId)).ToListAsync(ct)).ToHashSet();
        // Items that already have a file are included for profiles with upgrades on (and a recycle bin set), so a PROPER or a
        // better release in the feed can replace them. The ranker decides whether a release really is better.
        var upgradeProfiles = string.IsNullOrWhiteSpace(await _settings.GetAsync(SettingKeys.RecycleBin, ct))
            ? new List<int>()
            : await db.QualityProfiles.AsNoTracking().Where(p => p.UpgradesAllowed).Select(p => p.Id).ToListAsync(ct);

        var episodes = await (from e in db.Episodes.AsNoTracking()
                              join m in db.MediaItems.AsNoTracking() on e.MediaItemId equals m.Id
                              where m.MediaType == MediaType.Series && m.Monitored && e.Monitored && (!e.HasFile || upgradeProfiles.Contains(m.QualityProfileId)) && e.AirDate != null && e.AirDate <= tomorrow
                              select new { e.Id, MediaItemId = m.Id, m.Title, m.AlternateTitles, e.SeasonNumber, e.EpisodeNumber, e.AirDate, e.AirDateUtc, m.IsAnime, e.AbsoluteNumber }).ToListAsync(ct);
        var byEpisode = new Dictionary<(string, int, int), (int, int?)>();
        var byAbsolute = new Dictionary<(string, int), (int, int?)>();
        foreach (var e in episodes)
        {
            if (busy.Contains(((int?)e.MediaItemId, (int?)e.Id)) || !Episode.HasAired(e.AirDate, e.AirDateUtc, now)) continue;
            foreach (var name in Names(e.Title, e.AlternateTitles))
            {
                byEpisode.TryAdd((name, e.SeasonNumber, e.EpisodeNumber), (e.MediaItemId, e.Id));
                if (e.IsAnime && e.AbsoluteNumber is int abs) byAbsolute.TryAdd((name, abs), (e.MediaItemId, e.Id));
            }
        }

        var movies = await db.MediaItems.AsNoTracking()
            .Where(m => m.MediaType == MediaType.Movie && m.Monitored && (!m.HasFile || upgradeProfiles.Contains(m.QualityProfileId)))
            .Select(m => new { m.Id, m.Title, m.Year, m.AlternateTitles, m.TheatricalReleaseDate, m.DigitalReleaseDate, m.PhysicalReleaseDate }).ToListAsync(ct);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var byMovie = new Dictionary<string, List<(int, int?)>>();
        foreach (var m in movies)
        {
            if (busy.Contains(((int?)m.Id, (int?)null))) continue;
            if (!MediaItem.MovieAvailable(m.TheatricalReleaseDate, m.DigitalReleaseDate, m.PhysicalReleaseDate, today)) continue;
            foreach (var key in Names(m.Title, m.AlternateTitles))
            {
                if (!byMovie.TryGetValue(key, out var list)) byMovie[key] = list = [];
                list.Add((m.Id, m.Year));
            }
        }
        return new Wanted(byEpisode, byMovie, byAbsolute);
    }

    /// <summary>Normalized keys for the title and each alternate title.</summary>
    private static IEnumerable<string> Names(string title, string? alternates) =>
        new MediaItem { Title = title, AlternateTitles = alternates }.AlternateTitleList().Prepend(title)
            .Select(ReleaseParser.NormalizeTitle).Where(k => k.Length > 1).Distinct();

    /// <summary>Records the grab in the same search history the wanted search uses, so the item shows why it was grabbed
    /// and is not searched again while it downloads.</summary>
    private async Task RecordGrabAsync(int mediaItemId, int? episodeId, string title, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var key = episodeId is int e ? $"episode:{e}" : $"movie:{mediaItemId}";
        var row = await db.SearchAttempts.FirstOrDefaultAsync(s => s.ItemKey == key, ct);
        if (row is null)
        {
            row = new SearchAttempt { ItemKey = key, MediaItemId = mediaItemId, EpisodeId = episodeId };
            db.SearchAttempts.Add(row);
        }
        row.LastAttemptAt = now;
        row.Attempts++;
        row.LastOutcome = SearchOutcome.Grabbed;
        var message = "RSS sync sent " + title + " to the download client";
        row.LastMessage = message.Length <= 500 ? message : message[..500];
        row.NextAttemptAt = now + _policy.AfterGrabInterval;
        row.ConsecutiveFailures = 0;
        await db.SaveChangesAsync(ct);
    }
}
