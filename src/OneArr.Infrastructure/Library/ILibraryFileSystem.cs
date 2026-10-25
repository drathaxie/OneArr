using OneArr.Domain;

namespace OneArr.Infrastructure.Library;

public sealed record RootCheck(LibraryRootState State, string? Message);

public sealed record LibraryEnumeration(IReadOnlyList<CandidateFile> Files, int SkippedReparsePoints, int InaccessibleEntries);

/// <summary>Read-only view of the disk. Nothing behind this interface writes, moves, copies or links media.</summary>
public interface ILibraryFileSystem
{
    bool Windows { get; }
    Task<RootCheck> CheckRootAsync(string root, CancellationToken ct);
    Task<LibraryEnumeration> EnumerateAsync(string root, CancellationToken ct);
    Task<bool> FileExistsAsync(string path, CancellationToken ct);
}
