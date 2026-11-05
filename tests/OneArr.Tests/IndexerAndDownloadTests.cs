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
    public async Task ActualNyaaRssUrl_RetainsCategoryAndTrustedFilterInSearch()
    {
        var xml = """
            <rss xmlns:nyaa="https://nyaa.si/xmlns/nyaa" version="2.0">
              <channel><item>
                <title>Test Show S01E02 1080p</title>
                <link>https://nyaa.si/download/2345.torrent</link>
                <nyaa:infoHash>abcdef0123456789abcdef0123456789abcdef01</nyaa:infoHash>
                <nyaa:seeders>42</nyaa:seeders>
                <nyaa:size>1.5 GiB</nyaa:size>
              </item></channel>
            </rss>
            """;
        var handler = new FakeHandler(_ => FakeHandler.Text(xml, contentType: "application/rss+xml"));
        var client = new TorznabClient(new FakeFactory(handler));
        var idx = new IndexerConfig { Name = "Nyaa RSS", BaseUrl = "https://nyaa.si/?page=rss&c=1_2&f=2" };
        var results = await client.SearchAsync(idx,
            new ReleaseSearch(MediaType.Series, "Test Show", null, 1, 2, "5000"), CancellationToken.None);
        Assert.Single(results);
        Assert.Contains("page=rss&q=Test%20Show&c=1_2&f=2", handler.Requests.Single().RequestUri!.Query);
        Assert.False(TorznabClient.IsDirectNyaa(new Uri("https://nyaa.si/?page=download")));
        Assert.False(TorznabClient.IsDirectNyaa(new Uri("https://nyaa.si.evil.test/?page=rss")));
    }

    [Fact]
    public async Task GenericPublicRss_UsesRecentFeedAndDoesNotCallApi()
    {
        var xml = """
            <rss xmlns:nyaa="https://nyaa.si/xmlns/nyaa" version="2.0">
            <channel><item>
              <title>Test Show S01E02 1080p WEB-DL</title>
              <link>https://rss.example/download/125.torrent</link>
              <nyaa:size>1.3 GiB</nyaa:size>
              <nyaa:seeders>19</nyaa:seeders>
              <nyaa:infoHash>073740343fc24ada2790b79f0b3053c01014f41c</nyaa:infoHash>
              <pubDate>Wed, 28 Oct 2026 20:33:58 -0000</pubDate>
            </item><item>
              <title>Different Show S04E01</title><link>https://rss.example/download/999.torrent</link>
              <nyaa:size>900 MiB</nyaa:size>
            </item></channel></rss>
            """;
        var handler = new FakeHandler(req => {
            Assert.Equal("/download.rss", req.RequestUri!.AbsolutePath);
            Assert.DoesNotContain("apikey", req.RequestUri.Query);
            return FakeHandler.Text(xml, contentType: "application/rss+xml");
        });
        var client = new TorznabClient(new FakeFactory(handler));
        var idx = new IndexerConfig { Name = "Custom RSS", BaseUrl = "https://rss.example/download.rss",
            ApiKey = PublicRssClient.ProtocolMarker };
        var result = await client.SearchAsync(idx, new ReleaseSearch(
            MediaType.Series, "Test Show", null, 1, 2, "5000"), CancellationToken.None);
        var found = Assert.Single(result);
        Assert.Equal(19, found.Seeders);
        Assert.Equal(1395864371L, found.Size);
        Assert.Equal("073740343fc24ada2790b79f0b3053c01014f41c", found.InfoHash);
    }

    [Fact]
    public async Task PublicRssFeedIsFetchedOnceAcrossMultipleSearches()
    {
        var xml = """
            <rss><channel><item><title>Test Show S01E02 1080p WEB-DL</title>
              <enclosure url="https://feed.example/download/77.torrent" length="1395864371" type="application/x-bittorrent"/>
            </item></channel></rss>
            """;
        var handler = new FakeHandler(_ => FakeHandler.Text(xml, contentType: "application/rss+xml"));
        var factory = new FakeFactory(handler);
        var shared = new PublicRssClient(factory);
        var client = new TorznabClient(factory, rss: shared);
        var idx = new IndexerConfig { Name = "feed", BaseUrl = "https://feed.example/rss",
            ApiKey = PublicRssClient.ProtocolMarker };
        var search = new ReleaseSearch(MediaType.Series, "Test Show", null, 1, 2, "5000");
        Assert.Single(await client.SearchAsync(idx, search, CancellationToken.None));
        Assert.Single(await client.SearchAsync(idx, search, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void GenericRss_RejectsHtmlWebPagesAndDetailLinks()
    {
        Assert.Throws<ExternalServiceException>(() => PublicRssClient.ParseFeed(
            "<html><body>Login</body></html>", "Bad RSS", MediaType.Series));
        var xml = """
            <rss><channel><item><title>Test Show S01E01</title>
            <link>https://site.test/view/1234</link></item></channel></rss>
            """;
        Assert.Empty(PublicRssClient.ParseFeed(xml, "Not downloads", MediaType.Series));
    }

    [Fact]
    public void AtomFeedWithTorrentEnclosureIsSupported()
    {
        var xml = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><title>Test Movie 2026 1080p BluRay</title>
                <link rel="enclosure" type="application/x-bittorrent" href="https://site.test/download/3"/>
                <updated>2026-10-28T20:30:00Z</updated>
              </entry>
            </feed>
            """;
        var row = Assert.Single(PublicRssClient.ParseFeed(xml, "Atom", MediaType.Movie));
        Assert.Equal("https://site.test/download/3", row.Link);
        Assert.Contains(2000, row.Categories);
    }

    [Fact]
    public async Task NativeNyaaSearch_ParsesRssAndEscapesQuery()
    {
        var xml = """
            <rss xmlns:nyaa="https://nyaa.si/xmlns/nyaa" version="2.0">
              <channel><item>
                <title>Test Show S01E02 1080p</title>
                <link>https://nyaa.si/download/1234.torrent</link>
                <pubDate>Mon, 26 Dec 2022 16:47:05 -0000</pubDate>
                <nyaa:seeders>42</nyaa:seeders>
                <nyaa:infoHash>ABCDEF0123456789ABCDEF0123456789ABCDEF01</nyaa:infoHash>
                <nyaa:size>1.5 GiB</nyaa:size>
              </item></channel>
            </rss>
            """;
        var handler = new FakeHandler(_ => FakeHandler.Text(xml, contentType: "application/rss+xml"));
        var client = new TorznabClient(new FakeFactory(handler));
        var idx = new IndexerConfig { Name = "Nyaa.si", BaseUrl = "https://nyaa.si/" };
        var results = await client.SearchAsync(idx,
            new ReleaseSearch(MediaType.Series, "Test Show", null, 1, 2, "5000"), CancellationToken.None);
        Assert.Single(results);
        Assert.Equal(42, results[0].Seeders);
        Assert.Equal(1610612736L, results[0].Size);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01", results[0].InfoHash);
        Assert.Contains(5000, results[0].Categories);
        Assert.Equal("https://nyaa.si/download/1234.torrent", results[0].Link);
        Assert.Equal("/?page=rss&q=Test%20Show", handler.Requests.Single().RequestUri!.PathAndQuery);
    }

    [Fact]
    public void NativeNyaa_RejectsAnHtmlLoginPage()
    {
        Assert.Throws<ExternalServiceException>(() => TorznabClient.ParseNyaaRss("<html>login</html>", "Nyaa"));
        Assert.False(TorznabClient.IsDirectNyaa(new Uri("https://nyaa.si.evil.example/")));
        Assert.True(TorznabClient.IsDirectNyaa(new Uri("https://nyaa.si/")));
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

    // Regression for a real Test-button failure: qBittorrent's Execution Log
    // showed successful WebAPI logins, yet the Test button reported a login
    // failure. The old LoginAsync collapsed every unexpected login shape
    // into "check the username and password", and threw on "Ok." with no
    // SID before ever trying torrents/info. These tests use ListAsync only
    // (the read-only Test path); none of them adds, pauses, or deletes a
    // torrent, and none asserts on a cookie value, a password, or a full
    // response body, because those must never leak into an error message.
    [Fact]
    public async Task LoginOkWithoutSidAndInfoSucceeds_ListSucceeds()
    {
        // Bypass (localhost / whitelisted subnet) or a production HTTP
        // handler that consumed Set-Cookie: login is "Ok.", no SID is
        // visible, and the authenticated listing still succeeds. That
        // listing, not the cookie alone, is the proof the connection works.
        var handler = new FakeHandler(req =>
            req.RequestUri!.AbsolutePath == "/api/v2/auth/login"
                ? FakeHandler.Text("Ok.", contentType: "text/plain")
                : FakeHandler.Text("[]"));
        var client = new QBittorrentClient(new FakeFactory(handler));
        var items = await client.ListAsync(Config, "onearr-movies", CancellationToken.None);
        Assert.Empty(items);
        var info = handler.Requests.Single(r => r.RequestUri!.AbsolutePath == "/api/v2/torrents/info");
        Assert.False(info.Headers.TryGetValues("Cookie", out var cookies) && cookies.Any(c => c.Contains("SID=")));
    }

    [Fact]
    public async Task LoginOkWithoutSidAndInfoForbidden_ReportsPostLogin403NotCredentials()
    {
        var handler = new FakeHandler(req =>
            req.RequestUri!.AbsolutePath == "/api/v2/auth/login"
                ? FakeHandler.Text("Ok.", contentType: "text/plain")
                : FakeHandler.Text("Forbidden", HttpStatusCode.Forbidden, "text/plain"));
        var client = new QBittorrentClient(new FakeFactory(handler));
        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => client.ListAsync(Config, "onearr-movies", CancellationToken.None));
        Assert.Contains("HTTP 403", ex.Message);
        Assert.Contains("no session cookie", ex.Message);
        Assert.Contains("not a username/password failure", ex.Message);
        Assert.DoesNotContain("Check the username and password in Settings.", ex.Message);
        // The re-login retry is why the server log shows two successful
        // logins for one failed Test: login, info 403, login, info 403.
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task SidLoginThenInfoForbidden_ReportsPostLogin403NotCredentials()
    {
        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v2/auth/login")
            {
                var r = FakeHandler.Text("Ok.", contentType: "text/plain");
                r.Headers.Add("Set-Cookie", "SID=fakesid123; HttpOnly; path=/");
                return r;
            }
            return FakeHandler.Text("Forbidden", HttpStatusCode.Forbidden, "text/plain");
        });
        var client = new QBittorrentClient(new FakeFactory(handler));
        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => client.ListAsync(Config, "onearr-movies", CancellationToken.None));
        Assert.Contains("HTTP 403 after re-login", ex.Message);
        Assert.Contains("not a username/password failure", ex.Message);
        Assert.Contains("host-header", ex.Message);
        Assert.DoesNotContain("fakesid123", ex.Message);
        Assert.DoesNotContain("Check the username and password in Settings.", ex.Message);
    }

    [Fact]
    public async Task Login403_ReportsIpBanNotCredentials()
    {
        // Documented WebUI API shape: 403 on login is an IP ban after too
        // many failed attempts, not a per-attempt credential verdict.
        var handler = new FakeHandler(_ => FakeHandler.Text("Fails.", HttpStatusCode.Forbidden, "text/plain"));
        var client = new QBittorrentClient(new FakeFactory(handler));
        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => client.ListAsync(Config, "onearr-movies", CancellationToken.None));
        Assert.Contains("HTTP 403", ex.Message);
        Assert.Contains("banned", ex.Message);
        Assert.DoesNotContain("Check the username and password in Settings.", ex.Message);
    }

    [Fact]
    public async Task Login200Fails_StillReportsCredentials()
    {
        // The one shape that really is a credential failure must keep its
        // original message; distinguishing the others must not weaken this.
        var handler = new FakeHandler(_ => FakeHandler.Text("Fails.", contentType: "text/plain"));
        var client = new QBittorrentClient(new FakeFactory(handler));
        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => client.ListAsync(Config, "onearr-movies", CancellationToken.None));
        Assert.Contains("Check the username and password in Settings.", ex.Message);
    }

    [Fact]
    public async Task Login200UnexpectedBody_ReportsUnexpectedNotCredentialsAndDoesNotLeakBody()
    {
        const string body = "<html>not-qbittorrent</html>";
        var handler = new FakeHandler(_ => FakeHandler.Text(body, contentType: "text/html"));
        var client = new QBittorrentClient(new FakeFactory(handler));
        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => client.ListAsync(Config, "onearr-movies", CancellationToken.None));
        Assert.Contains("unexpected body", ex.Message);
        Assert.Contains("not treated as a username/password failure", ex.Message);
        Assert.DoesNotContain(body, ex.Message);
        Assert.DoesNotContain("Check the username and password in Settings.", ex.Message);
    }

    [Fact]
    public async Task Modern204Login_UsesPortScopedCookieForReadOnlyList()
    {
        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v2/auth/login")
            {
                var r = new HttpResponseMessage(HttpStatusCode.NoContent);
                r.Headers.Add("Set-Cookie", "QBT_SID_8080=newsecret; HttpOnly; path=/");
                return r;
            }
            var sent = req.Headers.TryGetValues("Cookie", out var values) && values.Contains("QBT_SID_8080=newsecret");
            return sent ? FakeHandler.Text("[]") : FakeHandler.Text("Forbidden", HttpStatusCode.Forbidden);
        });
        var client = new QBittorrentClient(new FakeFactory(handler));
        Assert.Empty(await client.ListAsync(Config, "onearr-movies", CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
        var info = handler.Requests.Last();
        Assert.Contains("QBT_SID_8080=newsecret", info.Headers.GetValues("Cookie"));
        Assert.DoesNotContain("SID=newsecret", info.Headers.GetValues("Cookie"));
    }

    [Fact]
    public async Task Empty200Login_WithCookie_UsesCookieAndLists()
    {
        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v2/auth/login")
            {
                var r = FakeHandler.Text("", contentType: "text/plain");
                r.Headers.Add("Set-Cookie", "QBT_SID_8080=newsecret; path=/");
                return r;
            }
            return req.Headers.TryGetValues("Cookie", out var cookies) && cookies.Contains("QBT_SID_8080=newsecret")
                ? FakeHandler.Text("[]")
                : FakeHandler.Text("Forbidden", HttpStatusCode.Forbidden);
        });
        var client = new QBittorrentClient(new FakeFactory(handler));
        Assert.Empty(await client.ListAsync(Config, "onearr-movies", CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Empty200Login_WithoutCookie_RequiresProtectedRead()
    {
        var handler = new FakeHandler(req =>
            req.RequestUri!.AbsolutePath == "/api/v2/auth/login"
                ? FakeHandler.Text("", contentType: "text/plain")
                : FakeHandler.Text("Forbidden", HttpStatusCode.Forbidden));
        var client = new QBittorrentClient(new FakeFactory(handler));
        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => client.ListAsync(Config, "onearr-movies", CancellationToken.None));
        Assert.Contains("HTTP 403", ex.Message);
        Assert.Contains("Authentication could not be verified", ex.Message);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Modern401Login_IsReportedAsBadCredentials()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("", HttpStatusCode.Unauthorized));
        var client = new QBittorrentClient(new FakeFactory(handler));
        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => client.ListAsync(Config, "onearr-movies", CancellationToken.None));
        Assert.Contains("HTTP 401", ex.Message);
        Assert.Contains("username and password", ex.Message);
    }

    [Theory]
    [InlineData("""{"success_count":1,"pending_count":0,"failure_count":0}""", HttpStatusCode.OK, true)]
    [InlineData("""{"success_count":0,"pending_count":1,"failure_count":0}""", HttpStatusCode.Accepted, true)]
    [InlineData("""{"success_count":0,"pending_count":0,"failure_count":1}""", HttpStatusCode.OK, false)]
    public async Task ModernTorrentAddResponse_RequiresSuccessOrPending(string body, HttpStatusCode status, bool accepted)
    {
        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v2/auth/login")
            {
                var r = new HttpResponseMessage(HttpStatusCode.NoContent);
                r.Headers.Add("Set-Cookie", "QBT_SID_8080=modern; path=/");
                return r;
            }
            Assert.Contains("QBT_SID_8080=modern", req.Headers.GetValues("Cookie"));
            return FakeHandler.Text(body, status);
        });
        var client = new QBittorrentClient(new FakeFactory(handler));
        const string magnet = "magnet:?xt=urn:btih:abcdef0123456789abcdef0123456789abcdef01";
        if (accepted)
            Assert.Equal("abcdef0123456789abcdef0123456789abcdef01", await client.AddAsync(Config, magnet, "onearr-movies", CancellationToken.None));
        else
            await Assert.ThrowsAsync<ExternalServiceException>(() => client.AddAsync(Config, magnet, "onearr-movies", CancellationToken.None));
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
