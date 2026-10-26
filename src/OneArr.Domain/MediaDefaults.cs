namespace OneArr.Domain;

/// <summary>
/// Pure rules for per-type defaults and series availability, kept free of I/O so they are unit-testable.
/// The movie/series separation is the point: a movie default must never be resolved from the series
/// default or vice versa. There is deliberately NO shared fallback: when the per-type default is unset,
/// the result is empty and the caller must require an explicit folder. Falling back to the legacy single
/// media root used to silently select the Movies folder for a series (or the TV folder for a movie)
/// whenever that path was stored as the shared root, which is exactly the mistake this rule prevents.
/// The legacy media root still exists as the import containment root; it is simply not an add default.
/// </summary>
public static class MediaDefaults
{
    public static string ResolveRoot(MediaType type, string? movieRoot, string? seriesRoot)
    {
        var perType = type == MediaType.Movie ? movieRoot : seriesRoot;
        if (!string.IsNullOrWhiteSpace(perType)) return perType.Trim();
        return "";
    }

    /// <summary>
    /// Series availability over monitored, aired episodes only. Applicable == 0 means the series cannot be
    /// classified reliably (no episodes, no monitored episodes, or no monitored episode with a known air
    /// date), so it is Unknown rather than Complete or Missing. Unaired and unmonitored episodes never count.
    /// </summary>
    public static string ClassifySeries(int availableEpisodes, int applicableEpisodes)
    {
        if (applicableEpisodes <= 0) return "Unknown";
        if (availableEpisodes >= applicableEpisodes) return "Complete";
        if (availableEpisodes <= 0) return "Missing";
        return "Partial";
    }
}
