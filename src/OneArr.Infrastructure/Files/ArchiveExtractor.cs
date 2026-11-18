using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace OneArr.Infrastructure.Files;

/// <summary>Plain-language failure to read or extract a packed download.
/// An ArgumentException so the import paths that already fail closed on one
/// (preview returns 400, automatic import records it as a hold reason) treat
/// it the same way, instead of surfacing a library exception type.</summary>
public sealed class ArchiveExtractionException : ArgumentException
{
    public ArchiveExtractionException(string message) : base(message) { }
    public ArchiveExtractionException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>One video file inside a packed download, as listed without extracting it.</summary>
public sealed record ArchiveMediaEntry(string Name, long SizeBytes, DateTimeOffset? LastModifiedUtc);

/// <summary>
/// Reads packed (RAR) downloads. A torrent folder that holds only an archive
/// has no importable video until the archive is opened, so the import paths
/// use this to list the video inside and to extract it.
/// Extraction always lands in a staging folder under the torrent root but
/// outside the torrent's own folder, so seeding is never affected: nothing
/// is ever written into the torrent folder, and the archive files themselves
/// are only ever read, never moved or deleted.
/// </summary>
public interface IArchiveExtractor
{
    /// <summary>True when the folder (recursively) holds a first-part RAR file.</summary>
    bool HasArchive(string contentPath);

    /// <summary>Video files inside the archives, listed without extracting
    /// anything. Throws <see cref="ArchiveExtractionException"/> when an
    /// archive is corrupt, incomplete, or password-protected.</summary>
    Task<IReadOnlyList<ArchiveMediaEntry>> ListMediaAsync(string contentPath, string torrentRoot, CancellationToken ct);

    /// <summary>Extracts the video files inside the archives to the staging
    /// folder for this job and returns their paths. Idempotent: an entry
    /// already extracted at the expected size is left alone. Throws
    /// <see cref="ArchiveExtractionException"/> on any failure; a partially
    /// written entry is removed, never left behind as if complete.</summary>
    Task<IReadOnlyList<string>> ExtractMediaAsync(string contentPath, string torrentRoot, int jobId, CancellationToken ct);

    /// <summary>Removes this job's staging folder, if any. Only ever removes
    /// the folder this extractor creates; anything else is left alone.</summary>
    void CleanupStaging(string torrentRoot, int jobId);
}

/// <summary>SharpCompress-backed RAR extraction (RAR 1.5 through 5.0, including
/// multi-volume archives). Pure managed code: no external unrar, 7z, or
/// WinRAR binary is needed on the machine running OneArr.</summary>
public sealed class SharpCompressArchiveExtractor : IArchiveExtractor
{
    /// <summary>Staging lives under the torrent root, outside any torrent
    /// folder. The leading dot keeps it out of the way of a library scan's
    /// usual view, and the job id keeps one job's staging from another's.</summary>
    public static string StagingDirectory(string torrentRoot, int jobId) =>
        Path.Combine(Path.GetFullPath(torrentRoot), ".onearr-extract", jobId.ToString());

    private static readonly Regex PartNumber = new(@"\.part0*(\d+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True for the first part of an archive only: a plain .rar, or
    /// .part01/.part1.rar. Later parts (.part02.rar, .r00, .r01, ...) are
    /// volumes of that archive, never archives of their own.</summary>
    public static bool IsArchiveRootPart(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)) return false;
        var m = PartNumber.Match(name);
        return m.Success ? int.TryParse(m.Groups[1].Value, out var n) && n == 1 : true;
    }

    public bool HasArchive(string contentPath)
    {
        try
        {
            if (File.Exists(contentPath)) return IsArchiveRootPart(contentPath);
            if (!Directory.Exists(contentPath)) return false;
            return Directory.EnumerateFiles(contentPath, "*", SearchOption.AllDirectories)
                .Any(IsArchiveRootPart);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static List<string> ArchiveRoots(string contentPath, string torrentRoot)
    {
        var full = Path.GetFullPath(contentPath);
        IEnumerable<string> candidates = File.Exists(full)
            ? [full]
            : Directory.Exists(full)
                ? Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                : [];
        var roots = new List<string>();
        foreach (var file in candidates)
        {
            if (!IsArchiveRootPart(file)) continue;
            try { SafeImporter.CanonicalUnder(torrentRoot, file); }
            catch { continue; } // an archive outside the torrent root is not ours to open
            roots.Add(file);
        }
        roots.Sort(StringComparer.OrdinalIgnoreCase);
        return roots;
    }

    public Task<IReadOnlyList<ArchiveMediaEntry>> ListMediaAsync(string contentPath, string torrentRoot, CancellationToken ct) =>
        Task.Run(() => ListMedia(contentPath, torrentRoot), ct);

    private IReadOnlyList<ArchiveMediaEntry> ListMedia(string contentPath, string torrentRoot)
    {
        var roots = ArchiveRoots(contentPath, torrentRoot);
        if (roots.Count == 0) return [];
        var entries = new List<ArchiveMediaEntry>();
        foreach (var root in roots)
            entries.AddRange(ListArchiveMedia(root));
        return entries;
    }

    private static List<ArchiveMediaEntry> ListArchiveMedia(string root)
    {
        try
        {
            if (!RarArchive.IsRarFile(root))
                throw new ArchiveExtractionException($"Packed (RAR): {Path.GetFileName(root)} is not a readable RAR archive.");
            using var archive = RarArchive.OpenArchive(root, new ReaderOptions());
            if (archive.IsEncrypted)
                throw new ArchiveExtractionException($"Packed (RAR): {Path.GetFileName(root)} is password-protected; OneArr cannot extract it without a password.");
            var missing = MissingVolumes(archive, root);
            if (missing.Count > 0)
                throw new ArchiveExtractionException($"Packed (RAR): {Path.GetFileName(root)} is incomplete — missing volume file {Path.GetFileName(missing[0])}.");
            var found = new List<ArchiveMediaEntry>();
            foreach (IEntry entry in archive.Entries)
            {
                if (entry.IsDirectory) continue;
                if (entry.IsEncrypted)
                    throw new ArchiveExtractionException($"Packed (RAR): {Path.GetFileName(root)} is password-protected; OneArr cannot extract it without a password.");
                if (entry.Key is null || !SafeImporter.IsMediaFile(entry.Key) || SampleFiles.IsSample(entry.Key)) continue;
                found.Add(new ArchiveMediaEntry(entry.Key, entry.Size,
                    entry.LastModifiedTime is DateTime t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : null));
            }
            return found;
        }
        catch (ArchiveExtractionException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            throw new ArchiveExtractionException($"Packed (RAR): {Path.GetFileName(root)} could not be read ({Trim(ex.Message)}).", ex);
        }
    }

    public Task<IReadOnlyList<string>> ExtractMediaAsync(string contentPath, string torrentRoot, int jobId, CancellationToken ct) =>
        Task.Run(() => ExtractMedia(contentPath, torrentRoot, jobId, ct), ct);

    private IReadOnlyList<string> ExtractMedia(string contentPath, string torrentRoot, int jobId, CancellationToken ct)
    {
        var roots = ArchiveRoots(contentPath, torrentRoot);
        if (roots.Count == 0) return [];
        // List (and so validate) the archives before creating anything: a
        // corrupt archive fails with no staging folder left behind.
        var total = roots.Sum(root => ListArchiveMedia(root).Sum(e => e.SizeBytes));
        var staging = StagingDirectory(torrentRoot, jobId);
        Directory.CreateDirectory(staging);
        // Refuse before writing anything when the staging volume cannot hold
        // the videos inside, instead of failing halfway through an extract.
        if (total > 0 && CopySpaceGuard.CopyProblem(staging, total) is string spaceError)
            throw new ArchiveExtractionException($"Packed (RAR): {spaceError}");
        var extracted = new List<string>();
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            extracted.AddRange(ExtractArchive(root, staging, ct));
        }
        return extracted;
    }

    private static List<string> ExtractArchive(string root, string staging, CancellationToken ct)
    {
        var extracted = new List<string>();
        try
        {
            if (!RarArchive.IsRarFile(root))
                throw new ArchiveExtractionException($"Packed (RAR): {Path.GetFileName(root)} is not a readable RAR archive.");
            using var archive = RarArchive.OpenArchive(root, new ReaderOptions());
            if (archive.IsEncrypted)
                throw new ArchiveExtractionException($"Packed (RAR): {Path.GetFileName(root)} is password-protected; OneArr cannot extract it without a password.");
            var missing = MissingVolumes(archive, root);
            if (missing.Count > 0)
                throw new ArchiveExtractionException($"Packed (RAR): {Path.GetFileName(root)} is incomplete — missing volume file {Path.GetFileName(missing[0])}.");
            // An entry without its own timestamp takes the archive file's
            // time, so a re-extraction always produces the same file time
            // and the apply's stale-preview check does not trip on it.
            var fallbackTime = new FileInfo(root).LastWriteTimeUtc;
            foreach (IArchiveEntry entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.IsDirectory) continue;
                if (entry.IsEncrypted)
                    throw new ArchiveExtractionException($"Packed (RAR): {Path.GetFileName(root)} is password-protected; OneArr cannot extract it without a password.");
                if (entry.Key is null || !SafeImporter.IsMediaFile(entry.Key) || SampleFiles.IsSample(entry.Key)) continue;
                extracted.Add(ExtractEntry(entry, staging, fallbackTime));
            }
            return extracted;
        }
        catch (ArchiveExtractionException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            throw new ArchiveExtractionException($"Packed (RAR): {Path.GetFileName(root)} could not be extracted ({Trim(ex.Message)}).", ex);
        }
    }

    /// <summary>Volume file names may be bare names next to the first part
    /// or full paths; resolve bare ones against the first part's folder.</summary>
    private static List<string> MissingVolumes(IArchive archive, string root)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(root))!;
        return archive.Volumes
            .Select(v => v.FileName)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => Path.IsPathRooted(f!) ? f! : Path.Combine(folder, f!))
            .Where(f => !File.Exists(f))
            .ToList();
    }

    private static string ExtractEntry(IArchiveEntry entry, string staging, DateTime fallbackTime)
    {
        // Entry paths inside an archive are untrusted: flatten nothing, but
        // refuse any entry that would escape the staging folder (absolute
        // path, "..", or a drive-qualified name). CanonicalUnder enforces it.
        var target = SafeImporter.CanonicalUnder(staging, entry.Key!.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(target) && new FileInfo(target).Length == entry.Size) return target;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".onearr-extract-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = entry.OpenEntryStream())
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                input.CopyTo(output);
            }
            if (new FileInfo(temp).Length != entry.Size)
                throw new ArchiveExtractionException($"Packed (RAR): {Path.GetFileName(entry.Key)} extracted incomplete ({new FileInfo(temp).Length:N0} of {entry.Size:N0} bytes).");
            var writtenAt = entry.LastModifiedTime is DateTime t
                ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
                : fallbackTime;
            File.SetLastWriteTimeUtc(temp, writtenAt);
            // The target may have appeared while this entry was extracted
            // (a second pass running at the same time): reuse it when it is
            // already complete instead of overwriting it.
            if (File.Exists(target) && new FileInfo(target).Length == entry.Size) { File.Delete(temp); return target; }
            File.Move(temp, target, overwrite: false);
            return target;
        }
        catch (ArchiveExtractionException)
        {
            TryDelete(temp);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            TryDelete(temp);
            throw new ArchiveExtractionException($"Packed (RAR): {Path.GetFileName(entry.Key)} could not be extracted ({Trim(ex.Message)}).", ex);
        }
    }

    public void CleanupStaging(string torrentRoot, int jobId)
    {
        try
        {
            var staging = StagingDirectory(torrentRoot, jobId);
            // Only the folder this extractor creates, under the torrent root:
            // never delete a torrent folder or anything outside the root.
            _ = SafeImporter.CanonicalUnder(Path.GetFullPath(torrentRoot), staging);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        catch { /* cleanup is best-effort; a leftover staging folder is harmless and reused next pass */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200];
}
