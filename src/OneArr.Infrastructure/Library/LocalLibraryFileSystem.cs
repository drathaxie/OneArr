using OneArr.Domain;
using OneArr.Infrastructure.Files;

namespace OneArr.Infrastructure.Library;

/// <summary>One entry in a folder listing. Kept separate from FileSystemInfo so the walk can be tested without a disk.</summary>
public sealed record LibraryEntry(string FullPath, bool IsDirectory, bool IsReparsePoint, long SizeBytes);

/// <summary>
/// Reads library folders on this machine. Junctions, symlinks and other reparse points are counted and skipped, never followed.
/// Every network-facing call has a time limit, and the walk stops at cancellation, at a timeout, or at a file-count cap.
/// A walk that did not finish is reported as incomplete so the reconciler never treats its files as missing.
/// </summary>
public sealed class LocalLibraryFileSystem : ILibraryFileSystem
{
    public static readonly TimeSpan ReachTimeout = TimeSpan.FromSeconds(10);
    public const int DefaultMaxFiles = 500_000;

    private readonly TimeSpan _enumerationTimeout;
    private readonly int _maxFiles;
    private readonly Func<string, IEnumerable<LibraryEntry>> _list;

    public LocalLibraryFileSystem() : this(TimeSpan.FromMinutes(15), DefaultMaxFiles, ListDirectory) { }

    /// <param name="list">Test seam. Production uses <see cref="ListDirectory"/>.</param>
    public LocalLibraryFileSystem(TimeSpan enumerationTimeout, int maxFiles, Func<string, IEnumerable<LibraryEntry>> list)
    {
        _enumerationTimeout = enumerationTimeout;
        _maxFiles = maxFiles;
        _list = list;
    }

    public bool Windows => OperatingSystem.IsWindows();

    public async Task<RootCheck> CheckRootAsync(string root, CancellationToken ct)
    {
        var problem = LibraryPaths.ValidateRoot(root, Windows);
        if (problem is not null) return new RootCheck(LibraryRootState.Invalid, problem);

        if (Windows && DriveLetter(root) is char drive && !IsDriveReady(drive))
            return new RootCheck(LibraryRootState.Unavailable, $"Drive {drive}: is not ready. Is the disk connected?");

        try
        {
            return await Task.Run(() => CheckReadable(root), ct).WaitAsync(ReachTimeout, ct);
        }
        catch (TimeoutException)
        {
            return new RootCheck(LibraryRootState.Unavailable, $"No response after {ReachTimeout.TotalSeconds:0} seconds. Is the share offline?");
        }
    }

    public async Task<LibraryEnumeration> EnumerateAsync(string root, CancellationToken ct)
    {
        var full = Path.GetFullPath(root);
        using var stop = new CancellationTokenSource();
        var work = Task.Run(() => Walk(full, _list, _maxFiles, stop.Token), CancellationToken.None);
        try
        {
            return await work.WaitAsync(_enumerationTimeout, ct);
        }
        catch (TimeoutException)
        {
            // Stop the walker at its next folder. A call already blocked in the OS finishes on its own, but nothing new starts.
            stop.Cancel();
            return new LibraryEnumeration([], 0, [full], Complete: false, IncompleteReason: $"Scan stopped after {_enumerationTimeout.TotalMinutes:0} minutes");
        }
    }

    public async Task<FileProbe> ProbeFileAsync(string path, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => Probe(path), ct).WaitAsync(ReachTimeout, ct);
        }
        catch (TimeoutException)
        {
            return new FileProbe(FileProbeState.Unknown, "The folder did not respond in time");
        }
    }

    /// <summary>
    /// Walks the tree with an explicit stack. Stops at cancellation. A folder that cannot be listed is recorded and the walk continues,
    /// so one bad folder does not hide the rest. Reaching the file cap makes the walk incomplete and marks the whole root unreadable.
    /// </summary>
    /// <summary>Public so tests can drive the walk with a fake listing. Production calls it through <see cref="EnumerateAsync"/>.</summary>
    public static LibraryEnumeration Walk(string root, Func<string, IEnumerable<LibraryEntry>> list, int maxFiles, CancellationToken ct)
    {
        var files = new List<CandidateFile>();
        var unreadable = new List<string>();
        var skipped = 0;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            List<LibraryEntry> entries;
            try
            {
                entries = list(dir).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                unreadable.Add(dir);
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.IsReparsePoint)
                {
                    skipped++;
                    continue;
                }
                if (entry.IsDirectory)
                {
                    pending.Push(entry.FullPath);
                    continue;
                }
                if (!SafeImporter.IsMediaFile(entry.FullPath)) continue;

                if (files.Count >= maxFiles)
                    return new LibraryEnumeration(files, skipped, [root], Complete: false, IncompleteReason: $"More than {maxFiles:N0} media files; the scan stopped early");
                files.Add(new CandidateFile(entry.FullPath, Path.GetRelativePath(root, entry.FullPath), entry.SizeBytes));
            }
        }

        return new LibraryEnumeration(files, skipped, unreadable, Complete: unreadable.Count == 0,
            IncompleteReason: unreadable.Count == 0 ? null : $"{unreadable.Count} folder(s) could not be read");
    }

    public static IEnumerable<LibraryEntry> ListDirectory(string dir) =>
        new DirectoryInfo(dir).EnumerateFileSystemInfos()
            .Select(e => new LibraryEntry(e.FullName, e is DirectoryInfo, (e.Attributes & FileAttributes.ReparsePoint) != 0, e is FileInfo f ? f.Length : 0));

    /// <summary>
    /// File.Exists returns false on access denied, so it cannot tell "gone" from "unreadable". The parent folder is listed instead:
    /// if it cannot be read the answer is Unknown; if it is readable and the file is absent the answer is Missing.
    /// </summary>
    private static FileProbe Probe(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileProbe(FileProbeState.Exists);
            var parent = Path.GetDirectoryName(path);
            if (parent is null) return new FileProbe(FileProbeState.Unknown, "No folder for this path");
            using var entries = Directory.EnumerateFileSystemEntries(parent).GetEnumerator();
            entries.MoveNext();
            return new FileProbe(FileProbeState.Missing);
        }
        catch (DirectoryNotFoundException)
        {
            // The folder itself is gone. Its root was readable moments ago in the same apply, so the file is confirmed absent.
            return new FileProbe(FileProbeState.Missing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileProbe(FileProbeState.Unknown, ex.Message);
        }
    }

    private static LibraryRootState CheckReadableState(string root) => CheckReadable(root).State;

    private static RootCheck CheckReadable(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return new RootCheck(LibraryRootState.Unavailable, "Folder not found or not reachable");
            using var entries = Directory.EnumerateFileSystemEntries(root).GetEnumerator();
            entries.MoveNext();
            return new RootCheck(LibraryRootState.Accessible, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new RootCheck(LibraryRootState.Unavailable, "Access denied. Check that the account running OneArr can read this folder");
        }
        catch (IOException ex)
        {
            return new RootCheck(LibraryRootState.Unavailable, ex.Message);
        }
    }

    private static char? DriveLetter(string root) =>
        root.Length >= 2 && root[1] == ':' && char.IsLetter(root[0]) ? char.ToUpperInvariant(root[0]) : null;

    private static bool IsDriveReady(char drive)
    {
        try
        {
            return new DriveInfo(drive + ":\\").IsReady;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
