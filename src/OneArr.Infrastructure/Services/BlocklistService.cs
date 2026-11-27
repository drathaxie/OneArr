using Microsoft.EntityFrameworkCore;
using OneArr.Domain;
using OneArr.Infrastructure.Persistence;

namespace OneArr.Infrastructure.Services;

public sealed record BlocklistResult(int JobId, string Message);

/// <summary>
/// Releases that must not be grabbed again. Blocklisting a download marks OneArr's record Failed, stops polling it, and lets
/// the item be searched again right away. It never removes, pauses or deletes the torrent or any file: that stays with you.
/// </summary>
public sealed class BlocklistService
{
    /// <summary>A torrent with no progress at all this long after it was grabbed is treated as dead.</summary>
    public static readonly TimeSpan DeadAfter = TimeSpan.FromHours(24);
    private static readonly string[] StalledStates = ["stalledDL", "metaDL", "forcedMetaDL"];
    private readonly IDbContextFactory<OneArrDbContext> _db;
    private readonly TimeProvider _time;

    public BlocklistService(IDbContextFactory<OneArrDbContext> db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    /// <summary>Hashes (lowercase) and "title:" + lowercase release titles, as the ranker checks them.</summary>
    public static async Task<IReadOnlySet<string>> BlockedKeysAsync(OneArrDbContext db, CancellationToken ct)
    {
        var rows = await db.Blocklist.AsNoTracking().Select(b => new { b.InfoHash, b.ReleaseTitle }).ToListAsync(ct);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (!string.IsNullOrWhiteSpace(r.InfoHash)) keys.Add(r.InfoHash.Trim().ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(r.ReleaseTitle)) keys.Add("title:" + r.ReleaseTitle.Trim().ToLowerInvariant());
        }
        return keys;
    }

    public async Task<BlocklistResult> BlocklistJobAsync(int jobId, string reason, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var job = await db.DownloadJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct) ?? throw new KeyNotFoundException("Download not found");
        if (job.Status == DownloadStatus.Imported) throw new InvalidOperationException("An imported download cannot be blocklisted");
        if (job.Blocklisted) return new BlocklistResult(job.Id, "Already blocklisted");
        Apply(db, job, string.IsNullOrWhiteSpace(reason) ? "Blocklisted by you" : reason.Trim());
        await db.SaveChangesAsync(ct);
        return new BlocklistResult(job.Id, "Blocklisted. The item will be searched again; the torrent was left in your download client.");
    }

    /// <summary>Blocklists downloads that have not started this long after the grab, so the item can get a working release.</summary>
    public async Task<string> BlocklistDeadTorrentsAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        await using var db = await _db.CreateDbContextAsync(ct);
        var candidates = await db.DownloadJobs
            .Where(j => !j.Blocklisted && (j.Status == DownloadStatus.Queued || j.Status == DownloadStatus.Downloading) && j.Progress <= 0)
            .ToListAsync(ct);
        // CreatedAt is compared here: SQLite cannot compare DateTimeOffset in SQL.
        var dead = candidates.Where(j => now - j.CreatedAt >= DeadAfter && StalledStates.Contains(j.ClientState)).ToList();
        foreach (var job in dead)
            Apply(db, job, $"No download progress {DeadAfter.TotalHours:0} hours after the grab (no seeders)");
        await db.SaveChangesAsync(ct);
        return $"Blocklisted {dead.Count} dead download(s)";
    }

    private void Apply(OneArrDbContext db, DownloadJob job, string reason)
    {
        var now = _time.GetUtcNow();
        reason = reason.Length <= 300 ? reason : reason[..300];
        db.Blocklist.Add(new BlocklistEntry
        {
            MediaItemId = job.MediaItemId, EpisodeId = job.EpisodeId, ReleaseTitle = job.ReleaseTitle,
            InfoHash = job.Hash?.ToLowerInvariant(), Reason = reason, DownloadJobId = job.Id, CreatedAt = now,
        });
        job.Status = DownloadStatus.Failed;
        job.ImportFailed = false;
        job.Blocklisted = true;
        job.Error = "Blocklisted: " + reason;
        job.UpdatedAt = now;
        // Search again soon instead of waiting out the post-grab interval.
        var key = job.EpisodeId is int e ? $"episode:{e}" : $"movie:{job.MediaItemId}";
        var attempt = db.SearchAttempts.FirstOrDefault(s => s.ItemKey == key);
        if (attempt is not null) attempt.NextAttemptAt = now;
    }

    public async Task<IReadOnlyList<BlocklistEntry>> ListAsync(CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var rows = await db.Blocklist.AsNoTracking().ToListAsync(ct);
        return rows.OrderByDescending(b => b.CreatedAt).Take(500).ToList();
    }

    /// <summary>Removes the entry so the release may be grabbed again. The old download record stays as it is.</summary>
    public async Task<bool> RemoveAsync(int id, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.Blocklist.Where(b => b.Id == id).ExecuteDeleteAsync(ct) > 0;
    }
}
