namespace OneArr.Domain;

public static class SettingKeys
{
    public const string AdminPasswordHash = "auth.passwordHash";
    /// <summary>JSON written by the last complete library apply. Automatic grabbing reads it.</summary>
    public const string LibraryReviewState = "library.reviewState";
    public const string TmdbApiKey = "tmdb.apiKey";
    public const string AutomaticGrab = "automation.grab";
    public const string DryRunImport = "import.dryRun";
    /// <summary>Explicit opt-in to use safe naming templates in confirmed manual imports.
    /// Background import remains dry-run only, independently of this setting.</summary>
    public const string RenameOnImport = "import.renameOnApply";
    public const string MovieNamingTemplate = "import.naming.movie";
    public const string SeriesNamingTemplate = "import.naming.series";
    public const string TorrentRoot = "paths.torrents";
    public const string MediaRoot = "paths.media";
    /// <summary>Per-type default root folders for the Add dialog. When one is unset, adds for that type require an explicit folder; they never fall back to the shared MediaRoot.</summary>
    public const string DefaultMovieRoot = "media.defaultRoot.movie";
    public const string DefaultSeriesRoot = "media.defaultRoot.series";
    /// <summary>Per-type default quality profile ids for the Add dialog, stored as the profile id string.</summary>
    public const string DefaultMovieProfile = "profile.default.movie";
    public const string DefaultSeriesProfile = "profile.default.series";
}
