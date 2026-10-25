using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Persistence;

namespace OneArr.Infrastructure.Files;

/// <summary>Read-only library scan: records media files under a root. Never writes to the filesystem.</summary>
public sealed class LibraryScanner : ILibraryScanner
{
    private readonly IDbContextFactory<OneArrDbContext> _db;

    public LibraryScanner(IDbContextFactory<OneArrDbContext> db)
    {
        _db = db;
    }

    public async Task<int> ScanAsync(string root, CancellationToken ct)
    {
        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Media root does not exist");

        var files = Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(SafeImporter.IsMediaFile)
            .ToList();

        await using var db = await _db.CreateDbContextAsync(ct);
        var known = await db.FileRecords.ToDictionaryAsync(f => f.Path, ct);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var size = new FileInfo(file).Length;
            if (known.TryGetValue(file, out var rec))
            {
                rec.Size = size;
                rec.ScannedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                db.FileRecords.Add(new FileRecord { Path = file, Size = size });
            }
        }
        await db.SaveChangesAsync(ct);
        return files.Count;
    }
}
