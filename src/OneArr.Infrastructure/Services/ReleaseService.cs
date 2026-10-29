using OneArr.Application;
using Microsoft.EntityFrameworkCore;

using OneArr.Domain;
using OneArr.Infrastructure.Persistence;
using OneArr.Infrastructure.Http;
using OneArr.Infrastructure.Security;
using OneArr.Infrastructure.Providers;

namespace OneArr.Infrastructure.Services;

public sealed record IndexerSearchError(string Indexer, string Message);

/// <summary>Per-indexer transparency for one manual search. Status is results, no-results, failed,
/// or skipped (disabled, or movies/TV not enabled for this media type). Never hides one indexer's
/// failure behind another's results.</summary>
public sealed record IndexerSearchOutcome(int Id, string Name, string Status, int ResultCount, string? Message);

public sealed record ReleaseSearchResult(IReadOnlyList<ReleaseDecision> Decisions, IReadOnlyList<IndexerSearchError> IndexerErrors, IReadOnlyList<IndexerSearchOutcome> IndexerOutcomes);

public sealed record GrabRequest(int MediaItemId, int? EpisodeId, string ReleaseTitle, string Link, string? InfoHash, int? DownloadClientId = null, bool SeasonPackOnly = false);

/// <summary>Manual release search and manual grab. Automated grabbing is gated by the automation setting and off by default.</summary>
public sealed class ReleaseService
{
    private static readonly HashSet<DownloadStatus> ActiveStatuses = [DownloadStatus.Queued, DownloadStatus.Downloading, DownloadStatus.Completed, DownloadStatus.Imported, DownloadStatus.Missing];
    private readonly IDbContextFactory<OneArrDbContext> _db;
    private readonly IIndexerClient _indexer;
    private readonly IReleaseRanker _ranker;
    private readonly IDownloadClient _downloads;
    private readonly TimeProvider _time;
    private readonly NativeTrackerClient? _native;

    public ReleaseService(IDbContextFactory<OneArrDbContext> db, IIndexerClient indexer,
        IReleaseRanker ranker, IDownloadClient downloads, TimeProvider time, NativeTrackerClient? native = null)
    {
        _db = db;
        _indexer = indexer;
        _ranker = ranker;
        _downloads = downloads;
        _time = time;
        _native = native;
    }

    public async Task<ReleaseSearchResult> SearchAsync(int mediaItemId, int? episodeId, CancellationToken ct, bool seasonPackOnly = false)
    {
        if (seasonPackOnly && episodeId is null) throw new ArgumentException("Season-pack search requires an episode in the target season");
        var (target, search, indexers) = await BuildTargetAsync(mediaItemId, episodeId, ct);
        var outcomes = new List<IndexerSearchOutcome>();
        var found = new List<ReleaseCandidate>();

        foreach (var indexer in indexers)
        {
            if (!indexer.Enabled) { outcomes.Add(new IndexerSearchOutcome(indexer.Id, indexer.Name, "skipped", 0, "Disabled")); continue; }
            if (target.Type == MediaType.Movie && !indexer.MoviesEnabled) { outcomes.Add(new IndexerSearchOutcome(indexer.Id, indexer.Name, "skipped", 0, "Movies not enabled")); continue; }
            if (target.Type == MediaType.Series && !indexer.TvEnabled) { outcomes.Add(new IndexerSearchOutcome(indexer.Id, indexer.Name, "skipped", 0, "TV not enabled")); continue; }
            try
            {
                var results = await _indexer.SearchAsync(indexer, search, ct);
                found.AddRange(results);
                outcomes.Add(new IndexerSearchOutcome(indexer.Id, indexer.Name, results.Count > 0 ? "results" : "no-results", results.Count, null));
            }
            catch (Exception ex) when (ex is ExternalServiceException or ArgumentException or UriFormatException or ProviderNotConfiguredException)
            {
                outcomes.Add(new IndexerSearchOutcome(indexer.Id, indexer.Name, "failed", 0, OutboundHttp.Redact(ex.Message)));
            }
        }

        var decisions = found
            .Where(c => !seasonPackOnly || (new ReleaseParser().Parse(c.Title) is var parsed && parsed.IsSeasonPack && parsed.Season == target.Season))
            .DistinctBy(c => (c.Title.ToLowerInvariant(), c.Link))
            .Select(c => _ranker.Evaluate(c, target))
            .OrderByDescending(d => d.Accepted)
            .ThenByDescending(d => d.Score)
            .ThenByDescending(d => d.Candidate.Seeders)
            .ToList();
        // IndexerErrors stays populated for backward compatibility; IndexerOutcomes is authoritative.
        var errors = outcomes.Where(o => o.Status == "failed").Select(o => new IndexerSearchError(o.Name, o.Message ?? "")).ToList();
        return new ReleaseSearchResult(decisions, errors, outcomes);
    }

    public async Task<DownloadJob> GrabAsync(GrabRequest request, CancellationToken ct)
    {
        if (!UrlGuard.IsAllowedDownloadLink(request.Link)) throw new ArgumentException("Link must be a magnet or http(s) URL");
        if (request.SeasonPackOnly && request.EpisodeId is null) throw new ArgumentException("Season-pack grab requires an episode in the selected season");
        var (target, _, _) = await BuildTargetAsync(request.MediaItemId, request.EpisodeId, ct);
        if (request.SeasonPackOnly)
        {
            var pack = new ReleaseParser().Parse(request.ReleaseTitle);
            if (!pack.IsSeasonPack || pack.Season != target.Season)
                throw new ArgumentException("Release is not a season pack for the selected season");
        }

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
        var missing = await db.DownloadJobs.AnyAsync(j => j.MediaItemId == request.MediaItemId
            && j.EpisodeId == request.EpisodeId
            && j.Status == DownloadStatus.Missing, ct);
        if (missing) throw new ArgumentException("A previous download for this item is missing from qBittorrent. Clear it in Queue before grabbing again");

        var item = await db.MediaItems.AsNoTracking().FirstAsync(m => m.Id == request.MediaItemId, ct);
        var clients = await db.DownloadClients.AsNoTracking().Where(c => c.Enabled).OrderBy(c => c.Id).ToListAsync(ct);
        // An explicitly selected client must exist and be enabled; otherwise the first enabled
        // client is the default, exactly as before. The chosen client's id is recorded on the job.
        var client = request.DownloadClientId is int wantedId
            ? clients.FirstOrDefault(x => x.Id == wantedId) ?? throw new ArgumentException("Selected download client does not exist or is disabled")
            : clients.FirstOrDefault() ?? throw new ArgumentException("No enabled download client is configured");
        var category = item.MediaType == MediaType.Movie ? client.MovieCategory : client.TvCategory;

        var hash = request.InfoHash?.ToLowerInvariant();
        string returnedHash;
        if (Uri.TryCreate(request.Link, UriKind.Absolute, out var torrentUrl) &&
            NativeTrackerClient.IsTorrentLeechDownload(torrentUrl))
        {
            // qBittorrent cannot fetch a private URL without OneArr's
            // authenticated session. Fetch with the native adapter and upload
            // the .torrent body directly, never forwarding site cookies.
            var configured = await db.Indexers.AsNoTracking().Where(i => i.Enabled).ToListAsync(ct);
            var owner = configured.FirstOrDefault(i =>
                TrackerSecretBox.IsNative(i.ApiKey) &&
                Uri.TryCreate(i.BaseUrl, UriKind.Absolute, out var u) &&
                u.Authority.Equals(torrentUrl.Authority, StringComparison.OrdinalIgnoreCase));
            if (owner is null || _native is null)
                throw new ExternalServiceException("TorrentLeech release requires a configured and enabled native tracker");
            var bytes = await _native.DownloadTorrentAsync(owner, torrentUrl, ct);
            returnedHash = await _downloads.AddTorrentAsync(client, bytes, category, ct);
        }
        else
        {
            returnedHash = await _downloads.AddAsync(client, request.Link, category, ct);
        }
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
            // Client identity is recorded at grab time: queue sync may only ever match this job
            // against this client's list, so another client's silence can never mark it missing.
            DownloadClientId = client.Id,
            ClientName = client.Name,
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
        // All indexers, in search order (lower Priority first, ties by Id): SearchAsync reports
        // skipped ones instead of silently omitting them, so the UI can show what was not tried.
        var indexers = await db.Indexers.AsNoTracking().OrderBy(i => i.Priority).ThenBy(i => i.Id).ToListAsync(ct);
        return (target, search, indexers);
    }
}
