using Microsoft.EntityFrameworkCore;
using OneArr.Domain;
using OneArr.Infrastructure.Services;
using Xunit;

namespace OneArr.Tests;

/// <summary>Blocklisting: the release is never grabbed again, the item is searched again, the torrent is never touched.</summary>
public class BlocklistTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Bad = "The.Matrix.1999.1080p.BluRay.x264-BAD";
    private const string Good = "The.Matrix.1999.1080p.BluRay.x264-GOOD";
    private const string HashA = "abcdef0123456789abcdef0123456789abcdef01";
    private const string HashB = "1111111111111111111111111111111111111111";

    private static async Task<(TestWorld W, int JobId)> GrabbedAsync()
    {
        var w = new TestWorld();
        await w.ConfigureAsync();
        w.Indexer.Body = TestWorld.Torznab((Bad, HashA, 500), (Good, HashB, 5));
        await w.AddMovieAsync("The Matrix", 1999);
        await w.ReconcileLibraryAsync();
        await w.Search.RunOnceAsync(Ct);
        await using var db = w.Db.CreateDbContext();
        var job = await db.DownloadJobs.SingleAsync();
        Assert.Equal(HashA, job.Hash);
        return (w, job.Id);
    }

    [Fact]
    public async Task Blocklisted_release_is_skipped_and_the_next_best_is_grabbed_at_once()
    {
        var (w, jobId) = await GrabbedAsync();
        using var _ = w;
        var blocklist = new BlocklistService(w.Db, w.Time);

        await blocklist.BlocklistJobAsync(jobId, "", Ct);
        var summary = await w.Search.RunOnceAsync(Ct);

        Assert.Equal(1, summary.Grabbed);
        await using var db = w.Db.CreateDbContext();
        var jobs = await db.DownloadJobs.OrderBy(j => j.Id).ToListAsync();
        Assert.Equal(DownloadStatus.Failed, jobs[0].Status);
        Assert.True(jobs[0].Blocklisted);
        Assert.Equal(HashB, jobs[1].Hash);
        // Nothing was removed or paused in qBittorrent.
        Assert.DoesNotContain(w.Qbit.Calls, c => c.Contains("delete") || c.Contains("pause") || c.Contains("stop"));
    }

    [Fact]
    public async Task Queue_sync_does_not_revive_a_blocklisted_download()
    {
        var (w, jobId) = await GrabbedAsync();
        using var _ = w;
        await new BlocklistService(w.Db, w.Time).BlocklistJobAsync(jobId, "", Ct);

        await w.Downloads.SyncQueueAsync(Ct);

        await using var db = w.Db.CreateDbContext();
        Assert.Equal(DownloadStatus.Failed, (await db.DownloadJobs.SingleAsync(j => j.Id == jobId)).Status);
    }

    [Fact]
    public async Task Manual_grab_of_a_blocklisted_release_is_refused_until_it_is_removed()
    {
        var (w, jobId) = await GrabbedAsync();
        using var _ = w;
        var blocklist = new BlocklistService(w.Db, w.Time);
        await blocklist.BlocklistJobAsync(jobId, "", Ct);
        // The blocklisted job is Failed, so the duplicate-hash and active-download checks no longer apply: only the blocklist refuses.
        int movieId;
        await using (var db = w.Db.CreateDbContext()) movieId = (await db.MediaItems.SingleAsync()).Id;
        var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
            w.Releases.GrabAsync(new OneArr.Infrastructure.Services.GrabRequest(movieId, null, Bad, "magnet:?xt=urn:btih:" + HashA, HashA), Ct));
        Assert.Contains("Blocklisted", refused.Message);

        var entry = Assert.Single(await blocklist.ListAsync(Ct));
        Assert.True(await blocklist.RemoveAsync(entry.Id, Ct));
        Assert.Empty(await blocklist.ListAsync(Ct));
    }

    [Fact]
    public async Task A_torrent_with_no_progress_a_day_after_the_grab_is_blocklisted()
    {
        var (w, jobId) = await GrabbedAsync();
        using var _ = w;
        await using (var db = w.Db.CreateDbContext())
        {
            var job = await db.DownloadJobs.SingleAsync();
            job.Status = DownloadStatus.Downloading;
            job.ClientState = "stalledDL";
            job.Progress = 0;
            await db.SaveChangesAsync();
        }
        var blocklist = new BlocklistService(w.Db, w.Time);

        w.Time.Advance(TimeSpan.FromHours(23));
        Assert.StartsWith("Blocklisted 0", await blocklist.BlocklistDeadTorrentsAsync(Ct));
        w.Time.Advance(TimeSpan.FromHours(2));
        Assert.StartsWith("Blocklisted 1", await blocklist.BlocklistDeadTorrentsAsync(Ct));
    }

    [Fact]
    public async Task A_slow_torrent_that_has_started_is_left_alone()
    {
        var (w, _) = await GrabbedAsync();
        using var __ = w;
        await using (var db = w.Db.CreateDbContext())
        {
            var job = await db.DownloadJobs.SingleAsync();
            job.Status = DownloadStatus.Downloading;
            job.ClientState = "stalledDL";
            job.Progress = 0.05;
            await db.SaveChangesAsync();
        }
        w.Time.Advance(TimeSpan.FromDays(3));
        Assert.StartsWith("Blocklisted 0", await new BlocklistService(w.Db, w.Time).BlocklistDeadTorrentsAsync(Ct));
    }
}
