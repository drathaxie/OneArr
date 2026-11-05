using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Providers;
using OneArr.Infrastructure.Services;
using Xunit;

namespace OneArr.Tests;

/// <summary>Connection-test parsing and stuck-download reconciliation: a Missing download must never be silently re-grabbed.</summary>
public class ConnectionAndMissingTests
{
    private const string HashA = "abcdef0123456789abcdef0123456789abcdef01";
    private const string HashB = "1111111111111111111111111111111111111111";
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Matrix = "The.Matrix.1999.1080p.BluRay.x264-GRP";

    [Fact]
    public void TorznabCapsReportsCategoriesAndSearchTypes()
    {
        var xml = "<caps><server version=\"1.0\" /><searching><search available=\"yes\" /><tv-search available=\"yes\" /><movie-search available=\"no\" /></searching>"
            + "<categories><category id=\"2000\" name=\"Movies\" /><category id=\"5000\" name=\"TV\" /></categories></caps>";

        var caps = TorznabClient.ParseCaps(xml, "fixture");

        Assert.Equal(2, caps.Categories.Count);
        Assert.Equal(2000, caps.Categories[0].Id);
        Assert.Equal("Movies", caps.Categories[0].Name);
        Assert.True(caps.SearchAvailable);
        Assert.True(caps.TvSearchAvailable);
        Assert.False(caps.MovieSearchAvailable);
    }

    [Fact]
    public void TorznabCapsRejectsNonCapsDocuments()
    {
        Assert.Throws<ExternalServiceException>(() => TorznabClient.ParseCaps("<rss><channel /></rss>", "fixture"));
        Assert.Throws<ExternalServiceException>(() => TorznabClient.ParseCaps("not xml at all", "fixture"));
    }

    [Fact]
    public async Task ItemWithMissingDownloadIsNotGrabbedAgainWhenItComesDue()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        w.Indexer.Body = TestWorld.Torznab((Matrix, HashA, 42));
        await w.AddMovieAsync("The Matrix", 1999);
        await w.ReconcileLibraryAsync();
        await w.Search.RunOnceAsync(Ct);

        // The torrent disappears from qBittorrent: the job is marked Missing, not deleted.
        await using (var db = w.Db.CreateDbContext())
        {
            var job = await db.DownloadJobs.SingleAsync();
            job.Status = DownloadStatus.Missing;
            await db.SaveChangesAsync();
        }

        w.Time.Advance(w.Policy.AfterGrabInterval + TimeSpan.FromHours(1));
        await w.ReconcileLibraryAsync();
        var summary = await w.Search.RunOnceAsync(Ct);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Grabbed);
        Assert.Equal(1, w.Qbit.AddCount);
        await using (var db = w.Db.CreateDbContext())
            Assert.Equal(SearchOutcome.SkippedMissingDownload, (await db.SearchAttempts.SingleAsync()).LastOutcome);
    }

    [Fact]
    public async Task ManualGrabIsRefusedWhileADownloadIsMissing()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        w.Indexer.Body = TestWorld.Torznab((Matrix, HashA, 42));
        var movieId = await w.AddMovieAsync("The Matrix", 1999);
        await w.ReconcileLibraryAsync();
        await w.Search.RunOnceAsync(Ct);

        await using (var db = w.Db.CreateDbContext())
        {
            var job = await db.DownloadJobs.SingleAsync();
            job.Status = DownloadStatus.Missing;
            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => w.Releases.GrabAsync(
            new GrabRequest(movieId, null, Matrix + "-OTHER", "magnet:?xt=urn:btih:" + HashB, HashB), Ct));

        Assert.Contains("missing from qBittorrent", ex.Message);
        Assert.Equal(1, w.Qbit.AddCount);
    }
}
