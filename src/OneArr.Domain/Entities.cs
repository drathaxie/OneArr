namespace OneArr.Domain;

public enum MediaType { Movie = 1, Series = 2 }

public enum DownloadStatus { Queued = 0, Downloading = 1, Completed = 2, Imported = 3, Failed = 4, Unknown = 5, Missing = 6 }

public enum ImportAction { DryRun = 0, Hardlink = 1, Copy = 2, Skipped = 3, Failed = 4, Deferred = 5,
    /// <summary>The file was imported into the library but matched no episode, so no episode was
    /// marked available. The job stays visibly unresolved instead of reporting fully imported.</summary>
    Unmatched = 6 }

/// <summary>Movie or series. Identity is (MediaType, Provider, ProviderId) so IDs never collide across media types.</summary>
public class MediaItem
{
    public int Id { get; set; }
    public MediaType MediaType { get; set; }
    public string Provider { get; set; } = "tmdb";
    public string ProviderId { get; set; } = "";
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string? Overview { get; set; }
    public string? PosterPath { get; set; }
    public bool Monitored { get; set; } = true;
    public int QualityProfileId { get; set; }
    public QualityProfile? QualityProfile { get; set; }
    public string RootFolder { get; set; } = "";
    public bool HasFile { get; set; }
    /// <summary>Path exactly as the migrated application stored it (may be a Linux container path). Never used as a Windows path directly.</summary>
    public string? LegacyPath { get; set; }
    public string? LegacySource { get; set; }
    /// <summary>Set when a complete library scan has checked this item. Null for items that existed before reconciliation.</summary>
    public DateTimeOffset? LibraryReviewedUtc { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Season> Seasons { get; set; } = new();
    public List<Episode> Episodes { get; set; } = new();
}

public class Season
{
    public int Id { get; set; }
    public int MediaItemId { get; set; }
    public int SeasonNumber { get; set; }
    public bool Monitored { get; set; } = true;
    public List<Episode> Episodes { get; set; } = new();
}

public class Episode
{
    public int Id { get; set; }
    public int MediaItemId { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public string Provider { get; set; } = "tmdb";
    public string ProviderId { get; set; } = "";
    public string Title { get; set; } = "";
    public DateOnly? AirDate { get; set; }
    public bool Monitored { get; set; } = true;
    public bool HasFile { get; set; }
}

public class QualityProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Lowest acceptable resolution (720, 1080, 2160).</summary>
    public int MinResolution { get; set; } = 720;
    /// <summary>Resolution at which upgrades stop being sought.</summary>
    public int CutoffResolution { get; set; } = 1080;
    public bool AllowWebSources { get; set; } = true;
}

public class IndexerConfig
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    /// <summary>Server-side only. Never returned by the API.</summary>
    public string ApiKey { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Whether this indexer is used for movie searches. Default true.</summary>
    public bool MoviesEnabled { get; set; } = true;
    /// <summary>Whether this indexer is used for TV searches. Default true.</summary>
    public bool TvEnabled { get; set; } = true;
    /// <summary>Search order: LOWER values are tried first; ties break by Id. Default 0 preserves Id order.</summary>
    public int Priority { get; set; } = 0;
}

/// <summary>Diagnostic status from the last connection test of one indexer. Written only by
/// connection tests, never by searches, so a failed search cannot masquerade as a failed test.</summary>
public class IndexerHealth
{
    public int IndexerId { get; set; }
    public DateTimeOffset? LastTestedAt { get; set; }
    /// <summary>Healthy or Failed from the last test; null when never tested. Disabled is derived at read time.</summary>
    public string? LastStatus { get; set; }
    /// <summary>Sanitized failure reason from the last failed test; null when the last test passed.</summary>
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class DownloadClientConfig
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>Server-side only. Never returned by the API.</summary>
    public string Password { get; set; } = "";
    public string MovieCategory { get; set; } = "onearr-movies";
    public string TvCategory { get; set; } = "onearr-tv";
    public bool Enabled { get; set; } = true;
}

public class DownloadJob
{
    public int Id { get; set; }
    public int? MediaItemId { get; set; }
    public int? EpisodeId { get; set; }
    public MediaType MediaType { get; set; }
    public string ReleaseTitle { get; set; } = "";
    public string Link { get; set; } = "";
    /// <summary>Correlation key from the download client (torrent info hash), used to detect duplicates and match queue items.</summary>
    public string? Hash { get; set; }
    public string Category { get; set; } = "";
    /// <summary>Owning download client. NULL for jobs created before client tracking: those are matched
    /// conservatively (hash preferred; name only when unique across all clients) and are only marked
    /// missing when no reachable enabled client reports them.</summary>
    public int? DownloadClientId { get; set; }
    /// <summary>Client display name, denormalized at grab time so a job still names its client if the
    /// client row is later deleted.</summary>
    public string ClientName { get; set; } = "";
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public double Progress { get; set; }
    public string ClientState { get; set; } = "";
    /// <summary>Live transfer rate in bytes per second as last reported by the owning client, 0 when unknown.</summary>
    public long DownloadSpeedBps { get; set; }
    /// <summary>ETA in seconds as last reported by the owning client, -1 when the client reports none.</summary>
    public long EtaSeconds { get; set; } = -1;
    public string? ContentPath { get; set; }
    public string? Error { get; set; }
    /// <summary>True when Status == Failed because an import failed (operator-retryable). False for
    /// download-side failures, which keep polling the client so recovery is possible.</summary>
    public bool ImportFailed { get; set; }
    /// <summary>Last time the owning client (or any client, for legacy jobs) reported this job.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }
    /// <summary>First time the job entered Completed. NULL for jobs completed before this was tracked.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Per-client reachability, upserted on every queue sync. This is what distinguishes "the
/// client is down" from "the download is missing": a job is never marked missing because its client
/// could not be reached.</summary>
public class DownloadClientHealth
{
    public int DownloadClientId { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    /// <summary>Last sync error for this client, if the most recent poll failed. NULL when healthy.</summary>
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A stored import preview: the plans a later apply must match exactly. The fingerprint
/// covers every planned file's source path, size, and last-write time plus its destination, so any
/// material change between preview and apply is caught and the apply is refused.</summary>
public class ImportPreview
{
    public int Id { get; set; }
    public int DownloadJobId { get; set; }
    public string Fingerprint { get; set; } = "";
    /// <summary>JSON array of ImportPlanDto (see DownloadService). Stored, not recomputed, so the
    /// apply step can show exactly what was previewed.</summary>
    public string PlansJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ImportRecord
{
    public int Id { get; set; }
    public int? DownloadJobId { get; set; }
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public ImportAction Action { get; set; }
    public bool Success { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class FileRecord
{
    public int Id { get; set; }
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset ScannedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class JobHistory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public bool Success { get; set; }
    public string? Message { get; set; }
}

/// <summary>Key/value application settings (TMDb key, admin password hash, flags).</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public enum SearchOutcome { None = 0, Grabbed = 1, NoAcceptableRelease = 2, IndexerError = 3, DownloadClientError = 4, SkippedActiveDownload = 5, Rejected = 6, SkippedMissingDownload = 7 }

/// <summary>
/// Persistent per-item search state, so restarts keep backoff and never re-search an item before its next attempt is due.
/// ItemKey is "movie:{id}" or "episode:{id}", which keeps movie and episode keys from colliding.
/// </summary>
public class SearchAttempt
{
    public int Id { get; set; }
    public string ItemKey { get; set; } = "";
    public int MediaItemId { get; set; }
    public int? EpisodeId { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public int ConsecutiveFailures { get; set; }
    public SearchOutcome LastOutcome { get; set; }
    public string? LastMessage { get; set; }
}

public enum LibraryRootState { Unknown = 0, Accessible = 1, Unavailable = 2, Invalid = 3 }

/// <summary>Present and Missing are verified facts. Inaccessible means the folder is offline. Unknown means the disk could not be read to decide.</summary>
public enum MediaFileStatus { Present = 0, Missing = 1, Inaccessible = 2, Unknown = 3 }

/// <summary>A folder OneArr scans for existing media. A media type can have several roots.</summary>
public class LibraryRoot
{
    public int Id { get; set; }
    public MediaType MediaType { get; set; }
    public string Path { get; set; } = "";
    /// <summary>Case-folded on Windows so C:\Media and c:\media are one root.</summary>
    public string PathKey { get; set; } = "";
    public string? Label { get; set; }
    public bool Enabled { get; set; } = true;
    public LibraryRootState LastState { get; set; } = LibraryRootState.Unknown;
    public string? LastMessage { get; set; }
    public DateTimeOffset? LastCheckedUtc { get; set; }
    /// <summary>When a complete, non-partial scan of this folder was last applied. Null means never.</summary>
    public DateTimeOffset? LastSuccessfulScanUtc { get; set; }
    /// <summary>True only when the most recent apply read this folder completely. A partial or failed scan sets it false.</summary>
    public bool LastScanComplete { get; set; }
}

/// <summary>Maps a path as Sonarr/Radarr saw it (often a Linux container path) to where the same folder is on this PC.</summary>
public class PathMapping
{
    public int Id { get; set; }
    public MediaType MediaType { get; set; }
    public string SourcePrefix { get; set; } = "";
    public string TargetPrefix { get; set; } = "";
}

/// <summary>A media file OneArr has verified on disk, linked to one movie or to one or more episodes.</summary>
public class MediaFile
{
    public int Id { get; set; }
    public string Path { get; set; } = "";
    /// <summary>Lookup key: the path, case-folded on Windows. Unique, so rescanning never duplicates a file.</summary>
    public string PathKey { get; set; } = "";
    public long SizeBytes { get; set; }
    public MediaFileStatus Status { get; set; } = MediaFileStatus.Present;
    public int? LibraryRootId { get; set; }
    public int? MediaItemId { get; set; }
    public int? Resolution { get; set; }
    public string MatchMethod { get; set; } = "";
    public DateTimeOffset FirstSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastVerifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<MediaFileEpisode> EpisodeLinks { get; set; } = new();
}

public class MediaFileEpisode
{
    public int MediaFileId { get; set; }
    public MediaFile? MediaFile { get; set; }
    public int EpisodeId { get; set; }
}

/// <summary>A reviewed, read-only scan result. Applying it is a separate, explicit step that revalidates each file.</summary>
public class LibraryScanPlan
{
    public int Id { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AppliedUtc { get; set; }
    public string PlanJson { get; set; } = "";
}
