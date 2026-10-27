using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Persistence;

namespace OneArr.Infrastructure.Services;

public sealed record EpisodeMonitor(int SeasonNumber, int EpisodeNumber, bool Monitored);

public sealed record AddMediaRequest(MediaType Type, string ProviderId, int QualityProfileId, string RootFolder, bool Monitored, IReadOnlyList<int>? MonitorSeasons, bool MonitorAllSeasons, IReadOnlyList<EpisodeMonitor>? MonitorEpisodes = null);

public sealed record AddMediaResult(MediaItem Item, bool Created);

/// <summary>Catalog operations: add (deduplicated), monitoring and wanted lists. All identity checks use (MediaType, Provider, ProviderId).</summary>
public sealed class MediaService
{
    private static readonly Regex UnsafeFolderChars = new(@"[\\/:*?""<>|\x00-\x1f]|\.\.", RegexOptions.Compiled);
    private readonly IDbContextFactory<OneArrDbContext> _db;
    private readonly IMetadataProvider _metadata;
    private readonly TimeProvider _time;

    public MediaService(IDbContextFactory<OneArrDbContext> db, IMetadataProvider metadata, TimeProvider time)
    {
        _db = db;
        _metadata = metadata;
        _time = time;
    }

    public async Task<AddMediaResult> AddAsync(AddMediaRequest request, CancellationToken ct)
    {
        if (!request.ProviderId.All(char.IsDigit) || request.ProviderId.Length == 0)
            throw new ArgumentException("Provider id must be numeric");
        ValidateRoot(request.RootFolder);

        await using (var db = await _db.CreateDbContextAsync(ct))
        {
            var existing = await FindAsync(db, request.Type, request.ProviderId, ct);
            if (existing is not null) return new AddMediaResult(existing, Created: false);
            if (!await db.QualityProfiles.AnyAsync(p => p.Id == request.QualityProfileId, ct))
                throw new ArgumentException("Quality profile does not exist");
        }

        // Metadata is fetched outside the DB transaction so network latency never holds a write lock.
        var details = await _metadata.GetDetailsAsync(request.Type, request.ProviderId, ct);
        var seasons = request.Type == MediaType.Series ? await _metadata.GetSeasonsAsync(request.ProviderId, ct) : [];

        await using var write = await _db.CreateDbContextAsync(ct);
        await using var tx = await write.Database.BeginTransactionAsync(ct);
        var item = new MediaItem
        {
            MediaType = request.Type,
            Provider = "tmdb",
            ProviderId = request.ProviderId,
            Title = details.Title,
            Year = details.Year,
            Overview = details.Overview,
            PosterPath = details.PosterPath,
            Monitored = request.Monitored,
            QualityProfileId = request.QualityProfileId,
            RootFolder = request.RootFolder,
            CreatedAt = _time.GetUtcNow(),
        };
        write.MediaItems.Add(item);
        try
        {
            await write.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Concurrent add of the same title: the unique index won; return the winner.
            await tx.RollbackAsync(ct);
            var winner = await FindAsync(write, request.Type, request.ProviderId, ct);
            if (winner is not null) return new AddMediaResult(winner, Created: false);
            throw;
        }

        if (request.Type == MediaType.Series)
        {
            var monitoredSeasons = request.MonitorAllSeasons ? null : new HashSet<int>(request.MonitorSeasons ?? []);
            foreach (var s in seasons)
            {
                var seasonMonitored = monitoredSeasons is null || monitoredSeasons.Contains(s.SeasonNumber);
                write.Seasons.Add(new Season { MediaItemId = item.Id, SeasonNumber = s.SeasonNumber, Monitored = seasonMonitored });
                foreach (var e in s.Episodes)
                {
                    // Per-episode flags win over the season flag when supplied (Sonarr exceptions inside
                    // a monitored season, or unmonitored episodes inside an unmonitored season).
                    var episodeMonitored = request.MonitorEpisodes?.FirstOrDefault(m => m.SeasonNumber == s.SeasonNumber && m.EpisodeNumber == e.EpisodeNumber)?.Monitored ?? seasonMonitored;
                    write.Episodes.Add(new Episode
                    {
                        MediaItemId = item.Id,
                        SeasonNumber = s.SeasonNumber,
                        EpisodeNumber = e.EpisodeNumber,
                        ProviderId = e.ProviderId,
                        Title = e.Title,
                        AirDate = e.AirDate,
                        Monitored = episodeMonitored,
                    });
                }
            }
            await write.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new AddMediaResult(item, Created: true);
    }

    public async Task<List<MediaItem>> ListAsync(MediaType type, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.MediaItems.AsNoTracking().Where(m => m.MediaType == type).OrderBy(m => m.Title).ToListAsync(ct);
    }

    /// <summary>
    /// One grouped query for every series' episode availability. Applicable means monitored with a known
    /// air date on or before today; unaired episodes and unmonitored episodes (including per-episode
    /// Sonarr exceptions inside a monitored season) never count as missing. A series with no episodes, or
    /// with no applicable episode, is absent here and classifies as Unknown. This is deliberately not one
    /// query per poster card: a 200-series library is one aggregate query, not 200.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, SeriesAvailability>> SeriesAvailabilityAsync(CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var rows = await (from e in db.Episodes.AsNoTracking()
                          join m in db.MediaItems.AsNoTracking() on e.MediaItemId equals m.Id
                          where m.MediaType == MediaType.Series
                          group e by e.MediaItemId into g
                          select new
                          {
                              MediaItemId = g.Key,
                              Applicable = g.Count(e => e.Monitored && e.AirDate != null && e.AirDate <= today),
                              Available = g.Count(e => e.Monitored && e.AirDate != null && e.AirDate <= today && e.HasFile),
                          }).ToListAsync(ct);
        return rows.ToDictionary(
            r => r.MediaItemId,
            r => new SeriesAvailability(MediaDefaults.ClassifySeries(r.Available, r.Applicable), r.Available, r.Applicable));
    }

    public async Task<MediaItem?> GetAsync(int id, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.MediaItems.AsNoTracking().Include(m => m.Seasons).Include(m => m.Episodes).FirstOrDefaultAsync(m => m.Id == id, ct);
    }

    /// <summary>Changing RootFolder only affects where future downloads go. It never moves or re-checks files already on disk.</summary>
    public async Task<bool> UpdateAsync(int id, bool? monitored, int? qualityProfileId, string? rootFolder, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var item = await db.MediaItems.FindAsync([id], ct);
        if (item is null) return false;
        if (monitored is bool m) item.Monitored = m;
        if (qualityProfileId is int p)
        {
            if (!await db.QualityProfiles.AnyAsync(q => q.Id == p, ct)) throw new ArgumentException("Quality profile does not exist");
            item.QualityProfileId = p;
        }
        if (rootFolder is not null)
        {
            ValidateRoot(rootFolder);
            item.RootFolder = rootFolder.Trim();
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Syncs monitored flags from a legacy Sonarr/Radarr read into already-imported items, without creating
    /// items and without touching anything except Monitored flags. Preview (apply: false) never calls SaveChanges.
    /// Only items matched by (media type, TMDb id) are touched. Series-level, season-level, and episode-level
    /// flags are each compared independently, so a preview reports a series-only change even when nothing else
    /// changes. For episodes: per-episode flags win when supplied; when episode data was not supplied at all
    /// (legacy caller), episodes in a synced season take that season's flag; when episode data is marked
    /// unknown (Sonarr episode read failed), episodes are left alone and the series is reported, so a
    /// per-episode exception is never silently overwritten by a season flag. Quality profile, root folder,
    /// titles, and file state are never touched.
    /// </summary>
    public async Task<MonitoringSyncResult> SyncLegacyMonitoringAsync(IReadOnlyList<LegacyItem> legacy, bool apply, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var items = await db.MediaItems.Include(m => m.Seasons).Include(m => m.Episodes).ToListAsync(ct);
        var byKey = items.ToDictionary(m => (m.MediaType, m.ProviderId), m => m);

        var changes = new List<MonitoringChange>();
        var unmatched = new List<string>();
        var episodesUnknownTitles = new List<string>();
        var checkedCount = 0;
        var skippedNoTmdb = 0;

        foreach (var leg in legacy)
        {
            if (leg.TmdbId is not int tmdb) { skippedNoTmdb++; continue; }
            if (!byKey.TryGetValue((leg.Type, tmdb.ToString()), out var item)) { unmatched.Add(leg.Title); continue; }
            checkedCount++;

            // Computed before any mutation, so preview (apply: false) reports it exactly as apply would.
            var seriesBefore = item.Monitored;
            var monitoredChanged = seriesBefore != leg.Monitored;
            var seasonChanges = new List<SeasonChange>();
            var episodeChanges = new List<EpisodeChange>();

            if (apply && monitoredChanged) item.Monitored = leg.Monitored;

            if (leg.Type == MediaType.Series)
            {
                if (leg.Seasons is { Count: > 0 })
                {
                    foreach (var ls in leg.Seasons)
                    {
                        var season = item.Seasons.FirstOrDefault(s => s.SeasonNumber == ls.SeasonNumber);
                        if (season is null) continue;
                        if (season.Monitored != ls.Monitored)
                        {
                            seasonChanges.Add(new SeasonChange(ls.SeasonNumber, season.Monitored, ls.Monitored));
                            if (apply) season.Monitored = ls.Monitored;
                        }
                    }
                }

                if (leg.EpisodesUnknown)
                {
                    episodesUnknownTitles.Add(leg.Title);
                }
                else if (leg.Episodes is not null)
                {
                    var byEpisode = leg.Episodes.ToDictionary(e => (e.SeasonNumber, e.EpisodeNumber));
                    foreach (var ep in item.Episodes)
                    {
                        if (byEpisode.TryGetValue((ep.SeasonNumber, ep.EpisodeNumber), out var le) && ep.Monitored != le.Monitored)
                        {
                            episodeChanges.Add(new EpisodeChange(ep.SeasonNumber, ep.EpisodeNumber, ep.Monitored, le.Monitored));
                            if (apply) ep.Monitored = le.Monitored;
                        }
                    }
                }
                else if (leg.Seasons is { Count: > 0 })
                {
                    // No episode data supplied: fall back to season flags for episodes in synced seasons.
                    foreach (var ls in leg.Seasons)
                    {
                        foreach (var ep in item.Episodes.Where(e => e.SeasonNumber == ls.SeasonNumber))
                        {
                            if (ep.Monitored != ls.Monitored)
                            {
                                episodeChanges.Add(new EpisodeChange(ep.SeasonNumber, ep.EpisodeNumber, ep.Monitored, ls.Monitored));
                                if (apply) ep.Monitored = ls.Monitored;
                            }
                        }
                    }
                }
            }

            if (monitoredChanged || seasonChanges.Count > 0 || episodeChanges.Count > 0)
                changes.Add(new MonitoringChange(item.Title, leg.Type == MediaType.Movie ? "movie" : "series", seriesBefore, leg.Monitored, monitoredChanged, seasonChanges.Count, episodeChanges.Count, leg.EpisodesUnknown, tmdb, seasonChanges, episodeChanges));
        }

        if (apply) await db.SaveChangesAsync(ct);
        return new MonitoringSyncResult(changes, unmatched, skippedNoTmdb, checkedCount, episodesUnknownTitles);
    }

    /// <summary>Sets a season's monitored flag and, with it, every episode in that season. File state is untouched.</summary>
    public async Task<bool> UpdateSeasonMonitoredAsync(int mediaItemId, int seasonNumber, bool monitored, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var season = await db.Seasons.FirstOrDefaultAsync(s => s.MediaItemId == mediaItemId && s.SeasonNumber == seasonNumber, ct);
        if (season is null) return false;
        season.Monitored = monitored;
        await db.Episodes.Where(e => e.MediaItemId == mediaItemId && e.SeasonNumber == seasonNumber)
            .ExecuteUpdateAsync(e => e.SetProperty(x => x.Monitored, monitored), ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Sets a single episode's monitored flag. Its season flag is left alone.</summary>
    public async Task<bool> UpdateEpisodeMonitoredAsync(int episodeId, bool monitored, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var episode = await db.Episodes.FindAsync([episodeId], ct);
        if (episode is null) return false;
        episode.Monitored = monitored;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        // Removes OneArr's record only. Files on disk and qBittorrent torrents are never touched.
        await using var db = await _db.CreateDbContextAsync(ct);
        var item = await db.MediaItems.FindAsync([id], ct);
        if (item is null) return false;
        db.MediaItems.Remove(item);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<WantedList> WantedAsync(CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        // Active downloads only: a Missing/Failed job does not count, so a stuck download is visible as wanted
        // with no download in progress rather than hidden behind an active flag.
        var active = (await db.DownloadJobs.AsNoTracking()
            .Where(j => j.Status == DownloadStatus.Queued || j.Status == DownloadStatus.Downloading || j.Status == DownloadStatus.Completed)
            .Select(j => new ValueTuple<int?, int?>(j.MediaItemId, j.EpisodeId)).ToListAsync(ct)).ToHashSet();
        var movies = await db.MediaItems.AsNoTracking()
            .Where(m => m.MediaType == MediaType.Movie && m.Monitored && !m.HasFile)
            .OrderBy(m => m.Title).ToListAsync(ct);
        var episodes = await (from e in db.Episodes.AsNoTracking()
                              join m in db.MediaItems.AsNoTracking() on e.MediaItemId equals m.Id
                              where m.Monitored && e.Monitored && !e.HasFile && e.AirDate != null && e.AirDate <= today
                              orderby m.Title, e.SeasonNumber, e.EpisodeNumber
                              select new WantedEpisode(m.Id, e.Id, m.Title, e.SeasonNumber, e.EpisodeNumber, e.Title, e.AirDate)).ToListAsync(ct);
        // Wanted only ever lists monitored items. These counts are the unmonitored counterpart, so the UI can
        // say plainly how much "missing" material is deliberately unmonitored rather than actually wanted.
        var unmonitoredMovies = await db.MediaItems.CountAsync(m => m.MediaType == MediaType.Movie && !m.Monitored && !m.HasFile, ct);
        var unmonitoredEpisodes = await (from e in db.Episodes.AsNoTracking()
                                         join m in db.MediaItems.AsNoTracking() on e.MediaItemId equals m.Id
                                         where (!m.Monitored || !e.Monitored) && !e.HasFile && e.AirDate != null && e.AirDate <= today
                                         select e).CountAsync(ct);
        return new WantedList(
            movies,
            episodes.Select(e => e with { HasActiveDownload = active.Contains(((int?)e.MediaItemId, (int?)e.EpisodeId)) }).ToList(),
            unmonitoredMovies, unmonitoredEpisodes,
            movies.Select(m => (m.Id, active.Contains(((int?)m.Id, (int?)null)))).ToDictionary(x => x.Id, x => x.Item2));
    }

    public static void ValidateRoot(string rootFolder)
    {
        var problem = OneArr.Infrastructure.Library.LibraryPaths.ValidateRoot(rootFolder, OperatingSystem.IsWindows());
        if (problem is not null) throw new ArgumentException(problem);
    }

    /// <summary>Sanitised folder name for a title, e.g. "Dune: Part Two" becomes "Dune Part Two".</summary>
    public static string SafeFolderName(string title)
    {
        var cleaned = UnsafeFolderChars.Replace(title, " ").Trim().Trim('.');
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");
        return cleaned.Length == 0 ? "Untitled" : cleaned;
    }

    private static Task<MediaItem?> FindAsync(OneArrDbContext db, MediaType type, string providerId, CancellationToken ct) =>
        db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.MediaType == type && m.Provider == "tmdb" && m.ProviderId == providerId, ct);
}

public sealed record WantedEpisode(int MediaItemId, int EpisodeId, string SeriesTitle, int SeasonNumber, int EpisodeNumber, string EpisodeTitle, DateOnly? AirDate, bool HasActiveDownload = false);

/// <summary>One season's monitored change: the season's identity, its current value, and the proposed value.</summary>
public sealed record SeasonChange(int SeasonNumber, bool Before, bool After);
/// <summary>One episode's monitored change: the episode's identity, its current value, and the proposed value.</summary>
public sealed record EpisodeChange(int SeasonNumber, int EpisodeNumber, bool Before, bool After);
public sealed record MonitoringChange(string Title, string Type, bool SeriesMonitoredBefore, bool SeriesMonitoredAfter, bool MonitoredChanged, int SeasonsChanged, int EpisodesChanged, bool EpisodesUnknown, int TmdbId, IReadOnlyList<SeasonChange> SeasonChanges, IReadOnlyList<EpisodeChange> EpisodeChanges);
public sealed record MonitoringSyncResult(IReadOnlyList<MonitoringChange> Changes, IReadOnlyList<string> UnmatchedTitles, int SkippedNoTmdb, int ItemsChecked, IReadOnlyList<string> EpisodesUnknownTitles);

public sealed record WantedList(IReadOnlyList<MediaItem> Movies, IReadOnlyList<WantedEpisode> Episodes, int UnmonitoredMoviesWithoutFile = 0, int UnmonitoredEpisodesWithoutFile = 0, IReadOnlyDictionary<int, bool>? MovieHasActiveDownload = null);

/// <summary>Episode-based series availability for one series: status plus the counts behind it.</summary>
public sealed record SeriesAvailability(string Status, int AvailableEpisodes, int ApplicableEpisodes);
