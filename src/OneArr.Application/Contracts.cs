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

public interface IIndexerClient
{
    Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(IndexerConfig indexer, ReleaseSearch search, CancellationToken ct);
}

public interface IDownloadClient
{
    Task<string> AddAsync(DownloadClientConfig client, string link, string category, CancellationToken ct);
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

public sealed record MetadataDetails(string ProviderId, string Title, int? Year, string? Overview, string? PosterPath, string? ImdbId, int? TvdbId, int SeasonCount);

public sealed record SeasonInfo(int SeasonNumber, IReadOnlyList<EpisodeInfo> Episodes);

public sealed record EpisodeInfo(int EpisodeNumber, string ProviderId, string Title, DateOnly? AirDate);

public sealed record ReleaseSearch(MediaType Type, string Title, int? Year, int? Season, int? Episode, string Category);

public sealed record ReleaseCandidate(
    string Title,
    string Link,
    long Size,
    int Seeders,
    string? InfoHash,
    string Indexer,
    IReadOnlyList<int> Categories);

public sealed record ParsedRelease(
    string Title,
    int? Year,
    int? Resolution,
    string Source,
    int? Season,
    IReadOnlyList<int> Episodes,
    bool IsSeasonPack,
    bool IsCam,
    bool IsUnsafeName);

public sealed record ReleaseTarget(
    MediaType Type,
    string Title,
    int? Year,
    int? Season,
    int? Episode,
    QualityProfile Profile,
    int? CurrentResolution,
    bool AlreadyHasFile,
    IReadOnlySet<string> KnownInfoHashes);

public sealed record ReleaseDecision(
    ReleaseCandidate Candidate,
    ParsedRelease Parsed,
    bool Accepted,
    int Score,
    IReadOnlyList<string> Reasons);

public sealed record DownloadClientItem(string Hash, string Name, string State, double Progress, string? ContentPath, string Category, long Size);

public sealed record ImportRequest(
    string SourcePath,
    string DestinationRoot,
    string DestinationRelativePath,
    string TorrentRoot,
    bool DryRun,
    int? DownloadJobId);

public sealed record ImportOutcome(ImportAction Action, string DestinationPath, bool Success, string Message);

public sealed record LegacyArrSource(string Kind, string BaseUrl, string ApiKey);

public sealed record LegacySeason(int SeasonNumber, bool Monitored);

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
    IReadOnlyList<LegacySeason>? Seasons = null);

public class ProviderNotConfiguredException : Exception
{
    public ProviderNotConfiguredException(string message) : base(message) { }
}

public class ExternalServiceException : Exception
{
    public ExternalServiceException(string message, Exception? inner = null) : base(message, inner) { }
}
