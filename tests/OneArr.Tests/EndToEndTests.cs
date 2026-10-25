using Microsoft.EntityFrameworkCore;
using OneArr.Domain;
using OneArr.Infrastructure.Persistence;
using OneArr.Infrastructure.Services;
using Xunit;

namespace OneArr.Tests;

/// <summary>
/// Full chain against fakes only: missing media -> scheduled Torznab search -> acceptable release -> qBittorrent submission
/// -> queue completion -> safe library import. Downloads are simulated by writing a file into the temp torrent root; nothing
/// is fetched, and no real tracker, indexer, download client or media folder is touched.
/// </summary>
public class EndToEndTests
{
    private const string MovieHash = "abcdef0123456789abcdef0123456789abcdef01";
    private const string OtherHash = "1111111111111111111111111111111111111111";
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task MissingMovieIsSearchedSubmittedCompletedAndImported()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        w.Indexer.Body = TestWorld.Torznab(
            ("The.Matrix.1999.2160p.BluRay.x265-GRP", MovieHash, 42),
            ("Other.Movie.1999.1080p.BluRay", OtherHash, 10));
        await w.AddMovieAsync("The Matrix", 1999);

        // 1. The movie is detected as wanted.
        await using (var db = w.Db.CreateDbContext())
            Assert.Single(await db.MediaItems.Where(m => m.Monitored && !m.HasFile).ToListAsync());

        // 2. Scheduled search queries the indexer (GET only) and submits the acceptable release to qBittorrent.
        await w.ReconcileLibraryAsync();
        var pass = await w.Search.RunOnceAsync(Ct);
        Assert.Equal(1, pass.Grabbed);
        Assert.Contains(w.Indexer.Queries, q => q.Contains("t=search") && q.Contains("cat=2000"));
        var torrent = Assert.Single(w.Qbit.Torrents);
        Assert.Equal(MovieHash, torrent.Hash);
        Assert.Equal("onearr-movies", torrent.Category);

        // 3. qBittorrent reports the download in progress; the queue sync reflects it.
        w.Qbit.SetTorrent(MovieHash, "downloading", 0.5, null);
        await w.Downloads.SyncQueueAsync(Ct);
        await using (var db = w.Db.CreateDbContext())
        {
            var job = await db.DownloadJobs.SingleAsync();
            Assert.Equal(DownloadStatus.Downloading, job.Status);
            Assert.Equal(0.5, job.Progress);
        }

        // 4. Completion: the client reports a finished torrent whose content exists under the torrent root.
        var content = Path.Combine(w.Dirs.Torrents, "onearr-movies", "The.Matrix.1999.2160p.BluRay.x265-GRP.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(content)!);
        File.WriteAllText(content, "fixture video bytes");
        w.Qbit.SetTorrent(MovieHash, "uploading", 1.0, content);
        await w.Downloads.SyncQueueAsync(Ct);
        await using (var db = w.Db.CreateDbContext())
            Assert.Equal(DownloadStatus.Completed, (await db.DownloadJobs.SingleAsync()).Status);

        // 5. Safe import into the media root, dry-run disabled for this test only.
        var import = await w.Downloads.ImportCompletedAsync(dryRun: false, Ct);
        Assert.Equal(0, import.Failed);
        var dest = Path.Combine(w.Dirs.Media, "movies", "The Matrix (1999)", "The.Matrix.1999.2160p.BluRay.x265-GRP.mkv");
        Assert.Equal("fixture video bytes", File.ReadAllText(dest));
        Assert.True(File.Exists(content), "seeding source must remain");
        await using (var db = w.Db.CreateDbContext())
        {
            Assert.Equal(DownloadStatus.Imported, (await db.DownloadJobs.SingleAsync()).Status);
            Assert.True((await db.MediaItems.SingleAsync()).HasFile);
            Assert.Equal(SearchOutcome.Grabbed, (await db.SearchAttempts.SingleAsync()).LastOutcome);
        }

        // 6. The movie is no longer wanted, so nothing is searched or added again, even after the post-grab interval.
        w.Time.Advance(w.Policy.AfterGrabInterval * 3);
        await w.ReconcileLibraryAsync();
        var after = await w.Search.RunOnceAsync(Ct);
        Assert.Equal(0, after.Considered);
        Assert.Equal(1, w.Qbit.AddCount);

        // 7. Re-running the import is idempotent: no second copy, no error.
        var again = await w.Downloads.ImportCompletedAsync(dryRun: false, Ct);
        Assert.Equal(0, again.Failed);
        Assert.Single(Directory.GetFiles(Path.Combine(w.Dirs.Media, "movies", "The Matrix (1999)")));

        // 8. Safety invariants across the whole run.
        Assert.DoesNotContain(w.Qbit.Calls, c => c.Contains("delete", StringComparison.OrdinalIgnoreCase) || c.Contains("pause", StringComparison.OrdinalIgnoreCase));
        Assert.All(w.Http.Requests.Where(r => r.RequestUri!.Host == TestWorld.IndexerHost), r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task EpisodeIsTargetedAndImportedIntoSeasonFolder()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        const string episodeHash = "2222222222222222222222222222222222222222";
        w.Indexer.Body = TestWorld.Torznab(("Test.Show.S01E02.720p.WEB-DL.x264-GRP", episodeHash, 20));
        var (_, episodes) = await w.AddSeriesAsync("Test Show", episodes: 3);

        // Three episodes are wanted. Only episode 2 has a matching release, so only one grab is expected.
        await w.ReconcileLibraryAsync();
        var pass = await w.Search.RunOnceAsync(Ct);
        Assert.Equal(3, pass.Searched);
        Assert.Equal(1, pass.Grabbed);
        Assert.Contains(w.Indexer.Queries, q => q.Contains("tvsearch") && q.Contains("ep=2"));
        Assert.Equal("onearr-tv", Assert.Single(w.Qbit.Torrents).Category);

        var content = Path.Combine(w.Dirs.Torrents, "onearr-tv", "Test.Show.S01E02.720p.WEB-DL.x264-GRP.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(content)!);
        File.WriteAllText(content, "episode bytes");
        w.Qbit.SetTorrent(episodeHash, "uploading", 1.0, content);
        await w.Downloads.SyncQueueAsync(Ct);
        var import = await w.Downloads.ImportCompletedAsync(dryRun: false, Ct);

        Assert.Equal(0, import.Failed);
        var dest = Path.Combine(w.Dirs.Media, "tv", "Test Show", "Season 01", "Test.Show.S01E02.720p.WEB-DL.x264-GRP.mkv");
        Assert.Equal("episode bytes", File.ReadAllText(dest));
        await using var db = w.Db.CreateDbContext();
        var ep = await db.Episodes.SingleAsync(e => e.Id == episodes[1]);
        Assert.True(ep.HasFile);
        Assert.False((await db.Episodes.SingleAsync(e => e.Id == episodes[0])).HasFile);
    }

    [Fact]
    public async Task DryRunImportPlansButChangesNothing()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        w.Indexer.Body = TestWorld.Torznab(("The.Matrix.1999.1080p.BluRay.x264-GRP", MovieHash, 42));
        await w.AddMovieAsync("The Matrix", 1999);
        await w.ReconcileLibraryAsync();
        await w.Search.RunOnceAsync(Ct);
        var content = Path.Combine(w.Dirs.Torrents, "onearr-movies", "The.Matrix.1999.1080p.BluRay.x264-GRP.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(content)!);
        File.WriteAllText(content, "x");
        w.Qbit.SetTorrent(MovieHash, "uploading", 1.0, content);
        await w.Downloads.SyncQueueAsync(Ct);

        var summary = await w.Downloads.ImportCompletedAsync(dryRun: true, Ct);

        Assert.Equal(1, summary.DryRun);
        Assert.Empty(Directory.GetFiles(w.Dirs.Media, "*", SearchOption.AllDirectories));
        await using var db = w.Db.CreateDbContext();
        Assert.Equal(DownloadStatus.Completed, (await db.DownloadJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task SearchRunsUnderTheSchedulerJobLock()
    {
        // The manual endpoint and the scheduler share JobRunner. Overlapping runs must be skipped, not doubled.
        using var w = new TestWorld();
        await w.ConfigureAsync();
        await w.AddMovieAsync("The Matrix", 1999);
        var gate = new TaskCompletionSource();

        var first = w.Runner.TryRunAsync("wanted-search", async t => { await gate.Task; return "done"; }, Ct);
        await w.ReconcileLibraryAsync();
        var second = await w.Runner.TryRunAsync("wanted-search", t => w.Search.RunOnceAsync(t).ContinueWith(x => x.Result.ToString(), TaskScheduler.Default), Ct);
        gate.SetResult();

        Assert.False(second);
        Assert.True(await first);
        Assert.Empty(w.Indexer.Queries);
    }
}
