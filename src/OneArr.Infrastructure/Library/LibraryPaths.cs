using System.Text.RegularExpressions;
using OneArr.Domain;

namespace OneArr.Infrastructure.Library;

/// <summary>Pure path rules for library roots and Sonarr/Radarr path mapping. No file system access, so every rule is testable on any OS.</summary>
public static class LibraryPaths
{
    private static readonly Regex DriveAbsolute = new(@"^[A-Za-z]:[\\/]", RegexOptions.Compiled);
    private static readonly Regex UncShare = new(@"^\\\\[^\\/]+\\[^\\/]+", RegexOptions.Compiled);

    public static bool IsWindowsStyle(string path) => DriveAbsolute.IsMatch(path) || UncShare.IsMatch(path);

    /// <summary>Null when the path can be used as a library root on the given OS; otherwise a plain-language reason.</summary>
    public static string? ValidateRoot(string? path, bool windows)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Enter a folder path";
        var p = path.Trim();
        if (p.Split(['\\', '/']).Contains("..")) return "Paths must not contain '..'";
        if (windows)
        {
            if (p.StartsWith('/')) return $"'{p}' is a Linux path. On Windows use a drive path such as D:\\Media\\Movies or a share such as \\\\server\\media";
            if (!IsWindowsStyle(p)) return "Use a drive letter (D:\\Media\\Movies) or a network share (\\\\server\\share\\Movies)";
            return null;
        }
        return p.StartsWith('/') ? null : "Path must be absolute, for example /data/media/movies";
    }

    /// <summary>Case-folded on Windows so C:\Media and c:\media are one key. Used for every equality and containment check.</summary>
    public static string Key(string path, bool windows)
    {
        var p = path.Trim();
        if (windows)
        {
            p = p.Replace('/', '\\').TrimEnd('\\');
            return p.ToUpperInvariant();
        }
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }

    /// <summary>True when <paramref name="keyPath"/> is strictly inside <paramref name="keyFolder"/>. Both must be keys from <see cref="Key"/>.</summary>
    public static bool IsWithin(string keyPath, string keyFolder, bool windows)
    {
        var sep = windows ? '\\' : '/';
        if (keyFolder.Length == 0) return keyPath.StartsWith(sep);
        return keyPath.Length > keyFolder.Length
            && keyPath.StartsWith(keyFolder, StringComparison.Ordinal)
            && keyPath[keyFolder.Length] == sep;
    }

    /// <summary>
    /// Maps a path Sonarr/Radarr stored onto this machine using the longest matching prefix.
    /// Returns null when no mapping covers it, so a Linux path is never used as a Windows path.
    /// </summary>
    public static string? MapLegacy(string legacyPath, IEnumerable<PathMapping> mappings, bool windows)
    {
        var source = legacyPath.Trim();
        if (windows && IsWindowsStyle(source)) return source;

        PathMapping? best = null;
        foreach (var m in mappings)
        {
            var prefix = m.SourcePrefix.Trim().TrimEnd('/');
            if (prefix.Length == 0) continue;
            if (source != prefix && !source.StartsWith(prefix + "/", StringComparison.Ordinal)) continue;
            if (best is null || prefix.Length > best.SourcePrefix.Trim().TrimEnd('/').Length) best = m;
        }
        if (best is null) return null;

        var rest = source[best.SourcePrefix.Trim().TrimEnd('/').Length..];
        var mapped = best.TargetPrefix.Trim().TrimEnd('\\', '/') + rest;
        if (windows) mapped = mapped.Replace('/', '\\');
        return ValidateRoot(mapped, windows) is null ? mapped : null;
    }

    /// <summary>Validates a mapping pair. Returns null when valid.</summary>
    public static string? ValidateMapping(string sourcePrefix, string targetPrefix, bool windows)
    {
        if (string.IsNullOrWhiteSpace(sourcePrefix) || !sourcePrefix.Trim().StartsWith('/')) return "The source must be the path Sonarr or Radarr uses, for example /data/media/movies";
        return ValidateRoot(targetPrefix, windows);
    }
}
