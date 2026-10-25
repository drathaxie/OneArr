using OneArr.Domain;
using OneArr.Infrastructure.Files;

namespace OneArr.Infrastructure.Library;

/// <summary>
/// Reads library folders on this machine. Junctions, symlinks and other reparse points are counted and skipped,
/// never followed, so a link cannot pull files from outside the root into the catalog.
/// Reachability checks are time-limited so an offline network share cannot hang a scan.
/// </summary>
public sealed class LocalLibraryFileSystem : ILibraryFileSystem
{
    public static readonly TimeSpan ReachTimeout = TimeSpan.FromSeconds(10);

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

    public Task<LibraryEnumeration> EnumerateAsync(string root, CancellationToken ct) =>
        Task.Run(() => Walk(Path.GetFullPath(root), ct), ct);

    public Task<bool> FileExistsAsync(string path, CancellationToken ct) =>
        Task.Run(() => File.Exists(path), ct).WaitAsync(ReachTimeout, ct);

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

    private static LibraryEnumeration Walk(string root, CancellationToken ct)
    {
        var files = new List<CandidateFile>();
        var skipped = 0;
        var inaccessible = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            List<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                inaccessible++;
                continue;
            }

            foreach (var entry in entries)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skipped++;
                    continue;
                }
                if (entry is DirectoryInfo sub)
                {
                    pending.Push(sub);
                    continue;
                }
                if (entry is FileInfo file && SafeImporter.IsMediaFile(file.Name))
                    files.Add(new CandidateFile(file.FullName, Path.GetRelativePath(root, file.FullName), file.Length));
            }
        }
        return new LibraryEnumeration(files, skipped, inaccessible);
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
