using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Persistence;
using OneArr.Infrastructure.Services;
using Xunit;

namespace OneArr.Tests;

/// <summary>Scheduled search behaviour: enablement, duplicate prevention, backoff, bounded runs and persistence.</summary>
public class WantedSearchTests
{
    private const string HashA = "abcdef0123456789abcdef0123456789abcdef01";
    private const string HashB = "1111111111111111111111111111111111111111";
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Matrix = "The.Matrix.1999.1080p.BluRay.x264-GRP";

    [Fact]
    public async Task DisabledByDefaultSearchesNothing()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync(automaticGrab: false);
        await w.AddMovieAsync("The Matrix", 1999);

        await w.ReconcileLibraryAsync();
        var summary = await w.Search.RunOnceAsync(Ct);

        Assert.False(summary.Enabled);
        Assert.Empty(w.Indexer.Queries);
        Assert.Equal(0, w.Qbit.AddCount);
    }

    [Fact]
    public async Task GrabsBestAcceptedReleaseForWantedMovie()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        w.Indexer.Body = TestWorld.Torznab((Matrix, HashA, 42));
        var movieId = await w.AddMovieAsync("The Matrix", 1999);

        await w.ReconcileLibraryAsync();
        var summary = await w.Search.RunOnceAsync(Ct);
        Assert.Equal(1, w.Qbit.AddCount);
        await using var db = w.Db.CreateDbContext();
        var job = await db.DownloadJobs.SingleAsync();
        Assert.Equal(HashA, job.Hash);
        Assert.Equal("onearr-movies", job.Category);
        Assert.Equal(movieId, job.MediaItemId);
        var state = await db.SearchAttempts.SingleAsync();
        Assert.Equal(SearchOutcome.Grabbed, state.LastOutcome);
        Assert.Equal(w.Time.GetUtcNow() + w.Policy.AfterGrabInterval, state.NextAttemptAt);
    }

    [Fact]
    public async Task ItemWithActiveDownloadIsNotGrabbedAgainWhenItComesDue()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        w.Indexer.Body = TestWorld.Torznab((Matrix, HashA, 42));
        await w.AddMovieAsync("The Matrix", 1999);
        await w.ReconcileLibraryAsync();
        await w.Search.RunOnceAsync(Ct);

        // Well past the post-grab interval, the item is due again, but its download is still active.
        w.Time.Advance(w.Policy.AfterGrabInterval + TimeSpan.FromHours(1));
        await w.ReconcileLibraryAsync();
        var summary = await w.Search.RunOnceAsync(Ct);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Grabbed);
        Assert.Equal(1, w.Qbit.AddCount);
    }

    [Fact]
    public async Task ManualGrabIsRefusedWhileAnotherDownloadIsActive()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        w.Indexer.Body = TestWorld.Torznab((Matrix, HashA, 42));
        var movieId = await w.AddMovieAsync("The Matrix", 1999);
        await w.ReconcileLibraryAsync();
        await w.Search.RunOnceAsync(Ct);

        var second = await Assert.ThrowsAsync<ArgumentException>(() => w.Releases.GrabAsync(
            new GrabRequest(movieId, null, Matrix + "-OTHER", "magnet:?xt=urn:btih:" + HashB, HashB), Ct));

        Assert.Contains("active download", second.Message);
        Assert.Equal(1, w.Qbit.AddCount);
    }

    [Fact]
    public async Task NoAcceptableReleaseWaitsSixHoursBeforeSearchingAgain()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        w.Indexer.Body = TestWorld.Torznab(("Other.Movie.1999.1080p.BluRay", HashB, 10));
        await w.AddMovieAsync("The Matrix", 1999);

        await w.ReconcileLibraryAsync();
        var first = await w.Search.RunOnceAsync(Ct);
        Assert.Equal(1, first.Searched);
        Assert.Equal(0, first.Grabbed);
        await using (var db = w.Db.CreateDbContext())
            Assert.Equal(SearchOutcome.NoAcceptableRelease, (await db.SearchAttempts.SingleAsync()).LastOutcome);

        w.Time.Advance(TimeSpan.FromHours(5));
        await w.ReconcileLibraryAsync();
        Assert.Equal(0, (await w.Search.RunOnceAsync(Ct)).Considered);
        Assert.Single(w.Indexer.Queries);

        w.Time.Advance(TimeSpan.FromHours(2));
        await w.ReconcileLibraryAsync();
        Assert.Equal(1, (await w.Search.RunOnceAsync(Ct)).Searched);
        Assert.Equal(2, w.Indexer.Queries.Count);
    }

    [Fact]
    public async Task IndexerFailuresBackOffExponentially()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        w.Indexer.Fail = true;
        await w.AddMovieAsync("The Matrix", 1999);

        await w.ReconcileLibraryAsync();
        await w.Search.RunOnceAsync(Ct);
        await using (var db = w.Db.CreateDbContext())
        {
            var s = await db.SearchAttempts.SingleAsync();
            Assert.Equal(SearchOutcome.IndexerError, s.LastOutcome);
            Assert.Equal(1, s.ConsecutiveFailures);
            Assert.Equal(w.Time.GetUtcNow() + TimeSpan.FromMinutes(15), s.NextAttemptAt);
        }

        w.Time.Advance(TimeSpan.FromMinutes(15));
        await w.ReconcileLibraryAsync();
        await w.Search.RunOnceAsync(Ct);
        await using (var db = w.Db.CreateDbContext())
        {
            var s = await db.SearchAttempts.SingleAsync();
            Assert.Equal(2, s.ConsecutiveFailures);
            Assert.Equal(w.Time.GetUtcNow() + TimeSpan.FromMinutes(30), s.NextAttemptAt);
        }

        w.Time.Advance(TimeSpan.FromMinutes(30));
        await w.ReconcileLibraryAsync();
        await w.Search.RunOnceAsync(Ct);
        await using (var db = w.Db.CreateDbContext())
            Assert.Equal(3, (await db.SearchAttempts.SingleAsync()).ConsecutiveFailures);

        // Third failure waits 60 minutes: not due after 59, due after 60.
        w.Time.Advance(TimeSpan.FromMinutes(59));
        await w.ReconcileLibraryAsync();
        Assert.Equal(0, (await w.Search.RunOnceAsync(Ct)).Considered);
        w.Time.Advance(TimeSpan.FromMinutes(1));
        await w.ReconcileLibraryAsync();
        Assert.Equal(1, (await w.Search.RunOnceAsync(Ct)).Considered);
    }

    private static async Task SetAirDateAsync(TestWorld w, int episodeId, DateOnly date)
    {
        await using var db = w.Db.CreateDbContext();
        (await db.Episodes.SingleAsync(e => e.Id == episodeId)).AirDate = date;
        await db.SaveChangesAsync();
    }

    /// <summary>An item searched before and overdue for another try, as a large backlog is.</summary>
    private static async Task MarkOverdueAsync(TestWorld w, int mediaItemId, int episodeId, DateTimeOffset dueSince)
    {
        await using var db = w.Db.CreateDbContext();
        db.SearchAttempts.Add(new SearchAttempt
        {
            ItemKey = $"episode:{episodeId}", MediaItemId = mediaItemId, EpisodeId = episodeId,
            LastAttemptAt = dueSince.AddHours(-6), NextAttemptAt = dueSince, Attempts = 1, LastOutcome = SearchOutcome.NoAcceptableRelease,
        });
        await db.SaveChangesAsync();
    }

    private static string Decode(string query) => Uri.UnescapeDataString(query).Replace('+', ' ');

    /// <summary>
    /// The South Park report: a new episode aired, but every run's few search slots went to overdue backlog items first,
    /// so the new episode waited for hours. A recently aired episode is now searched ahead of the backlog.
    /// </summary>
    [Fact]
    public async Task RecentlyAiredEpisodeIsSearchedBeforeOverdueBacklog()
    {
        using var w = new TestWorld(new SearchPolicy { MaxSearchesPerRun = 1 });
        await w.ConfigureAsync();
        var (backlog, backlogEpisodes) = await w.AddSeriesAsync("Backlog Show", episodes: 3);
        var (_, freshEpisodes) = await w.AddSeriesAsync("Fresh Show", episodes: 1);
        await SetAirDateAsync(w, freshEpisodes[0], DateOnly.FromDateTime(w.Time.GetUtcNow().UtcDateTime));
        foreach (var e in backlogEpisodes) await MarkOverdueAsync(w, backlog, e, w.Time.GetUtcNow().AddHours(-5));

        await w.ReconcileLibraryAsync();
        await w.Search.RunOnceAsync(Ct);

        var query = Assert.Single(w.Indexer.Queries);
        Assert.Contains("Fresh Show", Decode(query));
    }

    [Fact]
    public async Task NeverSearchedItemIsSearchedBeforeOverdueBacklog()
    {
        using var w = new TestWorld(new SearchPolicy { MaxSearchesPerRun = 1 });
        await w.ConfigureAsync();
        var (backlog, backlogEpisodes) = await w.AddSeriesAsync("Backlog Show", episodes: 2);
        await w.AddSeriesAsync("Unsearched Show", episodes: 1);
        foreach (var e in backlogEpisodes) await MarkOverdueAsync(w, backlog, e, w.Time.GetUtcNow().AddHours(-5));

        await w.ReconcileLibraryAsync();
        await w.Search.RunOnceAsync(Ct);

        Assert.Contains("Unsearched Show", Decode(Assert.Single(w.Indexer.Queries)));
    }

    [Fact]
    public async Task RecentlyAiredEpisodeWithoutReleaseIsRetriedWithinAnHour()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        var (_, episodes) = await w.AddSeriesAsync("Fresh Show", episodes: 1);
        await SetAirDateAsync(w, episodes[0], DateOnly.FromDateTime(w.Time.GetUtcNow().UtcDateTime));

        await w.ReconcileLibraryAsync();
        await w.Search.RunOnceAsync(Ct);

        await using var db = w.Db.CreateDbContext();
        var attempt = await db.SearchAttempts.SingleAsync(a => a.EpisodeId == episodes[0]);
        Assert.Equal(SearchOutcome.NoAcceptableRelease, attempt.LastOutcome);
        Assert.Equal(TimeSpan.FromHours(1), attempt.NextAttemptAt - attempt.LastAttemptAt);
    }

    [Fact]
    public async Task OlderEpisodeWithoutReleaseStillWaitsTheFullInterval()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        var (_, episodes) = await w.AddSeriesAsync("Old Show", episodes: 1);

        await w.ReconcileLibraryAsync();
        await w.Search.RunOnceAsync(Ct);

        await using var db = w.Db.CreateDbContext();
        var attempt = await db.SearchAttempts.SingleAsync(a => a.EpisodeId == episodes[0]);
        Assert.Equal(TimeSpan.FromHours(6), attempt.NextAttemptAt - attempt.LastAttemptAt);
    }

    [Fact]
    public async Task RunIsBoundedByMaxSearchesPerRun()
    {
        using var w = new TestWorld(new SearchPolicy { MaxSearchesPerRun = 2 });
        await w.ConfigureAsync();
        await w.AddSeriesAsync("Test Show", episodes: 5);

        await w.ReconcileLibraryAsync();
        var summary = await w.Search.RunOnceAsync(Ct);

        Assert.Equal(5, summary.Considered);
        Assert.Equal(2, summary.Searched);
        Assert.Equal(2, w.Indexer.Queries.Count);
    }

    [Fact]
    public async Task DownloadClientFailureCreatesNoJobAndIsRetried()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        w.Indexer.Body = TestWorld.Torznab((Matrix, HashA, 42));
        w.Qbit.FailAdd = true;
        await w.AddMovieAsync("The Matrix", 1999);

        await w.ReconcileLibraryAsync();
        var summary = await w.Search.RunOnceAsync(Ct);

        Assert.Equal(0, summary.Grabbed);
        await using (var db = w.Db.CreateDbContext())
        {
            Assert.Equal(0, await db.DownloadJobs.CountAsync());
            var s = await db.SearchAttempts.SingleAsync();
            Assert.Equal(SearchOutcome.DownloadClientError, s.LastOutcome);
            Assert.Equal(1, s.ConsecutiveFailures);
        }

        w.Qbit.FailAdd = false;
        w.Time.Advance(TimeSpan.FromMinutes(15));
        await w.ReconcileLibraryAsync();
        Assert.Equal(1, (await w.Search.RunOnceAsync(Ct)).Grabbed);
    }

    [Fact]
    public async Task SearchStateSurvivesRestart()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        w.Indexer.Body = TestWorld.Torznab(("Other.Movie.1999.1080p.BluRay", HashB, 10));
        await w.AddMovieAsync("The Matrix", 1999);
        await w.ReconcileLibraryAsync();
        await w.Search.RunOnceAsync(Ct);

        // A new service instance over the same database must honour the stored backoff.
        var restarted = w.RestartSearch();
        var summary = await restarted.RunOnceAsync(Ct);

        Assert.Equal(0, summary.Considered);
        Assert.Single(w.Indexer.Queries);
    }

    [Fact]
    public async Task OnlyMonitoredMissingAiredItemsAreSearched()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        await w.AddMovieAsync("Has File", 1999, hasFile: true);
        await w.AddMovieAsync("Unmonitored", 1999, monitored: false);
        var (_, episodes) = await w.AddSeriesAsync("Future Show", episodes: 1);
        await using (var db = w.Db.CreateDbContext())
        {
            var ep = await db.Episodes.SingleAsync(e => e.Id == episodes[0]);
            ep.AirDate = new DateOnly(2099, 1, 1);
            await db.SaveChangesAsync();
        }

        await w.ReconcileLibraryAsync();
        var summary = await w.Search.RunOnceAsync(Ct);

        Assert.Equal(0, summary.Considered);
        Assert.Empty(w.Indexer.Queries);
    }

    [Fact]
    public async Task An_entry_waiting_for_a_library_scan_is_skipped_and_the_rest_is_still_searched()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        await w.AddMovieAsync("The Matrix", 1999);
        await w.ReconcileLibraryAsync();
        // Added after the scan: it may already be on disk, so it waits, but it no longer pauses everything else.
        await w.AddMovieAsync("Dune", 2021);

        var summary = await w.Search.RunOnceAsync(Ct);

        Assert.True(summary.Enabled);
        Assert.Equal(1, summary.Searched);
        Assert.Contains("Matrix", Decode(Assert.Single(w.Indexer.Queries)));
    }
}
