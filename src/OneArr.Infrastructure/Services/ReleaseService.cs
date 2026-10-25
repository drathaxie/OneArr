using OneArr.Application;
using Microsoft.EntityFrameworkCore;

using OneArr.Domain;
using OneArr.Infrastructure.Persistence;
using OneArr.Infrastructure.Http;
using OneArr.Infrastructure.Security;

namespace OneArr.Infrastructure.Services;

public sealed record IndexerSearchError(string Indexer, string Message);

public sealed record ReleaseSearchResult(IReadOnlyList<ReleaseDecision> Decisions, IReadOnlyList<IndexerSearchError> IndexerErrors);

public sealed record GrabRequest(int MediaItemId, int? EpisodeId, string ReleaseTitle, string Link, string? InfoHash);

/// <summary>Manual release search and manual grab. Automated grabbing is gated by the automation setting and off by default.</summary>
public sealed class ReleaseService
{
    private static readonly HashSet<DownloadStatus> ActiveStatuses = [DownloadStatus.Queued, DownloadStatus.Downloading, DownloadStatus.Completed, DownloadStatus.Imported, DownloadStatus.Missing];
    private readonly IDbContextFactory<OneArrDbContext> _db;
    private readonly IIndexerClient _indexer;
    private readonly IReleaseRanker _ranker;
    private readonly IDownloadClient _downloads;
    private readonly TimeProvider _time;

    public ReleaseService(IDbContextFactory<OneArrDbContext> db, IIndexerClient indexer, IReleaseRanker ranker, IDownloadClient downloads, TimeProvider time)
    {
        _db = db;
        _indexer = indexer;
        _ranker = ranker;
        _downloads = downloads;
        _time = time;
    }

    public async Task<ReleaseSearchResult> SearchAsync(int mediaItemId, int? episodeId, CancellationToken ct)
    {
        var (target, search, indexers) = await BuildTargetAsync(mediaItemId, episodeId, ct);
        var errors = new List<IndexerSearchError>();
        var found = new List<ReleaseCandidate>();

        foreach (var indexer in indexers)
        {
            try
            {
                found.AddRange(await _indexer.SearchAsync(indexer, search, ct));
            }
            catch (Exception ex) when (ex is ExternalServiceException or ArgumentException or UriFormatException or ProviderNotConfiguredException)
            {
                errors.Add(new IndexerSearchError(indexer.Name, OutboundHttp.Redact(ex.Message)));
            }
        }

        var decisions = found
            .DistinctBy(c => (c.Title.ToLowerInvariant(), c.Link))
            .Select(c => _ranker.Evaluate(c, target))
            .OrderByDescending(d => d.Accepted)
            .ThenByDescending(d => d.Score)
            .ThenByDescending(d => d.Candidate.Seeders)
            .ToList();
        return new ReleaseSearchResult(decisions, errors);
    }

    public async Task<DownloadJob> GrabAsync(GrabRequest request, CancellationToken ct)
    {
        if (!UrlGuard.IsAllowedDownloadLink(request.Link)) throw new ArgumentException("Link must be a magnet or http(s) URL");
        var (target, _, _) = await BuildTargetAsync(request.MediaItemId, request.EpisodeId, ct);

        // Re-evaluate server-side so a manual grab cannot bypass duplicate, wrong-media or unsafe-name checks.
        var candidate = new ReleaseCandidate(request.ReleaseTitle, request.Link, 1, 1, request.InfoHash, "manual", []);
        var decision = _ranker.Evaluate(candidate, target);
        var hardReasons = decision.Reasons.Where(r => !r.StartsWith("No seeders", StringComparison.Ordinal) && !r.StartsWith("Release reports no size", StringComparison.Ordinal)).ToList();
        if (hardReasons.Count > 0) throw new ArgumentException("Release rejected: " + string.Join("; ", hardReasons));

        await using var db = await _db.CreateDbContextAsync(ct);
        // One active download per movie, or per episode. Queued, downloading and completed-but-not-yet-imported all count.
        var alreadyActive = await db.DownloadJobs.AnyAsync(j => j.MediaItemId == request.MediaItemId
            && j.EpisodeId == request.EpisodeId
            && (j.Status == DownloadStatus.Queued || j.Status == DownloadStatus.Downloading || j.Status == DownloadStatus.Completed), ct);
        if (alreadyActive) throw new ArgumentException("An active download already exists for this item");

        var item = await db.MediaItems.AsNoTracking().FirstAsync(m => m.Id == request.MediaItemId, ct);
        var clients = await db.DownloadClients.AsNoTracking().Where(c => c.Enabled).OrderBy(c => c.Id).ToListAsync(ct);
        var client = clients.FirstOrDefault() ?? throw new ArgumentException("No enabled download client is configured");
        var category = item.MediaType == MediaType.Movie ? client.MovieCategory : client.TvCategory;

        var hash = request.InfoHash?.ToLowerInvariant();
        var returnedHash = await _downloads.AddAsync(client, request.Link, category, ct);
        hash ??= string.IsNullOrEmpty(returnedHash) ? null : returnedHash;

        var job = new DownloadJob
        {
            MediaItemId = item.Id,
            EpisodeId = request.EpisodeId,
            MediaType = item.MediaType,
            ReleaseTitle = request.ReleaseTitle,
            Link = request.Link,
            Hash = hash,
            Category = category,
            Status = DownloadStatus.Queued,
            CreatedAt = _time.GetUtcNow(),
            UpdatedAt = _time.GetUtcNow(),
        };
        db.DownloadJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job;
    }

    private async Task<(ReleaseTarget Target, ReleaseSearch Search, List<IndexerConfig> Indexers)> BuildTargetAsync(int mediaItemId, int? episodeId, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var item = await db.MediaItems.AsNoTracking().Include(m => m.QualityProfile).FirstOrDefaultAsync(m => m.Id == mediaItemId, ct)
                   ?? throw new ArgumentException("Media item not found");
        var profile = item.QualityProfile ?? throw new ArgumentException("Media item has no quality profile");
        var episode = episodeId is int eid
            ? await db.Episodes.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eid && e.MediaItemId == mediaItemId, ct)
              ?? throw new ArgumentException("Episode does not belong to this series")
            : null;

        var hasFile = item.MediaType == MediaType.Movie ? item.HasFile : episode?.HasFile ?? false;
        var knownHashes = await db.DownloadJobs.AsNoTracking()
            .Where(j => j.Hash != null && j.Status != DownloadStatus.Failed)
            .Select(j => j.Hash!)
            .ToListAsync(ct);
        var target = new ReleaseTarget(
            item.MediaType,
            item.Title,
            item.Year,
            episode?.SeasonNumber,
            episode?.EpisodeNumber,
            profile,
            null,
            hasFile,
            new HashSet<string>(knownHashes.Select(h => h.ToLowerInvariant())));

        var category = item.MediaType == MediaType.Movie ? "2000" : "5000";
        var search = new ReleaseSearch(item.MediaType, item.Title, item.Year, episode?.SeasonNumber, episode?.EpisodeNumber, category);
        var indexers = await db.Indexers.AsNoTracking().Where(i => i.Enabled).OrderBy(i => i.Id).ToListAsync(ct);
        return (target, search, indexers);
    }
}
