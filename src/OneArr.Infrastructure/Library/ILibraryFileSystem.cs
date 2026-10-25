using OneArr.Domain;

namespace OneArr.Infrastructure.Library;

public sealed record RootCheck(LibraryRootState State, string? Message);

/// <summary>
/// The result of walking one root. <see cref="Complete"/> is false when any folder could not be read, the walk timed out,
/// or the file limit was reached. An incomplete walk must never be read as "those files are gone".
/// </summary>
public sealed record LibraryEnumeration(
    IReadOnlyList<CandidateFile> Files,
    int SkippedReparsePoints,
    IReadOnlyList<string> UnreadableFolders,
    bool Complete,
    string? IncompleteReason);

public enum FileProbeState { Exists, Missing, Unknown }

/// <summary>Missing means the folder was readable and the file is confirmed absent. Unknown means the disk could not decide.</summary>
public sealed record FileProbe(FileProbeState State, string? Message = null);

/// <summary>Read-only view of the disk. Nothing behind this interface writes, moves, copies or links media.</summary>
public interface ILibraryFileSystem
{
    bool Windows { get; }
    Task<RootCheck> CheckRootAsync(string root, CancellationToken ct);
    Task<LibraryEnumeration> EnumerateAsync(string root, CancellationToken ct);
    Task<FileProbe> ProbeFileAsync(string path, CancellationToken ct);
}
