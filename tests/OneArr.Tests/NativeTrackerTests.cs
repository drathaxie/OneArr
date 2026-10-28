using System.Net;
using System.Text;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Providers;
using OneArr.Infrastructure.Security;
using Xunit;

namespace OneArr.Tests;

public class NativeTrackerTests
{
    private static readonly TrackerSecretBox Secrets = new(Enumerable.Range(0, 32).Select(n => (byte)n).ToArray());
    private const string Host = "https://www.torrentleech.org/";
    private static IndexerConfig Config(int id = 1) => new()
    {
        Id = id, Name = "TorrentLeech", BaseUrl = Host,
        ApiKey = Secrets.Protect(new NativeTrackerCredentials("torrentleech", "my-user", "my-password"))
    };
    private static HttpResponseMessage Html(string html, HttpStatusCode code = HttpStatusCode.OK) =>
        FakeHandler.Text(html, code, "text/html");

    private static FakeHandler Upstream(List<string> calls)
    {
        var json = """
        {"torrentList":[{"fid":1234,"filename":"Show.S01E02.torrent","name":"Show S01E02 1080p WEB-DL","categoryID":32,
          "seeders":23,"size":1073741824,"addedTimestamp":"2026-10-20 12:00:00"}]}
        """;
        return new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add(request.Method + " " + path);
            if (path == "/user/account/login/" && request.Method == HttpMethod.Get)
            {
                var r = Html("""<form name="login-form"><input type="hidden" name="csrf_token" value="aaa"/></form>""");
                r.Headers.TryAddWithoutValidation("Set-Cookie", "PHPSESSID=initial; HttpOnly; path=/");
                return r;
            }
            if (path == "/user/account/login/" && request.Method == HttpMethod.Post)
            {
                var form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Contains("username=my-user", form);
                Assert.Contains("password=my-password", form);
                Assert.Contains("csrf_token=aaa", form);
                var r = Html("", HttpStatusCode.Found);
                r.Headers.TryAddWithoutValidation("Set-Cookie", "PHPSESSID=signed-in; HttpOnly; path=/");
                return r;
            }
            var cookie = request.Headers.TryGetValues("Cookie", out var vals)
                && vals.Any(c => c.Contains("PHPSESSID=signed-in"));
            if (!cookie) return Html("Forbidden", HttpStatusCode.Forbidden);
            if (path == "/") return Html("""<a href="/user/account/logout">Logout</a>""");
            if (path.StartsWith("/torrents/browse/list/"))
                return FakeHandler.Text(json);
            if (path == "/download/1234/Show.S01E02.torrent")
                return new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new ByteArrayContent(Encoding.ASCII.GetBytes("d4:infod4:name4:testee")) };
            return Html("not found", HttpStatusCode.NotFound);
        });
    }

    [Fact]
    public void CredentialsAreAuthenticatedAndBoundToPersistentKey()
    {
        var payload = Secrets.Protect(new NativeTrackerCredentials("torrentleech", "alice", "secret"));
        Assert.StartsWith("native1:", payload);
        Assert.DoesNotContain("secret", payload);
        Assert.Equal("alice", Secrets.Read(payload).Username);
        Assert.Equal("secret", Secrets.Read(payload).Password);
        var otherKey = new TrackerSecretBox(new byte[32]);
        Assert.Throws<ExternalServiceException>(() => otherKey.Read(payload));
        var bytes = Convert.FromBase64String(payload["native1:".Length..]);
        bytes[^1] ^= 0x23;
        Assert.Throws<ExternalServiceException>(() => Secrets.Read("native1:" + Convert.ToBase64String(bytes)));
    }

    [Fact]
    public async Task LoginSearchAndAuthenticatedTorrentFetch_AreConnectedEndToEnd()
    {
        var calls = new List<string>();
        var handler = Upstream(calls);
        var native = new NativeTrackerClient(new FakeFactory(handler), Secrets);
        var client = new TorznabClient(new FakeFactory(handler), native);
        var idx = Config();
        var releases = await client.SearchAsync(idx,
            new ReleaseSearch(MediaType.Series, "Show", null, 1, 2, "5000"), CancellationToken.None);
        var release = Assert.Single(releases);
        Assert.Equal("https://www.torrentleech.org/download/1234/Show.S01E02.torrent", release.Link);
        Assert.Contains(5000, release.Categories);
        Assert.Equal(23, release.Seeders);
        var bytes = await native.DownloadTorrentAsync(idx, new Uri(release.Link), CancellationToken.None);
        Assert.StartsWith("d4:infod", Encoding.ASCII.GetString(bytes));
        Assert.Single(calls.Where(c => c == "POST /user/account/login/"));
        Assert.Contains(calls, c => c.Contains("S01E02", StringComparison.Ordinal));
        Assert.Contains("GET /download/1234/Show.S01E02.torrent", calls);
    }

    [Fact]
    public async Task IncorrectLoginIsNotTreatedAsAuthenticated()
    {
        var handler = new FakeHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
                return Html("<div>Invalid password</div>");
            return Html("""<form name="login-form"></form>""");
        });
        var client = new NativeTrackerClient(new FakeFactory(handler), Secrets);
        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => client.TestAsync(Config(), CancellationToken.None));
        Assert.Contains("did not confirm", ex.Message);
        Assert.DoesNotContain("my-password", ex.Message);
    }

    [Fact]
    public async Task CredentialsAreNeverSentToArbitraryMirrors()
    {
        var handler = new FakeHandler(_ => throw new Exception("Network should not be called"));
        var client = new NativeTrackerClient(new FakeFactory(handler), Secrets);
        var idx = Config();
        idx.BaseUrl = "https://www.torrentleech.org.evil.test/";
        await Assert.ThrowsAsync<ExternalServiceException>(() => client.TestAsync(idx, CancellationToken.None));
        Assert.False(NativeTrackerClient.IsTorrentLeechDownload(new Uri("https://evil.test/download/123/abc.torrent")));
        Assert.False(NativeTrackerClient.IsTorrentLeechDownload(new Uri("http://www.torrentleech.org/download/123/abc.torrent")));
    }

    [Fact]
    public void TorrentInfoHashMatchesSha1OfExactInfoDictionary()
    {
        var torrent = Encoding.ASCII.GetBytes("d8:announce12:https://a.tld4:infod4:name4:testee");
        // Malformed announce fields must fail closed, not skip validation.
        Assert.Throws<ExternalServiceException>(() => QBittorrentClient.TorrentInfoHash(torrent));
        var valid = Encoding.ASCII.GetBytes("d4:infod4:name4:testee");
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            Encoding.ASCII.GetBytes("d4:name4:teste"))).ToLowerInvariant();
        Assert.Equal(expected, QBittorrentClient.TorrentInfoHash(valid));
        Assert.Throws<ExternalServiceException>(() => QBittorrentClient.TorrentInfoHash(
            Encoding.ASCII.GetBytes("d4:infod4:name4:test")));
    }

    [Fact]
    public async Task ManualGrabFromPrivateTracker_UploadsBytesAndRecordsTorrentHash()
    {
        using var world = new TestWorld();
        await world.ConfigureAsync(automaticGrab: false);
        var movieId = await world.AddMovieAsync("The Matrix", 1999);
        await using (var db = await world.Db.CreateDbContextAsync())
        {
            db.Indexers.Add(Config());
            await db.SaveChangesAsync();
        }
        var uploads = 0;
        world.Http.Route(TestWorld.QbitHost, request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v2/auth/login")
            {
                var login = FakeHandler.Text("Ok.");
                login.Headers.Add("Set-Cookie", "SID=qbit123; path=/");
                return login;
            }
            if (request.RequestUri.AbsolutePath == "/api/v2/torrents/add")
            {
                var data = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Contains("torrents", data);
                Assert.Contains("onearr-movies", data);
                uploads++;
                return FakeHandler.Text("Ok.");
            }
            return FakeHandler.Text("Not Found", HttpStatusCode.NotFound);
        });
        world.Http.Route("www.torrentleech.org", req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/user/account/login/" && req.Method == HttpMethod.Get)
                return Html("<form name='login-form'></form>");
            if (path == "/user/account/login/" && req.Method == HttpMethod.Post)
            {
                var logged = Html("", HttpStatusCode.Found);
                logged.Headers.Add("Set-Cookie", "PHPSESSID=logged; path=/");
                return logged;
            }
            if (!req.Headers.TryGetValues("Cookie", out var cookies) ||
                !cookies.Any(x => x.Contains("PHPSESSID=logged")))
                return Html("Forbidden", HttpStatusCode.Forbidden);
            if (path == "/") return Html("<a href='/user/account/logout'>Logout</a>");
            if (path.StartsWith("/download/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content =
                    new ByteArrayContent(Encoding.ASCII.GetBytes("d4:infod4:name4:testee")) };
            return Html("Not Found", HttpStatusCode.NotFound);
        });
        var factory = new FakeFactory(world.Http);
        var native = new NativeTrackerClient(factory, Secrets);
        var releases = new OneArr.Infrastructure.Services.ReleaseService(world.Db,
            new TorznabClient(factory, native),
            new OneArr.Infrastructure.Services.ReleaseRanker(new OneArr.Infrastructure.Services.ReleaseParser()),
            new QBittorrentClient(factory), world.Time, native);
        var job = await releases.GrabAsync(new OneArr.Infrastructure.Services.GrabRequest(
            movieId, null, "The.Matrix.1999.1080p.BluRay.x264-GRP",
            "https://www.torrentleech.org/download/1234/The.Matrix.1999.1080p.BluRay.x264-GRP.torrent", null),
            CancellationToken.None);
        Assert.Equal(1, uploads);
        Assert.Equal(QBittorrentClient.TorrentInfoHash(
            Encoding.ASCII.GetBytes("d4:infod4:name4:testee")), job.Hash);
        Assert.True(job.DownloadClientId.HasValue);
    }

    [Fact]
    public async Task MultipartUploadToQbittorrentSendsBytesAndCategory()
    {
        var captured = "";
        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v2/auth/login")
            {
                var r = FakeHandler.Text("Ok.");
                r.Headers.Add("Set-Cookie", "SID=qbit-token; path=/");
                return r;
            }
            Assert.Equal("/api/v2/torrents/add", req.RequestUri.AbsolutePath);
            Assert.Contains("SID=qbit-token", req.Headers.GetValues("Cookie"));
            captured = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeHandler.Text("Ok.");
        });
        var qbit = new QBittorrentClient(new FakeFactory(handler));
        var config = new DownloadClientConfig
        {
            Id = 20, Name = "qBittorrent", BaseUrl = "http://qbittorrent:8080",
            Username = "admin", Password = "password"
        };
        await qbit.AddTorrentAsync(config, Encoding.ASCII.GetBytes("d4:infod4:name4:testee"),
            "onearr-tv", CancellationToken.None);
        Assert.True(captured.Contains("name=torrents") || captured.Contains("name=\"torrents\""));
        Assert.Contains("release.torrent", captured);
        Assert.True(captured.Contains("name=category") || captured.Contains("name=\"category\""));
        Assert.Contains("onearr-tv", captured);
    }
}
