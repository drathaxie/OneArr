using OneArr.Domain;

namespace OneArr.Application;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Metadata lookup (TMDb initially). Implementations must never expose API keys to callers.</summary>
public interface IMetadataProvider
{
    Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(MediaType type, string query, CancellationToken ct);
    Task<MetadataDetails> GetDetailsAsync(MediaType type, string providerId, CancellationToken ct);
    Task<IReadOnlyList<SeasonInfo>> GetSeasonsAsync(string providerId, CancellationToken ct);
}

/// <summary>An episode's exact first-air moment, for deciding when it has really aired.</summary>
public sealed record EpisodeAirTime(int Season, int Episode, DateTimeOffset AirsUtc);

/// <summary>
/// Exact air times. TMDb gives an air date only, so an evening episode looks aired from midnight UTC. This source is
/// looked up by the TVDB or IMDb id TMDb reports, never by title.
/// </summary>
public interface IAirTimeProvider
{
    /// <summary>The provider's show id, or null when it does not know the show.</summary>
    Task<int?> FindShowAsync(int? tvdbId, string? imdbId, CancellationToken ct);
    Task<IReadOnlyList<EpisodeAirTime>> GetAirTimesAsync(int showId, CancellationToken ct);
}

public interface IIndexerClient
{
    Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(IndexerConfig indexer, ReleaseSearch search, CancellationToken ct);

    /// <summary>The indexer's newest releases, without a query (RSS sync). Null when this kind of indexer has no recent feed.</summary>
    Task<IReadOnlyList<ReleaseCandidate>?> RecentAsync(IndexerConfig indexer, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ReleaseCandidate>?>(null);
}

public interface IDownloadClient
{
    Task<string> AddAsync(DownloadClientConfig client, string link, string category, CancellationToken ct);
    /// <summary>Submit previously authenticated .torrent bytes directly to the download client.</summary>
    Task<string> AddTorrentAsync(DownloadClientConfig client, byte[] torrent, string category, CancellationToken ct) =>
        throw new ExternalServiceException("This download client does not support authenticated torrent uploads");
    Task<IReadOnlyList<DownloadClientItem>> ListAsync(DownloadClientConfig client, string category, CancellationToken ct);
}

public interface IReleaseParser
{
    ParsedRelease Parse(string releaseName);
}

public interface IReleaseRanker
{
    ReleaseDecision Evaluate(ReleaseCandidate candidate, ReleaseTarget target);
}

public interface IImporter
{
    Task<ImportOutcome> ImportAsync(ImportRequest request, CancellationToken ct);
}

public interface ILegacyArrReader
{
    /// <summary>Read-only. Implementations must only issue GET requests.</summary>
    Task<IReadOnlyList<LegacyItem>> ReadAsync(LegacyArrSource source, CancellationToken ct);
}

public sealed record MetadataSearchResult(string ProviderId, string Title, int? Year, string? Overview, string? PosterPath);

public sealed record MetadataDetails(string ProviderId, string Title, int? Year, string? Overview, string? PosterPath, string? ImdbId, int? TvdbId, int SeasonCount,
    IReadOnlyList<string>? AlternateTitles = null, MovieReleaseDates? MovieReleases = null);

/// <summary>Earliest release of each kind across countries.</summary>
public sealed record MovieReleaseDates(DateOnly? Theatrical, DateOnly? Digital, DateOnly? Physical);

public sealed record SeasonInfo(int SeasonNumber, IReadOnlyList<EpisodeInfo> Episodes);

public sealed record EpisodeInfo(int EpisodeNumber, string ProviderId, string Title, DateOnly? AirDate);

public sealed record ReleaseSearch(MediaType Type, string Title, int? Year, int? Season, int? Episode, string Category, int? AbsoluteEpisode = null);

public sealed record ReleaseCandidate(
    string Title,
    string Link,
    long Size,
    int Seeders,
    string? InfoHash,
    string Indexer,
    IReadOnlyList<int> Categories,
    DateTimeOffset? PublishDateUtc = null);

public sealed record ParsedRelease(
    string Title,
    int? Year,
    int? Resolution,
    string Source,
    int? Season,
    IReadOnlyList<int> Episodes,
    bool IsSeasonPack,
    bool IsCam,
    bool IsUnsafeName,
    int Revision = 1,
    IReadOnlyList<int>? AbsoluteEpisodes = null);

public sealed record ReleaseTarget(
    MediaType Type,
    string Title,
    int? Year,
    int? Season,
    int? Episode,
    QualityProfile Profile,
    int? CurrentResolution,
    bool AlreadyHasFile,
    IReadOnlySet<string> KnownInfoHashes,
    IReadOnlyList<string>? Aliases = null,
    IReadOnlySet<string>? Blocklisted = null,
    string? CurrentSource = null,
    int CurrentRevision = 1,
    int? AbsoluteEpisode = null);

public sealed record ReleaseDecision(
    ReleaseCandidate Candidate,
    ParsedRelease Parsed,
    bool Accepted,
    int Score,
    IReadOnlyList<string> Reasons);

public sealed record DownloadClientItem(string Hash, string Name, string State, double Progress, string? ContentPath, string Category, long Size, long DownloadSpeedBps = 0, long EtaSeconds = -1);

public sealed record ImportRequest(
    string SourcePath,
    string DestinationRoot,
    string DestinationRelativePath,
    string TorrentRoot,
    bool DryRun,
    int? DownloadJobId,
    string? DestinationFileName = null);

public sealed record ImportOutcome(ImportAction Action, string DestinationPath, bool Success, string Message);

public sealed record LegacyArrSource(string Kind, string BaseUrl, string ApiKey);

public sealed record LegacySeason(int SeasonNumber, bool Monitored);

/// <summary>One episode's monitored flag from Sonarr. Season/episode numbers are Sonarr's, not TMDb's.</summary>
public sealed record LegacyEpisode(int SeasonNumber, int EpisodeNumber, bool Monitored);

public sealed record LegacyItem(
    string Source,
    MediaType Type,
    string Title,
    int? Year,
    int? TmdbId,
    int? TvdbId,
    string? ImdbId,
    string? Path,
    bool Monitored,
    string? Error,
    IReadOnlyList<LegacySeason>? Seasons = null,
    // Null means episode data was not supplied (caller falls back to season flags, if any).
    // EpisodesUnknown means the reader tried and failed to get episode data: sync must leave episodes alone.
    IReadOnlyList<LegacyEpisode>? Episodes = null,
    bool EpisodesUnknown = false);

public class ProviderNotConfiguredException : Exception
{
    public ProviderNotConfiguredException(string message) : base(message) { }
}

public class ExternalServiceException : Exception
{
    public ExternalServiceException(string message, Exception? inner = null) : base(message, inner) { }
}
