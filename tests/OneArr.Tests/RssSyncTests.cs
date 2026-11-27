using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Providers;
using OneArr.Infrastructure.Services;
using Xunit;

namespace OneArr.Tests;

/// <summary>RSS sync: opt-in per indexer, gated like wanted search, grabs only releases that match a wanted item.</summary>
public class RssSyncTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string HashA = "abcdef0123456789abcdef0123456789abcdef01";
    private const string HashB = "1111111111111111111111111111111111111111";
    private const string HashC = "2222222222222222222222222222222222222222";

    private static RssSyncService Rss(TestWorld w) =>
        new(w.Db, new TorznabClient(new FakeFactory(w.Http)), new ReleaseParser(), w.Releases, w.Settings, w.Library, w.Time, w.Policy);

    private static async Task OptInAsync(TestWorld w, bool rss = true)
    {
        await using var db = w.Db.CreateDbContext();
        foreach (var i in db.Indexers) i.RssSync = rss;
        await db.SaveChangesAsync();
    }

    /// <summary>A series whose newest episode aired today, as a weekly show's does.</summary>
    private static async Task<(int SeriesId, List<int> Episodes)> FreshShowAsync(TestWorld w)
    {
        var (series, episodes) = await w.AddSeriesAsync("Fresh Show", episodes: 2);
        await using var db = w.Db.CreateDbContext();
        (await db.Episodes.SingleAsync(e => e.Id == episodes[1])).AirDate = DateOnly.FromDateTime(w.Time.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync();
        return (series, episodes);
    }

    [Fact]
    public async Task IndexersAreNotReadUnlessOptedIn()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        w.Indexer.Body = TestWorld.Torznab(("Fresh.Show.S01E02.1080p.WEB-DL.x264-GRP", HashA, 50));
        await FreshShowAsync(w);
        await w.ReconcileLibraryAsync();

        var summary = await Rss(w).RunOnceAsync(Ct);

        Assert.Contains("No enabled indexer has RSS sync turned on", summary.Message);
        Assert.Empty(w.Indexer.Queries);
        Assert.Equal(0, w.Qbit.AddCount);
    }

    [Fact]
    public async Task NothingIsReadWhileAutomaticGrabbingIsOff()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync(automaticGrab: false);
        await OptInAsync(w);
        await FreshShowAsync(w);
        await w.ReconcileLibraryAsync();

        var summary = await Rss(w).RunOnceAsync(Ct);

        Assert.False(summary.Enabled);
        Assert.Empty(w.Indexer.Queries);
    }

    [Fact]
    public async Task NewEpisodeInTheFeedIsGrabbedAndRecorded()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        await OptInAsync(w);
        w.Indexer.Body = TestWorld.Torznab(
            ("Other.Show.S03E04.1080p.WEB-DL.x264-GRP", HashB, 90),
            ("Fresh.Show.S01E02.1080p.WEB-DL.x264-GRP", HashA, 50));
        var (series, episodes) = await FreshShowAsync(w);
        await w.ReconcileLibraryAsync();

        var summary = await Rss(w).RunOnceAsync(Ct);

        Assert.Equal(1, summary.Grabbed);
        var query = Assert.Single(w.Indexer.Queries);
        Assert.DoesNotContain("q=", query);
        Assert.Contains("cat=" + Uri.EscapeDataString("5000,2000"), query);
        await using var db = w.Db.CreateDbContext();
        var job = await db.DownloadJobs.SingleAsync();
        Assert.Equal((series, (int?)episodes[1]), (job.MediaItemId, job.EpisodeId));
        Assert.Equal(HashA, job.Hash);
        var attempt = await db.SearchAttempts.SingleAsync();
        Assert.Equal(SearchOutcome.Grabbed, attempt.LastOutcome);
        Assert.Contains("RSS sync", attempt.LastMessage);
    }

    [Fact]
    public async Task OnlyOptedInIndexersAreRead()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        var other = new FakeIndexer { Body = TestWorld.Torznab(("Fresh.Show.S01E02.1080p.WEB-DL.x264-GRP", HashA, 50)) };
        w.Http.Route("other.test", other.Respond);
        await using (var db = w.Db.CreateDbContext())
        {
            db.Indexers.Add(new IndexerConfig { Name = "opted-in", BaseUrl = "http://other.test/", ApiKey = "k", Enabled = true, RssSync = true });
            await db.SaveChangesAsync();
        }
        await FreshShowAsync(w);
        await w.ReconcileLibraryAsync();

        var summary = await Rss(w).RunOnceAsync(Ct);

        Assert.Empty(w.Indexer.Queries);
        Assert.Single(other.Queries);
        Assert.Equal(1, summary.Grabbed);
    }

    [Fact]
    public async Task ReleasesThatDoNotMatchAWantedItemAreIgnored()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        await OptInAsync(w);
        var (series, episodes) = await FreshShowAsync(w);
        // Episode 1 is already on disk, the season pack and the future episode are not single wanted episodes.
        await using (var db = w.Db.CreateDbContext())
        {
            (await db.Episodes.SingleAsync(e => e.Id == episodes[0])).HasFile = true;
            db.Episodes.Add(new Episode { MediaItemId = series, SeasonNumber = 1, EpisodeNumber = 3, ProviderId = "future", Title = "Next week", AirDate = DateOnly.FromDateTime(w.Time.GetUtcNow().UtcDateTime).AddDays(7), Monitored = true });
            await db.SaveChangesAsync();
        }
        w.Indexer.Body = TestWorld.Torznab(
            ("Fresh.Show.S01E01.1080p.WEB-DL.x264-GRP", HashA, 50),
            ("Fresh.Show.S01.1080p.WEB-DL.x264-GRP", HashB, 50),
            ("Fresh.Show.S01E03.1080p.WEB-DL.x264-GRP", HashC, 50));
        await w.ReconcileLibraryAsync();

        var summary = await Rss(w).RunOnceAsync(Ct);

        Assert.Equal(0, summary.Matched);
        Assert.Equal(0, w.Qbit.AddCount);
    }

    [Fact]
    public async Task ReleaseBelowTheQualityProfileIsNotGrabbed()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        await OptInAsync(w);
        await FreshShowAsync(w);
        w.Indexer.Body = TestWorld.Torznab(("Fresh.Show.S01E02.480p.WEB-DL.x264-GRP", HashA, 50));
        await w.ReconcileLibraryAsync();

        var summary = await Rss(w).RunOnceAsync(Ct);

        Assert.Equal(1, summary.Matched);
        Assert.Equal(0, summary.Grabbed);
    }

    [Fact]
    public async Task EpisodeAlreadyDownloadingIsNotGrabbedAgain()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        await OptInAsync(w);
        await FreshShowAsync(w);
        w.Indexer.Body = TestWorld.Torznab(("Fresh.Show.S01E02.1080p.WEB-DL.x264-GRP", HashA, 50));
        await w.ReconcileLibraryAsync();
        var rss = Rss(w);
        await rss.RunOnceAsync(Ct);

        // The next sync sees a different release of the same episode.
        w.Indexer.Body = TestWorld.Torznab(("Fresh.Show.S01E02.1080p.WEB-DL.x264-OTHER", HashB, 500));
        var second = await rss.RunOnceAsync(Ct);

        Assert.Equal(0, second.Grabbed);
        Assert.Equal(1, w.Qbit.AddCount);
    }

    [Fact]
    public async Task WantedMovieInTheFeedIsGrabbed()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        await OptInAsync(w);
        var movie = await w.AddMovieAsync("The Matrix", 1999);
        w.Indexer.Body = TestWorld.Torznab(("The.Matrix.1999.1080p.BluRay.x264-GRP", HashA, 42));
        await w.ReconcileLibraryAsync();

        var summary = await Rss(w).RunOnceAsync(Ct);

        Assert.Equal(1, summary.Grabbed);
        await using var db = w.Db.CreateDbContext();
        Assert.Equal(movie, (await db.DownloadJobs.SingleAsync()).MediaItemId);
    }

    [Fact]
    public async Task OneFailingIndexerDoesNotStopTheOthers()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        await OptInAsync(w);
        w.Indexer.Fail = true;
        var other = new FakeIndexer { Body = TestWorld.Torznab(("Fresh.Show.S01E02.1080p.WEB-DL.x264-GRP", HashA, 50)) };
        w.Http.Route("other.test", other.Respond);
        await using (var db = w.Db.CreateDbContext())
        {
            db.Indexers.Add(new IndexerConfig { Name = "second", BaseUrl = "http://other.test/", ApiKey = "k", Enabled = true, RssSync = true, Priority = 5 });
            await db.SaveChangesAsync();
        }
        await FreshShowAsync(w);
        await w.ReconcileLibraryAsync();

        var summary = await Rss(w).RunOnceAsync(Ct);

        Assert.Equal(1, summary.Grabbed);
        Assert.Contains(summary.Errors, e => e.StartsWith("fixture:"));
    }
}
