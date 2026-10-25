namespace OneArr.Domain;

public static class SettingKeys
{
    public const string AdminPasswordHash = "auth.passwordHash";
    /// <summary>JSON written by the last complete library apply. Automatic grabbing reads it.</summary>
    public const string LibraryReviewState = "library.reviewState";
    public const string TmdbApiKey = "tmdb.apiKey";
    public const string AutomaticGrab = "automation.grab";
    public const string DryRunImport = "import.dryRun";
    public const string TorrentRoot = "paths.torrents";
    public const string MediaRoot = "paths.media";
}
