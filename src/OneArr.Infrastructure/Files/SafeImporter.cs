using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OneArr.Application;
using OneArr.Domain;

namespace OneArr.Infrastructure.Files;

/// <summary>
/// Imports one completed download file into a media root. Guarantees:
/// - source must canonically live under the torrent root and must not be a symlink;
/// - destination must canonically live under the media root (fail closed otherwise);
/// - never overwrites: identical existing content is a no-op (idempotent), different content fails;
/// - hardlinks when the filesystem allows it (same device), otherwise copies to a temp file, verifies SHA-256,
///   then moves into place. The source is never deleted or modified.
/// </summary>
public sealed class SafeImporter : IImporter
{
    private static readonly string[] MediaExtensions = [".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".mpg", ".mpeg", ".webm"];

    public static bool IsMediaFile(string path) => MediaExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public async Task<ImportOutcome> ImportAsync(ImportRequest request, CancellationToken ct)
    {
        string source;
        string dest;
        try
        {
            source = CanonicalUnder(request.TorrentRoot, request.SourcePath);
            dest = PlanDestination(request.DestinationRoot, request.DestinationRelativePath, Path.GetFileName(source));
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ImportOutcome(ImportAction.Failed, request.DestinationRoot, false, ex.Message);
        }

        var info = new FileInfo(source);
        if (!info.Exists) return new ImportOutcome(ImportAction.Failed, dest, false, "Source file does not exist");
        if (info.LinkTarget is not null) return new ImportOutcome(ImportAction.Failed, dest, false, "Refusing to import a symbolic link");

        if (File.Exists(dest))
        {
            if (await SameContentAsync(source, dest, ct))
                return new ImportOutcome(ImportAction.Skipped, dest, true, "Already imported with identical content");
            return new ImportOutcome(ImportAction.Failed, dest, false, "Destination exists with different content; not overwriting");
        }

        if (request.DryRun)
            return new ImportOutcome(ImportAction.DryRun, dest, true, $"Would import {info.Length} bytes (dry run, no changes made)");

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        if (TryHardLink(source, dest))
            return new ImportOutcome(ImportAction.Hardlink, dest, true, "Hardlinked on the same filesystem");

        var temp = dest + ".onearr-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = File.OpenRead(source))
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
            return new ImportOutcome(ImportAction.Copy, dest, true, "Verified copy (SHA-256) written; source retained");
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Resolves a path and requires it to be inside the given root. Throws on escape attempts.</summary>
    public static string CanonicalUnder(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidate);
        if (!full.StartsWith(rootFull, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Path is outside the allowed root");
        return full;
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
        if (!dest.StartsWith(rootFull, StringComparison.Ordinal))
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

    private static async Task<byte[]> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, ct);
    }

    private static bool TryHardLink(string source, string dest)
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            return link(source, dest) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
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
