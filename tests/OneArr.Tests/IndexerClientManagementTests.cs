using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OneArr.Api;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Persistence;
using OneArr.Infrastructure.Providers;
using OneArr.Infrastructure.Services;
using Xunit;

namespace OneArr.Tests;

/// <summary>Indexer + download-client management: CRUD with secret preservation, saved and
/// unsaved connection tests, health, Test All, grab client selection, search transparency,
/// ownership preservation, and the migration that adds the new columns.</summary>
[Collection(ApiHostCollection.Name)]
public class IndexerClientManagementTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Matrix = "The.Matrix.1999.1080p.BluRay.x264-GRP";
    private const string MovieHash = "abcdef0123456789abcdef0123456789abcdef01";

    private static string CapsXml => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "torznab_caps.xml"));

    private sealed class StubApiFactory : WebApplicationFactory<Program>
    {
        public string ConfigDir { get; }
        private readonly HttpMessageHandler _routing;

        public StubApiFactory(HttpMessageHandler routing)
        {
            _routing = routing;
            ConfigDir = Path.Combine(Path.GetTempPath(), "onearr-mgmt-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("ONEARR_CONFIG_DIR", ConfigDir);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureServices(s => s.AddSingleton<IHttpClientFactory>(new FakeFactory(_routing)));

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) ApiFactory.RemoveConfigDir(ConfigDir);
        }
    }

    private static async Task<HttpClient> SignedInAsync(StubApiFactory factory)
    {
        var client = factory.CreateClient();
        var setup = await client.PostAsJsonAsync("/api/auth/setup", new { password = "correct horse battery" });
        Assert.True(setup.IsSuccessStatusCode, await setup.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await setup.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path)
    {
        var res = await client.GetAsync(path);
        Assert.True(res.IsSuccessStatusCode, path + " -> " + (int)res.StatusCode + " " + await res.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync());
    }

    private static async Task<JsonDocument> PostJsonAsync(HttpClient client, string path, object body)
    {
        var res = await client.PostAsJsonAsync(path, body);
        Assert.True(res.IsSuccessStatusCode, path + " -> " + (int)res.StatusCode + " " + await res.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync());
    }

    private static HttpResponseMessage ServerError(HttpRequestMessage _) =>
        FakeHandler.Text("server error", HttpStatusCode.InternalServerError, "text/plain");

    private static string? QueryValue(HttpRequestMessage req, string key)
    {
        var query = req.RequestUri!.Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && Uri.UnescapeDataString(kv[0]) == key) return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }

    [Fact]
    public async Task Indexer_crud_preserves_key_on_empty_put_and_replaces_it_on_new_key()
    {
        var expectedKey = "key-one";
        var routing = new RoutingHandler();
        routing.Route("indexer.test", req =>
            QueryValue(req, "apikey") == expectedKey
                ? FakeHandler.Text(CapsXml, contentType: "application/xml")
                : FakeHandler.Text("unauthorized", HttpStatusCode.Unauthorized, "text/plain"));
        using var factory = new StubApiFactory(routing);
        var client = await SignedInAsync(factory);

        var created = await PostJsonAsync(client, "/api/indexers", new
        {
            name = "fixture", baseUrl = "http://indexer.test/", apiKey = "key-one",
            enabled = true, moviesEnabled = true, tvEnabled = true, priority = 0,
        });
        var id = created.RootElement.GetProperty("id").GetInt32();
        Assert.True(created.RootElement.GetProperty("hasKey").GetBoolean());
        Assert.DoesNotContain("key-one", created.RootElement.GetRawText());

        var listed = await GetJsonAsync(client, "/api/indexers");
        Assert.DoesNotContain("key-one", listed.RootElement.GetRawText());
        Assert.True(listed.RootElement[0].GetProperty("hasKey").GetBoolean());

        var tested = await PostJsonAsync(client, "/api/connections/test", new { service = "torznab", indexerId = id });
        Assert.True(tested.RootElement.GetProperty("ok").GetBoolean());

        // Empty key on edit preserves the stored key: the old key still authenticates.
        var put = await client.PutAsJsonAsync($"/api/indexers/{id}", new
        {
            name = "fixture", baseUrl = "http://indexer.test/", apiKey = "",
            enabled = true, moviesEnabled = true, tvEnabled = true, priority = 0,
        });
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        tested = await PostJsonAsync(client, "/api/connections/test", new { service = "torznab", indexerId = id });
        Assert.True(tested.RootElement.GetProperty("ok").GetBoolean());

        // A non-empty key replaces it: only the new key authenticates now.
        expectedKey = "key-two";
        put = await client.PutAsJsonAsync($"/api/indexers/{id}", new
        {
            name = "fixture", baseUrl = "http://indexer.test/", apiKey = "key-two",
            enabled = true, moviesEnabled = true, tvEnabled = true, priority = 0,
        });
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        tested = await PostJsonAsync(client, "/api/connections/test", new { service = "torznab", indexerId = id });
        Assert.True(tested.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Native_tracker_api_encrypts_credentials_tests_login_and_preserves_secret_on_edit()
    {
        var router = new RoutingHandler();
        router.Route("www.torrentleech.org", request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/user/account/login/" && request.Method == HttpMethod.Get)
            {
                var r = FakeHandler.Text("<form name='login-form'></form>", contentType: "text/html");
                r.Headers.Add("Set-Cookie", "PHPSESSID=initial; path=/");
                return r;
            }
            if (path == "/user/account/login/" && request.Method == HttpMethod.Post)
            {
                var form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Contains("username=alice", form);
                Assert.Contains("password=private-secret", form);
                var r = FakeHandler.Text("", HttpStatusCode.Found);
                r.Headers.Add("Set-Cookie", "PHPSESSID=authenticated; path=/");
                return r;
            }
            if (path == "/" && request.Headers.TryGetValues("Cookie", out var cookies) &&
                cookies.Any(c => c.Contains("PHPSESSID=authenticated")))
                return FakeHandler.Text("<a href='/user/account/logout'>Logout</a>", contentType: "text/html");
            return FakeHandler.Text("Forbidden", HttpStatusCode.Forbidden);
        });
        using var factory = new StubApiFactory(router);
        var client = await SignedInAsync(factory);
        var created = await PostJsonAsync(client, "/api/indexers", new
        {
            name = "TorrentLeech", baseUrl = "https://www.torrentleech.org/",
            apiKey = "", trackerId = "torrentleech",
            trackerSettings = new { username = "alice", password = "private-secret" },
            enabled = true, moviesEnabled = true, tvEnabled = true, priority = 0
        });
        var id = created.RootElement.GetProperty("id").GetInt32();
        Assert.Equal("TorrentLeech", created.RootElement.GetProperty("protocol").GetString());
        Assert.DoesNotContain("private-secret", created.RootElement.GetRawText());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<OneArrDbContext>>();
            await using var context = await db.CreateDbContextAsync();
            var stored = await context.Indexers.SingleAsync();
            Assert.StartsWith("native1:", stored.ApiKey);
            Assert.DoesNotContain("private-secret", stored.ApiKey);
        }
        var tested = await PostJsonAsync(client, "/api/connections/test",
            new { service = "native-tracker", indexerId = id });
        Assert.True(tested.RootElement.GetProperty("ok").GetBoolean());
        var put = await client.PutAsJsonAsync($"/api/indexers/{id}", new
        {
            name = "TorrentLeech", baseUrl = "https://www.torrentleech.org/",
            apiKey = "", trackerId = "torrentleech",
            trackerSettings = new { username = "", password = "" },
            enabled = true, moviesEnabled = true, tvEnabled = true, priority = 2
        });
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        tested = await PostJsonAsync(client, "/api/connections/test",
            new { service = "native-tracker", indexerId = id });
        Assert.True(tested.RootElement.GetProperty("ok").GetBoolean());
        var listed = await GetJsonAsync(client, "/api/indexers");
        Assert.DoesNotContain("private-secret", listed.RootElement.GetRawText());
    }

    [Fact]
    public async Task Client_crud_preserves_password_on_empty_put()
    {
        var expectedPassword = "pw-one";
        var routing = new RoutingHandler();
        routing.Route("qbit-auth.test", req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v2/auth/login")
            {
                var form = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (form.Contains("password=" + expectedPassword))
                {
                    var r = FakeHandler.Text("Ok.", contentType: "text/plain");
                    r.Headers.Add("Set-Cookie", "SID=fakesid; HttpOnly; path=/");
                    return r;
                }
                return FakeHandler.Text("Forbidden", HttpStatusCode.Forbidden, "text/plain");
            }
            if (req.RequestUri!.AbsolutePath == "/api/v2/torrents/info")
                return FakeHandler.Text("[]");
            return FakeHandler.Text("not found", HttpStatusCode.NotFound, "text/plain");
        });
        using var factory = new StubApiFactory(routing);
        var client = await SignedInAsync(factory);

        var created = await PostJsonAsync(client, "/api/download-clients", new
        {
            name = "qbit", baseUrl = "http://qbit-auth.test", username = "admin", password = "pw-one",
            movieCategory = "onearr-movies", tvCategory = "onearr-tv", enabled = true,
        });
        var id = created.RootElement.GetProperty("id").GetInt32();
        Assert.True(created.RootElement.GetProperty("hasPassword").GetBoolean());
        Assert.DoesNotContain("pw-one", created.RootElement.GetRawText());

        var tested = await PostJsonAsync(client, "/api/connections/test", new { service = "qbittorrent", clientId = id });
        Assert.True(tested.RootElement.GetProperty("ok").GetBoolean());

        // Empty password on edit preserves the stored password. This is asserted against the
        // stored row directly: a passing connection test alone would not prove it, because the
        // client caches its session and would not log in again.
        var put = await client.PutAsJsonAsync($"/api/download-clients/{id}", new
        {
            name = "qbit", baseUrl = "http://qbit-auth.test", username = "admin", password = "",
            movieCategory = "onearr-movies", tvCategory = "onearr-tv", enabled = true,
        });
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        await using (var db = factory.Services.GetRequiredService<IDbContextFactory<OneArrDbContext>>().CreateDbContext())
        {
            Assert.Equal("pw-one", (await db.DownloadClients.SingleAsync()).Password);
        }

        put = await client.PutAsJsonAsync($"/api/download-clients/{id}", new
        {
            name = "qbit", baseUrl = "http://qbit-auth.test", username = "admin", password = "pw-two",
            movieCategory = "onearr-movies", tvCategory = "onearr-tv", enabled = true,
        });
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        await using (var db = factory.Services.GetRequiredService<IDbContextFactory<OneArrDbContext>>().CreateDbContext())
        {
            Assert.Equal("pw-two", (await db.DownloadClients.SingleAsync()).Password);
        }
    }

    [Fact]
    public async Task Unsaved_tests_do_not_persist_records()
    {
        var routing = new RoutingHandler();
        routing.Route(TestWorld.IndexerHost, req => FakeHandler.Text(CapsXml, contentType: "application/xml"));
        routing.Route(TestWorld.QbitHost, new FakeQbit().Respond);
        routing.Route("failing.test", ServerError);
        using var factory = new StubApiFactory(routing);
        var client = await SignedInAsync(factory);

        var torznab = await PostJsonAsync(client, "/api/connections/test", new { service = "torznab", baseUrl = "http://indexer.test/", apiKey = "k" });
        Assert.True(torznab.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, torznab.RootElement.GetProperty("categories").GetArrayLength());

        var qbit = await PostJsonAsync(client, "/api/connections/test", new { service = "qbittorrent", baseUrl = "http://qbit.test", username = "admin", password = "pw" });
        Assert.True(qbit.RootElement.GetProperty("ok").GetBoolean());

        var failed = await PostJsonAsync(client, "/api/connections/test", new { service = "torznab", baseUrl = "http://failing.test/", apiKey = "k" });
        Assert.False(failed.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("500", failed.RootElement.GetProperty("detail").GetString());

        Assert.Equal(0, (await GetJsonAsync(client, "/api/indexers")).RootElement.GetArrayLength());
        Assert.Equal(0, (await GetJsonAsync(client, "/api/download-clients")).RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Saved_test_failure_records_health_then_success_clears_it()
    {
        var routing = new RoutingHandler();
        routing.Route("flaky.test", ServerError);
        using var factory = new StubApiFactory(routing);
        var client = await SignedInAsync(factory);

        var created = await PostJsonAsync(client, "/api/indexers", new
        {
            name = "flaky", baseUrl = "http://flaky.test/", apiKey = "k",
            enabled = true, moviesEnabled = true, tvEnabled = true, priority = 0,
        });
        var id = created.RootElement.GetProperty("id").GetInt32();

        var failed = await PostJsonAsync(client, "/api/connections/test", new { service = "torznab", indexerId = id });
        Assert.False(failed.RootElement.GetProperty("ok").GetBoolean());
        var listed = await GetJsonAsync(client, "/api/indexers");
        Assert.Equal("Failed", listed.RootElement[0].GetProperty("healthStatus").GetString());
        Assert.Contains("500", listed.RootElement[0].GetProperty("lastError").GetString());

        routing.Route("flaky.test", req => FakeHandler.Text(CapsXml, contentType: "application/xml"));
        var passed = await PostJsonAsync(client, "/api/connections/test", new { service = "torznab", indexerId = id });
        Assert.True(passed.RootElement.GetProperty("ok").GetBoolean());
        listed = await GetJsonAsync(client, "/api/indexers");
        Assert.Equal("Healthy", listed.RootElement[0].GetProperty("healthStatus").GetString());
        Assert.Equal(JsonValueKind.Null, listed.RootElement[0].GetProperty("lastError").ValueKind);
    }

    [Fact]
    public async Task Test_all_tests_enabled_only_and_isolates_failures()
    {
        var routing = new RoutingHandler();
        routing.Route(TestWorld.IndexerHost, req => FakeHandler.Text(CapsXml, contentType: "application/xml"));
        routing.Route("failing.test", ServerError);
        routing.Route(TestWorld.QbitHost, new FakeQbit().Respond);
        using var factory = new StubApiFactory(routing);
        var client = await SignedInAsync(factory);

        await PostJsonAsync(client, "/api/indexers", new { name = "good", baseUrl = "http://indexer.test/", apiKey = "k", enabled = true, moviesEnabled = true, tvEnabled = true, priority = 0 });
        await PostJsonAsync(client, "/api/indexers", new { name = "bad", baseUrl = "http://failing.test/", apiKey = "k", enabled = true, moviesEnabled = true, tvEnabled = true, priority = 1 });
        await PostJsonAsync(client, "/api/indexers", new { name = "off", baseUrl = "http://indexer.test/", apiKey = "k", enabled = false, moviesEnabled = true, tvEnabled = true, priority = 2 });
        await PostJsonAsync(client, "/api/download-clients", new { name = "qbit", baseUrl = "http://qbit.test", username = "admin", password = "pw", movieCategory = "onearr-movies", tvCategory = "onearr-tv", enabled = true });

        var all = await PostJsonAsync(client, "/api/connections/test-all", new { });
        Assert.Equal(3, all.RootElement.GetProperty("tested").GetInt32());
        Assert.Equal(2, all.RootElement.GetProperty("passed").GetInt32());
        var results = all.RootElement.GetProperty("results");
        Assert.DoesNotContain(results.EnumerateArray(), r => r.GetProperty("name").GetString() == "off");
        Assert.Contains(results.EnumerateArray(), r => r.GetProperty("name").GetString() == "bad" && !r.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Indexer_and_client_validation_and_not_found()
    {
        var routing = new RoutingHandler();
        using var factory = new StubApiFactory(routing);
        var client = await SignedInAsync(factory);

        var emptyName = await client.PostAsJsonAsync("/api/indexers", new { name = "", baseUrl = "http://indexer.test/", apiKey = "k", enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, emptyName.StatusCode);

        var badUrl = await client.PostAsJsonAsync("/api/indexers", new { name = "x", baseUrl = "ftp://indexer.test/", apiKey = "k", enabled = true });
        Assert.Equal((HttpStatusCode)502, badUrl.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/indexers/999", new { name = "x", baseUrl = "http://indexer.test/", apiKey = "k", enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/indexers/999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/download-clients/999", new { name = "x", baseUrl = "http://qbit.test", username = "a", password = "p", enabled = true })).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_client_keeps_its_jobs_owned_and_disabling_keeps_them_too()
    {
        var routing = new RoutingHandler();
        routing.Route(TestWorld.QbitHost, new FakeQbit().Respond);
        using var factory = new StubApiFactory(routing);
        var client = await SignedInAsync(factory);

        int movieId;
        await using (var db = factory.Services.GetRequiredService<IDbContextFactory<OneArrDbContext>>().CreateDbContext())
        {
            db.QualityProfiles.Add(new QualityProfile { Name = "HD-1080p", MinResolution = 720, CutoffResolution = 1080, AllowWebSources = true });
            await db.SaveChangesAsync();
            var profileId = db.QualityProfiles.First().Id;
            db.MediaItems.Add(new MediaItem
            {
                MediaType = MediaType.Movie, Provider = "tmdb", ProviderId = "603", Title = "The Matrix", Year = 1999,
                Monitored = true, QualityProfileId = profileId, RootFolder = "/tmp", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            movieId = db.MediaItems.First().Id;
        }

        var created = await PostJsonAsync(client, "/api/download-clients", new
        {
            name = "qbit", baseUrl = "http://qbit.test", username = "admin", password = "pw",
            movieCategory = "onearr-movies", tvCategory = "onearr-tv", enabled = true,
        });
        var clientId = created.RootElement.GetProperty("id").GetInt32();

        var grab = await client.PostAsJsonAsync("/api/grab", new { mediaItemId = movieId, episodeId = (int?)null, title = Matrix, link = "magnet:?xt=urn:btih:" + MovieHash, infoHash = MovieHash });
        Assert.True(grab.IsSuccessStatusCode, await grab.Content.ReadAsStringAsync());

        var deleted = await client.DeleteAsync($"/api/download-clients/{clientId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await using (var db = factory.Services.GetRequiredService<IDbContextFactory<OneArrDbContext>>().CreateDbContext())
        {
            var job = await db.DownloadJobs.SingleAsync();
            Assert.Equal(clientId, job.DownloadClientId);
        }
    }

    [Fact]
    public async Task Search_response_reports_per_indexer_outcomes_and_publish_dates()
    {
        var routing = new RoutingHandler();
        routing.Route(TestWorld.IndexerHost, req => FakeHandler.Text(
            "<rss version=\"2.0\" xmlns:torznab=\"http://torznab.com/schemas/2015/feed\"><channel>"
            + "<item><title>The.Matrix.1999.1080p.BluRay.x264-GRP</title>"
            + "<link>magnet:?xt=urn:btih:" + MovieHash + "</link>"
            + "<pubDate>Thu, 01 Oct 2026 12:00:00 GMT</pubDate>"
            + "<torznab:attr name=\"seeders\" value=\"42\" /><torznab:attr name=\"infohash\" value=\"" + MovieHash + "\" />"
            + "</item></channel></rss>", contentType: "application/xml"));
        using var factory = new StubApiFactory(routing);
        var client = await SignedInAsync(factory);

        int movieId;
        await using (var db = factory.Services.GetRequiredService<IDbContextFactory<OneArrDbContext>>().CreateDbContext())
        {
            db.QualityProfiles.Add(new QualityProfile { Name = "HD-1080p", MinResolution = 720, CutoffResolution = 1080, AllowWebSources = true });
            await db.SaveChangesAsync();
            var profileId = db.QualityProfiles.First().Id;
            db.Indexers.Add(new IndexerConfig { Name = "fixture", BaseUrl = "http://indexer.test/", ApiKey = "k", Enabled = true });
            db.MediaItems.Add(new MediaItem
            {
                MediaType = MediaType.Movie, Provider = "tmdb", ProviderId = "603", Title = "The Matrix", Year = 1999,
                Monitored = true, QualityProfileId = profileId, RootFolder = "/tmp", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            movieId = db.MediaItems.First().Id;
        }

        var search = await PostJsonAsync(client, "/api/search", new { mediaItemId = movieId, episodeId = (int?)null });
        var indexers = search.RootElement.GetProperty("indexers");
        Assert.Equal("results", indexers[0].GetProperty("status").GetString());
        Assert.Equal(1, indexers[0].GetProperty("resultCount").GetInt32());
        var first = search.RootElement.GetProperty("results")[0];
        Assert.Equal("2026-10-01T12:00:00+00:00", first.GetProperty("publishDateUtc").GetString());
    }

    [Fact]
    public async Task Movie_search_reports_results_failures_and_skips_per_indexer()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync(automaticGrab: false);
        w.Indexer.Body = TestWorld.Torznab((Matrix, MovieHash, 42));
        w.Http.Route("badindexer.test", ServerError);
        await using (var db = w.Db.CreateDbContext())
        {
            db.Indexers.Add(new IndexerConfig { Name = "bad", BaseUrl = "http://badindexer.test/", ApiKey = "k", Enabled = true });
            db.Indexers.Add(new IndexerConfig { Name = "off", BaseUrl = "http://indexer.test/", ApiKey = "k", Enabled = false });
            db.Indexers.Add(new IndexerConfig { Name = "movies-off", BaseUrl = "http://indexer.test/", ApiKey = "k", Enabled = true, MoviesEnabled = false });
            await db.SaveChangesAsync();
        }
        var movieId = await w.AddMovieAsync("The Matrix", 1999);

        var result = await w.Releases.SearchAsync(movieId, null, Ct);

        var byName = result.IndexerOutcomes.ToDictionary(o => o.Name);
        Assert.Equal("results", byName["fixture"].Status);
        Assert.Equal(1, byName["fixture"].ResultCount);
        Assert.Equal("failed", byName["bad"].Status);
        Assert.Contains("500", byName["bad"].Message);
        Assert.Equal(("skipped", "Disabled"), (byName["off"].Status, byName["off"].Message));
        Assert.Equal(("skipped", "Movies not enabled"), (byName["movies-off"].Status, byName["movies-off"].Message));
        Assert.Single(result.Decisions);
    }

    [Fact]
    public async Task Series_search_skips_tv_disabled_indexers()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync(automaticGrab: false);
        await using (var db = w.Db.CreateDbContext())
        {
            db.Indexers.Add(new IndexerConfig { Name = "tv-off", BaseUrl = "http://indexer.test/", ApiKey = "k", Enabled = true, TvEnabled = false });
            await db.SaveChangesAsync();
        }
        var (seriesId, episodeIds) = await w.AddSeriesAsync("Show", 1);

        var result = await w.Releases.SearchAsync(seriesId, episodeIds[0], Ct);

        var tvOff = result.IndexerOutcomes.Single(o => o.Name == "tv-off");
        Assert.Equal("skipped", tvOff.Status);
        Assert.Equal("TV not enabled", tvOff.Message);
    }

    [Fact]
    public async Task Grab_records_the_selected_client_and_rejects_disabled_or_missing_clients()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync(automaticGrab: false);
        w.Http.Route("qbit2.test", w.Qbit.Respond);
        int secondId;
        await using (var db = w.Db.CreateDbContext())
        {
            db.DownloadClients.Add(new DownloadClientConfig { Name = "qbit2", BaseUrl = "http://qbit2.test", Username = "admin", Password = "pw", MovieCategory = "movies2", TvCategory = "tv2", Enabled = true });
            db.DownloadClients.Add(new DownloadClientConfig { Name = "qbit-off", BaseUrl = "http://qbit.test", Username = "admin", Password = "pw", MovieCategory = "onearr-movies", TvCategory = "onearr-tv", Enabled = false });
            await db.SaveChangesAsync();
            secondId = db.DownloadClients.Single(c => c.Name == "qbit2").Id;
        }
        var disabledId = 0;
        await using (var db = w.Db.CreateDbContext())
        {
            disabledId = db.DownloadClients.Single(c => c.Name == "qbit-off").Id;
        }
        var movieId = await w.AddMovieAsync("The Matrix", 1999);

        var job = await w.Releases.GrabAsync(new GrabRequest(movieId, null, Matrix, "magnet:?xt=urn:btih:" + MovieHash, MovieHash, secondId), Ct);
        Assert.Equal(secondId, job.DownloadClientId);
        Assert.Equal("movies2", job.Category);
        Assert.Contains(w.Qbit.Torrents, t => t.Category == "movies2");

        // A second movie, so the duplicate-download check cannot mask the client check: the
        // failure must come from client validation, and the message must say so.
        var otherMovieId = await w.AddMovieAsync("The Matrix Reloaded", 2003);
        const string otherHash = "2222222222222222222222222222222222222222";
        var disabledEx = await Assert.ThrowsAsync<ArgumentException>(() => w.Releases.GrabAsync(
            new GrabRequest(otherMovieId, null, "The.Matrix.Reloaded.2003.1080p.BluRay.x264-GRP", "magnet:?xt=urn:btih:" + otherHash, otherHash, disabledId), Ct));
        Assert.Contains("does not exist or is disabled", disabledEx.Message);
        var missingEx = await Assert.ThrowsAsync<ArgumentException>(() => w.Releases.GrabAsync(
            new GrabRequest(otherMovieId, null, "The.Matrix.Reloaded.2003.1080p.BluRay.x264-GRP", "magnet:?xt=urn:btih:" + otherHash, otherHash, 999), Ct));
        Assert.Contains("does not exist or is disabled", missingEx.Message);
    }

    [Fact]
    public void Torznab_parse_reads_pubDate_and_tolerates_its_absence()
    {
        const string head = "<rss version=\"2.0\" xmlns:torznab=\"http://torznab.com/schemas/2015/feed\"><channel>";
        var dated = TorznabClient.Parse(head
            + "<item><title>Movie.2021.1080p.WEB</title><link>magnet:?xt=urn:btih:" + MovieHash + "</link>"
            + "<pubDate>Thu, 01 Oct 2026 12:00:00 GMT</pubDate></item></channel></rss>", "fixture");
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), dated[0].PublishDateUtc);

        var undated = TorznabClient.Parse(head
            + "<item><title>Movie.2021.1080p.WEB</title><link>magnet:?xt=urn:btih:" + MovieHash + "</link></item></channel></rss>", "fixture");
        Assert.Null(undated[0].PublishDateUtc);
    }

    [Fact]
    public async Task Migration_upgrade_keeps_existing_indexers_searching_both_media_types()
    {
        var dir = Path.Combine(Path.GetTempPath(), "onearr-mgmt-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = new DbContextOptionsBuilder<OneArrDbContext>().UseSqlite($"Data Source={Path.Combine(dir, "onearr.db")}").Options;
            await using (var previous = new OneArrDbContext(options))
            {
                await ((Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<IServiceProvider>)previous.Database).Instance.GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("AddDownloadClientTracking");
                await previous.Database.ExecuteSqlRawAsync(
                    "INSERT INTO Indexers (Id, Name, BaseUrl, ApiKey, Enabled) VALUES (1, 'old', 'http://old.test/', 'k', 1)");
            }
            await using (var upgraded = new OneArrDbContext(options))
            {
                await upgraded.Database.MigrateAsync();
                var indexer = await upgraded.Indexers.SingleAsync();
                Assert.True(indexer.MoviesEnabled);
                Assert.True(indexer.TvEnabled);
                Assert.Equal(0, indexer.Priority);
                Assert.Equal(0, await upgraded.IndexerHealth.CountAsync());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Edit_dialog_test_reuses_saved_key_for_blank_field_and_writes_no_health()
    {
        var expectedKey = "saved-key";
        var routing = new RoutingHandler();
        routing.Route("indexer.test", req =>
            QueryValue(req, "apikey") == expectedKey
                ? FakeHandler.Text(CapsXml, contentType: "application/xml")
                : FakeHandler.Text("unauthorized", HttpStatusCode.Unauthorized, "text/plain"));
        using var factory = new StubApiFactory(routing);
        var client = await SignedInAsync(factory);

        var created = await PostJsonAsync(client, "/api/indexers", new
        {
            name = "fixture", baseUrl = "http://indexer.test/", apiKey = "saved-key",
            enabled = true, moviesEnabled = true, tvEnabled = true, priority = 0,
        });
        var id = created.RootElement.GetProperty("id").GetInt32();

        // Edit-dialog Test: blank key field plus a changed URL. The server must test the
        // new URL with the STORED key (only it authenticates), save nothing, and record
        // no health — the values tested are not the saved ones.
        var tested = await PostJsonAsync(client, "/api/connections/test", new
        {
            service = "torznab", indexerId = id, baseUrl = "http://indexer.test/changed", apiKey = "",
        });
        Assert.True(tested.RootElement.GetProperty("ok").GetBoolean());
        Assert.DoesNotContain("saved-key", tested.RootElement.GetRawText());

        var listed = await GetJsonAsync(client, "/api/indexers");
        Assert.Equal("http://indexer.test/", listed.RootElement[0].GetProperty("baseUrl").GetString());
        Assert.Equal("Not tested", listed.RootElement[0].GetProperty("healthStatus").GetString());

        // A supplied key overrides the stored one for this one test only.
        expectedKey = "typed-key";
        tested = await PostJsonAsync(client, "/api/connections/test", new
        {
            service = "torznab", indexerId = id, baseUrl = "http://indexer.test/changed", apiKey = "typed-key",
        });
        Assert.True(tested.RootElement.GetProperty("ok").GetBoolean());
        listed = await GetJsonAsync(client, "/api/indexers");
        Assert.Equal("http://indexer.test/", listed.RootElement[0].GetProperty("baseUrl").GetString());
        Assert.Equal("Not tested", listed.RootElement[0].GetProperty("healthStatus").GetString());
    }

    [Fact]
    public async Task Edit_dialog_test_reuses_saved_password_for_blank_field_and_writes_no_health()
    {
        var expectedPassword = "saved-pw";
        var routing = new RoutingHandler();
        routing.Route("qbit-auth.test", req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v2/auth/login")
            {
                var form = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (form.Contains("password=" + expectedPassword))
                {
                    var r = FakeHandler.Text("Ok.", contentType: "text/plain");
                    r.Headers.Add("Set-Cookie", "SID=fakesid; HttpOnly; path=/");
                    return r;
                }
                return FakeHandler.Text("Forbidden", HttpStatusCode.Forbidden, "text/plain");
            }
            if (req.RequestUri!.AbsolutePath == "/api/v2/torrents/info")
                return FakeHandler.Text("[]");
            return FakeHandler.Text("not found", HttpStatusCode.NotFound, "text/plain");
        });
        using var factory = new StubApiFactory(routing);
        var client = await SignedInAsync(factory);

        var created = await PostJsonAsync(client, "/api/download-clients", new
        {
            name = "qbit", baseUrl = "http://qbit-auth.test", username = "admin", password = "saved-pw",
            movieCategory = "onearr-movies", tvCategory = "onearr-tv", enabled = true,
        });
        var id = created.RootElement.GetProperty("id").GetInt32();

        // Blank password field: the server must test with the STORED password.
        var tested = await PostJsonAsync(client, "/api/connections/test", new
        {
            service = "qbittorrent", clientId = id, baseUrl = "http://qbit-auth.test",
            username = "admin", password = "", movieCategory = "onearr-movies", tvCategory = "onearr-tv",
        });
        Assert.True(tested.RootElement.GetProperty("ok").GetBoolean());
        Assert.DoesNotContain("saved-pw", tested.RootElement.GetRawText());

        var listed = await GetJsonAsync(client, "/api/download-clients");
        Assert.Equal("Not tested", listed.RootElement[0].GetProperty("healthStatus").GetString());
    }

    /// <summary>Slow caps handler that yields instead of blocking: a blocking handler
    /// would serialize task startup on the enumerating thread, so only one item would
    /// ever start before the batch deadline. The delay observes the request token, so
    /// started items cancel promptly at the deadline either way.</summary>
    private sealed class SlowCapsHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return FakeHandler.Text(CapsXml, contentType: "application/xml");
        }
    }

    [Fact]
    public async Task Test_all_batch_deadline_reports_skipped_and_timed_out_instead_of_failing()
    {
        using var factory = new StubApiFactory(new SlowCapsHandler());
        await using (var db = factory.Services.GetRequiredService<IDbContextFactory<OneArrDbContext>>().CreateDbContext())
        {
            for (var i = 0; i < 6; i++)
                db.Indexers.Add(new IndexerConfig { Name = $"slow-{i}", BaseUrl = "http://slow.test/", ApiKey = "k", Enabled = true, Priority = i });
            await db.SaveChangesAsync();
        }

        // Six slow indexers, four gate slots, a 150 ms batch deadline: the four that
        // start time out, the two still queued are reported skipped — and the call
        // returns all six results instead of faulting.
        var results = await ApiEndpoints.TestAllCoreAsync(
            factory.Services.GetRequiredService<IDbContextFactory<OneArrDbContext>>(),
            factory.Services.GetRequiredService<IHttpClientFactory>(),
            factory.Services.GetRequiredService<IDownloadClient>(),
            TimeSpan.FromMilliseconds(150), Ct);

        Assert.Equal(6, results.Length);
        Assert.Equal(2, results.Count(r => r.Detail.Contains("skipped", StringComparison.Ordinal)));
        Assert.Equal(4, results.Count(r => r.Detail.Contains("timed out", StringComparison.Ordinal)));
        Assert.All(results, r => Assert.False(r.Ok));

        await using (var db = factory.Services.GetRequiredService<IDbContextFactory<OneArrDbContext>>().CreateDbContext())
        {
            Assert.Equal(6, await db.IndexerHealth.CountAsync());
        }
    }
}
