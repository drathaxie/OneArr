using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure;
using OneArr.Infrastructure.Files;
using OneArr.Infrastructure.Persistence;
using OneArr.Infrastructure.Providers;
using OneArr.Infrastructure.Services;

namespace OneArr.Tests;

/// <summary>A clock tests can move forward, so backoff and due times are exercised without waiting.</summary>
public sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now;
    public ManualTime(DateTimeOffset start) => _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Routes by host name so one fake handler can serve the indexer and the download client. Nothing reaches the network.</summary>
public sealed class RoutingHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _hosts = new(StringComparer.OrdinalIgnoreCase);
    public List<HttpRequestMessage> Requests { get; } = new();

    public void Route(string host, Func<HttpRequestMessage, HttpResponseMessage> respond) => _hosts[host] = respond;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (!_hosts.TryGetValue(request.RequestUri!.Host, out var respond))
            throw new InvalidOperationException($"Unrouted test request to {request.RequestUri.Host}; tests must never reach real hosts");
        return Task.FromResult(respond(request));
    }
}

/// <summary>Fake Torznab indexer. Serves the body the test sets, or a server error when Fail is set.</summary>
public sealed class FakeIndexer
{
    public string Body { get; set; } = "<rss version=\"2.0\" xmlns:torznab=\"http://torznab.com/schemas/2015/feed\"><channel></channel></rss>";
    public bool Fail { get; set; }
    public List<string> Queries { get; } = new();

    public HttpResponseMessage Respond(HttpRequestMessage req)
    {
        Queries.Add(req.RequestUri!.Query);
        if (Fail) return FakeHandler.Text("server error", HttpStatusCode.InternalServerError, "text/plain");
        return FakeHandler.Text(Body, contentType: "application/xml");
    }

    public static string ReadFixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

/// <summary>
/// Fake qBittorrent WebUI v2 with a torrent list the test controls. Add records the magnet and category; the test moves
/// torrents through downloading and completion. Delete and pause endpoints are recorded so tests can prove they were never called.
/// </summary>
public sealed class FakeQbit
{
    public sealed record Torrent(string Hash, string Name, string Category, string State, double Progress, string? ContentPath);

    public List<Torrent> Torrents { get; } = new();
    public List<string> Calls { get; } = new();
    public int AddCount => Calls.Count(c => c == "torrents/add");
    public bool FailAdd { get; set; }

    public HttpResponseMessage Respond(HttpRequestMessage req)
    {
        var path = req.RequestUri!.AbsolutePath;
        Calls.Add(path.Replace("/api/v2/", ""));
        if (path == "/api/v2/auth/login")
        {
            var r = FakeHandler.Text("Ok.", contentType: "text/plain");
            r.Headers.Add("Set-Cookie", "SID=fakesid; HttpOnly; path=/");
            return r;
        }
        if (!req.Headers.TryGetValues("Cookie", out var cookies) || !cookies.Any(c => c.Contains("SID=fakesid")))
            return FakeHandler.Text("Forbidden", HttpStatusCode.Forbidden, "text/plain");

        switch (path)
        {
            case "/api/v2/torrents/add":
            {
                if (FailAdd) return FakeHandler.Text("boom", HttpStatusCode.InternalServerError, "text/plain");
                var form = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var fields = form.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""));
                var link = fields["urls"];
                var hash = Hash(link);
                Torrents.Add(new Torrent(hash, "added", fields["category"], "downloading", 0, null));
                return FakeHandler.Text("Ok.", contentType: "text/plain");
            }
            case "/api/v2/torrents/info":
            {
                var category = Uri.UnescapeDataString(req.RequestUri.Query.Replace("?category=", ""));
                var rows = Torrents.Where(t => t.Category == category).Select(t => new
                {
                    hash = t.Hash, name = t.Name, state = t.State, progress = t.Progress, content_path = t.ContentPath, category = t.Category, size = 1L,
                });
                return FakeHandler.Text(System.Text.Json.JsonSerializer.Serialize(rows));
            }
            default:
                return FakeHandler.Text("not found", HttpStatusCode.NotFound, "text/plain");
        }
    }

    public void SetTorrent(string hash, string state, double progress, string? contentPath)
    {
        var i = Torrents.FindIndex(t => t.Hash == hash);
        if (i < 0) throw new InvalidOperationException("no such torrent in fake client");
        var t = Torrents[i];
        Torrents[i] = t with { State = state, Progress = progress, ContentPath = contentPath };
    }

    private static string Hash(string link)
    {
        var start = link.IndexOf("btih:", StringComparison.OrdinalIgnoreCase) + 5;
        return link.Substring(start, 40).ToLowerInvariant();
    }
}

/// <summary>
/// Builds the real services over an in-memory SQLite database, a temp directory tree and the fake upstreams.
/// Use this instead of hand-wiring so integration tests exercise the same graph the API uses.
/// </summary>
public sealed class TestWorld : IDisposable
{
    public const string IndexerHost = "indexer.test";
    public const string QbitHost = "qbit.test";

    public IDbContextFactory<OneArrDbContext> Db { get; }
    public ManualTime Time { get; }
    public TempTree Dirs { get; } = new();
    public RoutingHandler Http { get; } = new();
    public FakeIndexer Indexer { get; } = new();
    public FakeQbit Qbit { get; } = new();
    public SettingsStore Settings { get; }
    public ReleaseService Releases { get; }
    public DownloadService Downloads { get; }
    public WantedSearchService Search { get; private set; }
    public JobRunner Runner { get; }
    public SearchPolicy Policy { get; }

    private readonly Microsoft.Data.Sqlite.SqliteConnection _keepAlive;

    public TestWorld(SearchPolicy? policy = null)
    {
        (Db, _keepAlive) = TestDb.Create();
        Time = new ManualTime(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        Policy = policy ?? new SearchPolicy();
        Http.Route(IndexerHost, Indexer.Respond);
        Http.Route(QbitHost, Qbit.Respond);

        var factory = new FakeFactory(Http);
        Settings = new SettingsStore(Db);
        var ranker = new ReleaseRanker(new ReleaseParser());
        Releases = new ReleaseService(Db, new TorznabClient(factory), ranker, new QBittorrentClient(factory), Time);
        Downloads = new DownloadService(Db, new QBittorrentClient(factory), new SafeImporter(TimeSpan.Zero), Settings, Time);
        Search = new WantedSearchService(Db, Releases, Settings, Time, Policy);
        Runner = new JobRunner(Db, Time);
    }

    /// <summary>Recreates the search service over the same database, as after a process restart.</summary>
    public WantedSearchService RestartSearch() => Search = new WantedSearchService(Db, new ReleaseService(Db, new TorznabClient(new FakeFactory(Http)), new ReleaseRanker(new ReleaseParser()), new QBittorrentClient(new FakeFactory(Http)), Time), Settings, Time, Policy);

    public async Task<int> AddProfileAsync()
    {
        await using var db = Db.CreateDbContext();
        var p = new QualityProfile { Name = "HD-1080p", MinResolution = 720, CutoffResolution = 1080, AllowWebSources = true };
        db.QualityProfiles.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    /// <summary>Configures one indexer, one download client, the roots under the temp tree, and automatic grabbing on.</summary>
    public async Task ConfigureAsync(bool automaticGrab = true)
    {
        var profileId = await AddProfileAsync();
        await using (var db = Db.CreateDbContext())
        {
            db.Indexers.Add(new IndexerConfig { Name = "fixture", BaseUrl = $"http://{IndexerHost}/", ApiKey = "test-key", Enabled = true });
            db.DownloadClients.Add(new DownloadClientConfig { Name = "qbit", BaseUrl = $"http://{QbitHost}", Username = "admin", Password = "pw", MovieCategory = "onearr-movies", TvCategory = "onearr-tv", Enabled = true });
            await db.SaveChangesAsync();
        }
        await Settings.SetAsync(SettingKeys.AutomaticGrab, automaticGrab.ToString());
        await Settings.SetAsync(SettingKeys.DryRunImport, "false");
        await Settings.SetAsync(SettingKeys.TorrentRoot, Dirs.Torrents);
        await Settings.SetAsync(SettingKeys.MediaRoot, Dirs.Media);
        ProfileId = profileId;
    }

    public int ProfileId { get; private set; }

    public async Task<int> AddMovieAsync(string title, int year, bool hasFile = false, bool monitored = true)
    {
        await using var db = Db.CreateDbContext();
        var m = new MediaItem
        {
            MediaType = MediaType.Movie, Provider = "tmdb", ProviderId = Guid.NewGuid().ToString("N")[..6], Title = title, Year = year,
            Monitored = monitored, HasFile = hasFile, QualityProfileId = ProfileId, RootFolder = Path.Combine(Dirs.Media, "movies"), CreatedAt = Time.GetUtcNow(),
        };
        db.MediaItems.Add(m);
        await db.SaveChangesAsync();
        return m.Id;
    }

    public async Task<(int SeriesId, List<int> EpisodeIds)> AddSeriesAsync(string title, int episodes)
    {
        await using var db = Db.CreateDbContext();
        var s = new MediaItem
        {
            MediaType = MediaType.Series, Provider = "tmdb", ProviderId = Guid.NewGuid().ToString("N")[..6], Title = title, Year = 2011,
            Monitored = true, QualityProfileId = ProfileId, RootFolder = Path.Combine(Dirs.Media, "tv"), CreatedAt = Time.GetUtcNow(),
        };
        db.MediaItems.Add(s);
        await db.SaveChangesAsync();
        var ids = new List<int>();
        for (var e = 1; e <= episodes; e++)
        {
            var ep = new Episode { MediaItemId = s.Id, SeasonNumber = 1, EpisodeNumber = e, ProviderId = $"{s.Id}-{e}", Title = $"Episode {e}", AirDate = new DateOnly(2010, 1, e), Monitored = true };
            db.Episodes.Add(ep);
            await db.SaveChangesAsync();
            ids.Add(ep.Id);
        }
        return (s.Id, ids);
    }

    public static string Torznab(params (string Title, string Hash, int Seeders)[] items)
    {
        var sb = new StringBuilder("<rss version=\"2.0\" xmlns:torznab=\"http://torznab.com/schemas/2015/feed\"><channel>");
        foreach (var (title, hash, seeders) in items)
        {
            sb.Append($"<item><title>{title}</title><link>magnet:?xt=urn:btih:{hash}&amp;dn=x</link><size>2000000000</size>");
            sb.Append($"<category>2000</category><torznab:attr name=\"seeders\" value=\"{seeders}\" /><torznab:attr name=\"infohash\" value=\"{hash}\" /></item>");
        }
        return sb.Append("</channel></rss>").ToString();
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
        Dirs.Dispose();
    }
}
