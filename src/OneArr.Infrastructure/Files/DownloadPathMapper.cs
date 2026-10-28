using System.Text.Json;

namespace OneArr.Infrastructure.Files;

/// <summary>
/// Per-download-client remote POSIX path to local download-root translation.
/// Never transforms an already-local path unless an explicit rule exists.
/// This is separate from legacy *Arr media-library migration mappings.
/// </summary>
public sealed record DownloadPathMapping(string RemotePrefix, string LocalPrefix);

public static class DownloadPathMapper
{
    public static string SettingsKey(int clientId)
    {
        if (clientId <= 0) throw new ArgumentOutOfRangeException(nameof(clientId));
        return $"downloadClient.{clientId}.remotePath";
    }

    public static void Validate(DownloadPathMapping rule, string torrentRoot)
    {
        _ = Segments(rule.RemotePrefix);
        var local = RequireLocalRoot(rule.LocalPrefix, torrentRoot);
        if (!Directory.Exists(local))
            throw new ArgumentException("The mapped local download folder must already exist");
    }

    /// <summary>Only configured matching paths are translated. A nonmatching
    /// source path fails closed; it does not fall back to an unrelated local file.</summary>
    public static string Map(DownloadPathMapping rule, string remotePath, string torrentRoot)
    {
        var remoteSegments = Segments(rule.RemotePrefix);
        var fileSegments = Segments(remotePath);
        if (fileSegments.Length < remoteSegments.Length ||
            !remoteSegments.SequenceEqual(fileSegments.Take(remoteSegments.Length), StringComparer.Ordinal))
            throw new ArgumentException("Download client path does not match its configured remote prefix");

        var local = RequireLocalRoot(rule.LocalPrefix, torrentRoot);
        if (!Directory.Exists(local))
            throw new ArgumentException("Mapped local download folder is unavailable");
        var result = fileSegments.Skip(remoteSegments.Length)
            .Aggregate(local, (path, segment) => Path.Combine(path, segment));
        result = Path.GetFullPath(result);
        // Avoid reparse-point / junction traversal; SafeImporter also checks again at apply.
        var configured = Path.GetFullPath(torrentRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(result.TrimEnd(Path.DirectorySeparatorChar), configured, comparison))
        {
            SafeImporter.RequireNoLinksBelow(torrentRoot, Path.Combine(result, "__onearr_path_check__"));
            return configured;
        }
        SafeImporter.RequireNoLinksBelow(torrentRoot, result);
        return SafeImporter.CanonicalUnder(torrentRoot, result);
    }

    public static string Serialize(DownloadPathMapping rule) =>
        JsonSerializer.Serialize(rule);

    public static DownloadPathMapping Read(string json)
    {
        try
        {
            var rule = JsonSerializer.Deserialize<DownloadPathMapping>(json);
            if (rule is null) throw new ArgumentException("Invalid download path mapping");
            return rule;
        }
        catch (JsonException)
        {
            throw new ArgumentException("Stored download path mapping is invalid; correct it in Download Clients");
        }
    }

    private static string[] Segments(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal) ||
            path.Contains('\\') || path.Contains("//", StringComparison.Ordinal) ||
            path.Any(char.IsControl) || path.Contains('?') || path.Contains('#'))
            throw new ArgumentException("Remote download prefix/path must be an absolute POSIX path");
        if (path.Length > 1024) throw new ArgumentException("Remote path exceeds 1024 characters");
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(x => x is "." or ".." || x.Contains(':')))
            throw new ArgumentException("Unsafe remote download path segment");
        return segments;
    }

    private static string RequireLocalRoot(string path, string torrentRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Mapped local path must be absolute");
        if (string.IsNullOrWhiteSpace(torrentRoot) || !Path.IsPathFullyQualified(torrentRoot))
            throw new ArgumentException("Set an absolute torrent root in Media Management first");
        var root = Path.GetFullPath(torrentRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        // A mapping to the torrent root itself is valid. Child paths must pass boundary checking.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root, comparison))
            _ = SafeImporter.CanonicalUnder(torrentRoot, full);
        SafeImporter.RequireNoLinksBelow(torrentRoot, Path.Combine(full, "__onearr_path_check__"));
        return full;
    }
}
