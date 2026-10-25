using System.Security.Cryptography;

namespace OneArr.Updates;

/// <summary>Checks a downloaded artifact against its checksum and extracts it with zip-slip and link protections.</summary>
public static class ArtifactVerifier
{
    public const long MaxArtifactBytes = 400L * 1024 * 1024;
    public const long MaxExtractedBytes = 1024L * 1024 * 1024;
    public const int MaxEntries = 20_000;

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static void RequireHash(string path, string expectedLowerHex)
    {
        var actual = Sha256(path);
        if (!string.Equals(actual, expectedLowerHex, StringComparison.Ordinal))
            throw new InvalidDataException("Artifact checksum does not match; the download was discarded");
    }

    /// <summary>
    /// Extracts into an empty destination. Refuses absolute paths, "..", symlink entries, and anything whose full path
    /// lands outside the destination. Also caps entry count and total size to bound disk use.
    /// </summary>
    public static void SafeExtract(string zipPath, string destination)
    {
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new InvalidOperationException("Extraction target must be empty");
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("Artifact has too many entries");

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (Path.IsPathRooted(name) || name.StartsWith('/') || name.Contains(':'))
                throw new InvalidDataException($"Unsafe entry path: {entry.FullName}");
            if (name.Split('/').Any(part => part == ".."))
                throw new InvalidDataException($"Unsafe entry path: {entry.FullName}");

            // Unix mode bits live in the high 16 bits of ExternalAttributes. 0xA000 is a symbolic link.
            var mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (mode == 0xA000) throw new InvalidDataException($"Symbolic link entry refused: {entry.FullName}");

            var target = Path.GetFullPath(Path.Combine(destination, name));
            if (!target.StartsWith(root, comparison) && !string.Equals(target, root.TrimEnd(Path.DirectorySeparatorChar), comparison))
                throw new InvalidDataException($"Entry escapes the destination: {entry.FullName}");

            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            total += entry.Length;
            if (total > MaxExtractedBytes) throw new InvalidDataException("Artifact expands beyond the size limit");
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }
}
