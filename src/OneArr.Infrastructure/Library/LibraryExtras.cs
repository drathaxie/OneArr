namespace OneArr.Infrastructure.Library;

/// <summary>
/// Recognises bonus material laid out the way Plex and Jellyfin expect it: a folder such as "Featurettes" inside a title's
/// folder, or a file name ending in "-trailer", "-featurette" and so on. "Specials" is deliberately not here: it is season 0.
/// </summary>
public static class LibraryExtras
{
    private static readonly HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Behind The Scenes", "Deleted Scenes", "Featurettes", "Interviews", "Scenes", "Shorts", "Trailers", "Extras", "Other", "Bonus",
    };

    private static readonly string[] Suffixes =
        ["-behindthescenes", "-deleted", "-featurette", "-interview", "-scene", "-short", "-trailer", "-other"];

    /// <param name="relativePath">Path relative to the library root.</param>
    public static bool IsExtra(string relativePath)
    {
        var parts = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        // Only a folder inside a title's folder counts: a top-level folder named "Other" may be a real collection.
        for (var i = 1; i < parts.Length - 1; i++)
            if (Folders.Contains(parts[i])) return true;
        var name = Path.GetFileNameWithoutExtension(parts[^1]);
        return Suffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }
}
