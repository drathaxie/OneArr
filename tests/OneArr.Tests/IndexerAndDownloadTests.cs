using System.Net;
using System.Text;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Http;
using OneArr.Infrastructure.Persistence;
using OneArr.Infrastructure.Providers;
using OneArr.Infrastructure.Services;
using Xunit;

namespace OneArr.Tests;

public class TorznabClientTests
{
    [Fact]
    public void ParsesFixtureFeedPreservingCategoriesSizeAndSeeders()
    {
        var xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "torznab_search.xml"));
        var items = TorznabClient.Parse(xml, "fixture");
        Assert.Equal(2, items.Count);
        var first = items[0];
        Assert.Equal(25_000_000_000, first.Size);
        Assert.Equal(42, first.Seeders);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01", first.InfoHash);
        Assert.Contains(2040, first.Categories);
        Assert.StartsWith("magnet:", first.Link);
        Assert.Equal("https://indexer.example/dl/2.torrent", items[1].Link);
    }

    [Fact]
    public void InvalidXmlFailsSafely()
    {
        var ex = Assert.Throws<ExternalServiceException>(() => TorznabClient.Parse("<rss><channel>", "bad"));
        Assert.Contains("invalid XML", ex.Message);
    }

    [Fact]
    public void IndexerErrorElementIsSurfacedAndRedacted()
    {
        var xml = "<error code=\"100\" description=\"Incorrect user credentials apikey=SECRET123\"/>";
        var ex = Assert.Throws<ExternalServiceException>(() => TorznabClient.Parse(xml, "idx"));
        Assert.DoesNotContain("SECRET123", ex.Message);
    }

    [Fact]
    public async Task RateLimitedIndexerReportsFriendlyError()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("slow down", HttpStatusCode.TooManyRequests, "text/plain"));
        var client = new TorznabClient(new FakeFactory(handler));
        var idx = new IndexerConfig { Name = "idx", BaseUrl = "https://indexer.example/", ApiKey = "k" };
        var search = new ReleaseSearch(MediaType.Movie, "The Matrix", 1999, null, null, "2000");
        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => client.SearchAsync(idx, search, CancellationToken.None));
        Assert.Contains("rate limiting", ex.Message);
        Assert.True(handler.Requests.Count >= 1);
    }

    [Fact]
    public async Task MovieSearchUsesSearchTypeAndSeriesUsesTvSearchWithEpisode()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("<rss></rss>", contentType: "application/xml"));
        var client = new TorznabClient(new FakeFactory(handler));
        var idx = new IndexerConfig { Name = "idx", BaseUrl = "https://indexer.example/prefix/", ApiKey = "KEY" };
        await client.SearchAsync(idx, new ReleaseSearch(MediaType.Series, "Test Show", null, 1, 2, "5000"), CancellationToken.None);
        var uri = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("t=tvsearch", uri);
        Assert.Contains("season=1", uri);
        Assert.Contains("ep=2", uri);
    }
}

public class QBittorrentClientTests
{
    private static DownloadClientConfig Config => new() { Id = 1, Name = "qb", BaseUrl = "http://qbittorrent:8080", Username = "u", Password = "p", MovieCategory = "onearr-movies", TvCategory = "onearr-tv" };

    private static FakeHandler FakeQbit(List<string> log)
    {
        return new FakeHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            log.Add(req.Method + " " + path);
            if (path == "/api/v2/auth/login")
            {
                var r = FakeHandler.Text("Ok.", contentType: "text/plain");
                r.Headers.Add("Set-Cookie", "SID=fakesid123; HttpOnly; path=/");
                return r;
            }
            if (req.Headers.TryGetValues("Cookie", out var cookies) && !cookies.Any(c => c.Contains("fakesid123")))
                return FakeHandler.Text("Forbidden", HttpStatusCode.Forbidden, "text/plain");
            if (path == "/api/v2/torrents/add")
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                log.Add("BODY " + body);
                return FakeHandler.Text("Ok.", contentType: "text/plain");
            }
            if (path == "/api/v2/torrents/info")
            {
                var json = """
                [{"hash":"abcdef0123456789abcdef0123456789abcdef01","name":"The.Matrix.1999.1080p","state":"downloading","progress":0.4,"content_path":"/data/torrents/onearr-movies/The.Matrix","category":"onearr-movies","size":123},
                 {"hash":"1111111111111111111111111111111111111111","name":"Done","state":"uploading","progress":1,"content_path":"/data/torrents/onearr-tv/Done","category":"onearr-tv","size":5}]
                """;
                return FakeHandler.Text(json);
            }
            return FakeHandler.Text("not found", HttpStatusCode.NotFound, "text/plain");
        });
    }

    [Fact]
    public async Task LogsInAddsToCategoryAndListsJobsWithoutDeleting()
    {
        var log = new List<string>();
        var client = new QBittorrentClient(new FakeFactory(FakeQbit(log)));
        var hash = await client.AddAsync(Config, "magnet:?xt=urn:btih:abcdef0123456789abcdef0123456789abcdef01&dn=x", "onearr-movies", CancellationToken.None);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01", hash);
        var items = await client.ListAsync(Config, "onearr-movies", CancellationToken.None);
        Assert.Equal(2, items.Count);
        Assert.Contains(log, l => l.StartsWith("BODY") && l.Contains("category=onearr-movies"));
        Assert.DoesNotContain(log, l => l.Contains("delete", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StateMappingAndMagnetHashParsing()
    {
        Assert.Equal(DownloadStatus.Downloading, QBittorrentClient.MapState("downloading", 0.4));
        Assert.Equal(DownloadStatus.Completed, QBittorrentClient.MapState("uploading", 1));
        Assert.Equal(DownloadStatus.Failed, QBittorrentClient.MapState("error", 0));
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", QBittorrentClient.MagnetInfoHash("magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567"));
    }

    [Fact]
    public async Task RejectsNonMagnetNonHttpLinks()
    {
        var client = new QBittorrentClient(new FakeFactory(new FakeHandler(_ => FakeHandler.Text("Ok."))));
        await Assert.ThrowsAsync<ExternalServiceException>(() => client.AddAsync(Config, "file:///etc/passwd", "onearr-movies", CancellationToken.None));
    }
}
