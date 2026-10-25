using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OneArr.Application;
using OneArr.Domain;

namespace OneArr.Infrastructure.Files;

/// <summary>
/// Imports one completed download file into a media root. Guarantees:
/// - the source must lie inside the torrent root, and no component below either root may be a link or redirection
///   (symlink, junction, mount point). On Windows the OS-resolved path of every component is compared as well;
/// - the destination must lie inside the media root (fail closed otherwise);
/// - nothing is overwritten: identical content is a no-op (idempotent), different content fails;
/// - a file modified within the quiet period is deferred, not imported (it may still be downloading);
/// - hardlink when source and destination share a volume and the filesystem allows it, otherwise a verified copy
///   (SHA-256) moved into place. The source is opened shared, so a seeding client is never blocked, and it is never
///   deleted or modified.
/// </summary>
public sealed class SafeImporter : IImporter
{
    public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromSeconds(30);
    private static readonly string[] MediaExtensions = [".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".mpg", ".mpeg", ".webm"];
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly TimeSpan _quietPeriod;

    public SafeImporter(TimeSpan? quietPeriod = null)
    {
        _quietPeriod = quietPeriod ?? DefaultQuietPeriod;
    }

    public static bool IsMediaFile(string path) => MediaExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public async Task<ImportOutcome> ImportAsync(ImportRequest request, CancellationToken ct)
    {
        string source;
        string dest;
        try
        {
            source = CanonicalUnder(request.TorrentRoot, request.SourcePath);
            RequireNoLinksBelow(request.TorrentRoot, source);
            dest = PlanDestination(request.DestinationRoot, request.DestinationRelativePath, Path.GetFileName(source));
            RequireNoLinksBelow(request.DestinationRoot, dest);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ImportOutcome(ImportAction.Failed, request.DestinationRoot, false, ex.Message);
        }

        var info = new FileInfo(source);
        if (!info.Exists) return new ImportOutcome(ImportAction.Failed, dest, false, "Source file does not exist");
        if (info.LinkTarget is not null) return new ImportOutcome(ImportAction.Failed, dest, false, "Refusing to import a symbolic link");
        if (_quietPeriod > TimeSpan.Zero && DateTime.UtcNow - info.LastWriteTimeUtc < _quietPeriod)
            return new ImportOutcome(ImportAction.Deferred, dest, false, $"Source changed within the last {_quietPeriod.TotalSeconds:0} s; will retry");

        if (File.Exists(dest))
        {
            if (await SameContentAsync(source, dest, ct))
                return new ImportOutcome(ImportAction.Skipped, dest, true, "Already imported with identical content");
            return new ImportOutcome(ImportAction.Failed, dest, false, "Destination exists with different content; not overwriting");
        }

        if (request.DryRun)
            return new ImportOutcome(ImportAction.DryRun, dest, true, $"Would import {info.Length} bytes (dry run, no changes made)");

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        if (TryHardLink(source, dest, out var linkReason))
            return new ImportOutcome(ImportAction.Hardlink, dest, true, "Hardlinked on the same volume");

        var temp = dest + ".onearr-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = OpenShared(source))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await input.CopyToAsync(output, ct);
            }
            if (new FileInfo(temp).Length != info.Length || !await SameContentAsync(source, temp, ct))
            {
                File.Delete(temp); // our own temp file only
                return new ImportOutcome(ImportAction.Failed, dest, false, "Verification failed after copy; temp file removed");
            }
            if (File.Exists(dest))
            {
                File.Delete(temp);
                return new ImportOutcome(ImportAction.Failed, dest, false, "Destination appeared during copy; not overwriting");
            }
            File.Move(temp, dest, overwrite: false);
            return new ImportOutcome(ImportAction.Copy, dest, true, $"Verified copy (SHA-256) written; hardlink not used: {linkReason}");
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Resolves a path and requires it to be inside the given root. Case-insensitive on Windows.</summary>
    public static string CanonicalUnder(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidate);
        if (!full.StartsWith(rootFull, PathComparison))
            throw new UnauthorizedAccessException("Path is outside the allowed root");
        return full;
    }

    /// <summary>
    /// Fails closed if the root or any component below it is a link or redirection, or does not resolve to the path it
    /// was reached by. On Windows the OS-resolved path is compared, which also catches junctions and mount points.
    /// Generic reparse points without a redirection (such as OneDrive placeholders) are allowed.
    /// Non-existent components are not checked, because nothing exists there to redirect.
    /// </summary>
    public static void RequireNoLinksBelow(string root, string path)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(rootFull, full);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new UnauthorizedAccessException("Path is outside the allowed root");

        if (OperatingSystem.IsWindows() && Directory.Exists(rootFull) && !SameResolvedPath(rootFull))
            throw new UnauthorizedAccessException("Root folder is a junction, symlink or mount point; configure the real folder");

        var current = rootFull;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (IsLinkEntry(current)) throw new UnauthorizedAccessException("Path passes through a symbolic link");
            if (!File.Exists(current) && !Directory.Exists(current)) return; // nothing deeper exists yet
            if (OperatingSystem.IsWindows() && !SameResolvedPath(current))
                throw new UnauthorizedAccessException("Path passes through a junction, symlink or mount point");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool SameResolvedPath(string path)
    {
        var resolved = WindowsNative.FinalPath(path);
        return resolved is not null && string.Equals(resolved.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLinkEntry(string path)
    {
        // LinkTarget is read from the entry itself, so it also works for dangling links and does not follow them.
        if (new FileInfo(path).LinkTarget is not null) return true;
        return Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is not null;
    }

    /// <summary>Builds the destination path from a relative folder name and a file name, rejecting traversal and absolute paths.</summary>
    public static string PlanDestination(string destinationRoot, string relativeFolder, string fileName)
    {
        if (Path.IsPathRooted(relativeFolder) || relativeFolder.Contains("..", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Destination folder must be relative and must not contain '..'");
        if (fileName.Contains("..", StringComparison.Ordinal) || fileName != Path.GetFileName(fileName))
            throw new UnauthorizedAccessException("Unsafe file name");
        if (relativeFolder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new UnauthorizedAccessException("Invalid characters in destination folder");

        var dest = Path.GetFullPath(Path.Combine(destinationRoot, relativeFolder, fileName));
        var rootFull = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!dest.StartsWith(rootFull, PathComparison))
            throw new UnauthorizedAccessException("Destination escapes the media root");
        return dest;
    }

    public static async Task<bool> SameContentAsync(string a, string b, CancellationToken ct)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        var ha = await HashAsync(a, ct);
        var hb = await HashAsync(b, ct);
        return CryptographicOperations.FixedTimeEquals(ha, hb);
    }

    /// <summary>Opens for reading without blocking other readers, writers or deleters (a seeding client keeps working).</summary>
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);

    private static async Task<byte[]> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = OpenShared(path);
        return await SHA256.HashDataAsync(stream, ct);
    }

    private static bool TryHardLink(string source, string dest, out string reason)
    {
        if (OperatingSystem.IsWindows()) return WindowsNative.TryCreateHardLink(source, dest, out reason);
        reason = "";
        try
        {
            if (link(source, dest) == 0) return true;
            reason = "link() failed";
            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            reason = "hardlinks unavailable on this platform";
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    private static extern int link(string oldpath, string newpath);
}
