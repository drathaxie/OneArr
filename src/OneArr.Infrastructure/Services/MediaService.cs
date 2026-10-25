using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Persistence;

namespace OneArr.Infrastructure.Services;

public sealed record AddMediaRequest(MediaType Type, string ProviderId, int QualityProfileId, string RootFolder, bool Monitored, IReadOnlyList<int>? MonitorSeasons, bool MonitorAllSeasons);

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
                    write.Episodes.Add(new Episode
                    {
                        MediaItemId = item.Id,
                        SeasonNumber = s.SeasonNumber,
                        EpisodeNumber = e.EpisodeNumber,
                        ProviderId = e.ProviderId,
                        Title = e.Title,
                        AirDate = e.AirDate,
                        Monitored = seasonMonitored,
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
        var movies = await db.MediaItems.AsNoTracking()
            .Where(m => m.MediaType == MediaType.Movie && m.Monitored && !m.HasFile)
            .OrderBy(m => m.Title).ToListAsync(ct);
        var episodes = await (from e in db.Episodes.AsNoTracking()
                              join m in db.MediaItems.AsNoTracking() on e.MediaItemId equals m.Id
                              where m.Monitored && e.Monitored && !e.HasFile && e.AirDate != null && e.AirDate <= today
                              orderby m.Title, e.SeasonNumber, e.EpisodeNumber
                              select new WantedEpisode(m.Id, e.Id, m.Title, e.SeasonNumber, e.EpisodeNumber, e.Title, e.AirDate)).ToListAsync(ct);
        return new WantedList(movies, episodes);
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

public sealed record WantedEpisode(int MediaItemId, int EpisodeId, string SeriesTitle, int SeasonNumber, int EpisodeNumber, string EpisodeTitle, DateOnly? AirDate);

public sealed record WantedList(IReadOnlyList<MediaItem> Movies, IReadOnlyList<WantedEpisode> Episodes);
