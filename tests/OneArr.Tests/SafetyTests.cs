using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Files;
using OneArr.Infrastructure.Http;
using OneArr.Infrastructure.Legacy;
using OneArr.Infrastructure.Persistence;
using OneArr.Infrastructure.Providers;
using OneArr.Infrastructure.Security;
using OneArr.Infrastructure.Services;
using Xunit;

namespace OneArr.Tests;

/// <summary>Filesystem tests use a per-test temp tree on Linux paths only (the importer is Linux-targeted).</summary>
public sealed class TempTree : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "onearr-test-" + Guid.NewGuid().ToString("N"));
    public string Torrents => Path.Combine(Root, "torrents");
    public string Media => Path.Combine(Root, "media");

    public TempTree()
    {
        Directory.CreateDirectory(Torrents);
        Directory.CreateDirectory(Media);
    }

    public string WriteFile(string relative, string content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => RemoveLinksAndTree(Root);

    /// <summary>
    /// Deletes a tree without following links. A junction or symlink is removed as a link only; its target (which may be
    /// outside the temp tree, in the junction tests) is never touched.
    /// </summary>
    public static void RemoveLinksAndTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var dir in Directory.GetDirectories(path))
        {
            if (new DirectoryInfo(dir).LinkTarget is not null) Directory.Delete(dir, recursive: false);
            else RemoveLinksAndTree(dir);
        }
        foreach (var file in Directory.GetFiles(path)) new FileInfo(file).Delete();
        Directory.Delete(path, recursive: false);
    }
}

public class CopySpaceGuardTests
{
    [Fact]
    public void UsesBoundedSafetyReserveAndNeverAcceptsTooLittleCapacity()
    {
        Assert.Equal(16L * 1024 * 1024, CopySpaceGuard.RequiredBytes(0));
        Assert.Equal(16L * 1024 * 1024 + 1024, CopySpaceGuard.RequiredBytes(1024));
        Assert.False(CopySpaceGuard.HasEnoughSpace(100, 1024));
        Assert.True(CopySpaceGuard.HasEnoughSpace(CopySpaceGuard.RequiredBytes(1024), 1024));
        Assert.Equal(long.MaxValue, CopySpaceGuard.RequiredBytes(long.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => CopySpaceGuard.RequiredBytes(-1));
    }

    [Fact]
    public void ReportsFreeSpaceOfExistingTempVolumeWithoutCreatingAnything()
    {
        using var tree = new TempTree();
        var destination = Path.Combine(tree.Media, "New Movie", "movie.mkv");
        Assert.Null(CopySpaceGuard.CopyProblem(destination, 1));
        Assert.False(Directory.Exists(Path.GetDirectoryName(destination)));
    }
}

public class SafeImporterTests
{
    private readonly SafeImporter _importer = new(TimeSpan.Zero);

    private static ImportRequest Req(TempTree t, string source, string rel = "The Matrix (1999)", bool dry = false) =>
        new(source, t.Media, rel, t.Torrents, dry, null);

    [Fact]
    public async Task ImportsCompletedFileWithoutDeletingSource()
    {
        using var t = new TempTree();
        var src = t.WriteFile("torrents/movie/The.Matrix.1999.1080p.mkv", "video-bytes");
        var outcome = await _importer.ImportAsync(Req(t, src), CancellationToken.None);
        Assert.True(outcome.Success, outcome.Message);
        Assert.True(File.Exists(src), "seeding source must never be deleted");
        Assert.Equal("video-bytes", File.ReadAllText(outcome.DestinationPath));
    }

    [Fact]
    public async Task SameFileReimportIsIdempotent()
    {
        using var t = new TempTree();
        var src = t.WriteFile("torrents/movie/a.mkv", "same");
        var first = await _importer.ImportAsync(Req(t, src), CancellationToken.None);
        var second = await _importer.ImportAsync(Req(t, src), CancellationToken.None);
        Assert.True(first.Success);
        Assert.Equal(ImportAction.Skipped, second.Action);
        Assert.Equal(first.DestinationPath, second.DestinationPath);
    }

    [Fact]
    public async Task DifferentContentAtDestinationIsNeverOverwritten()
    {
        using var t = new TempTree();
        var src = t.WriteFile("torrents/movie/a.mkv", "new-content");
        var dest = Path.Combine(t.Media, "The Matrix (1999)", "a.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, "user-content");
        var outcome = await _importer.ImportAsync(Req(t, src), CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Equal("user-content", File.ReadAllText(dest));
    }

    [Fact]
    public async Task DryRunChangesNothing()
    {
        using var t = new TempTree();
        var src = t.WriteFile("torrents/movie/a.mkv", "x");
        var outcome = await _importer.ImportAsync(Req(t, src, dry: true), CancellationToken.None);
        Assert.Equal(ImportAction.DryRun, outcome.Action);
        Assert.False(File.Exists(outcome.DestinationPath));
        Assert.Empty(Directory.GetFileSystemEntries(t.Media));
    }

    [Fact]
    public async Task SourceOutsideTorrentRootIsRefused()
    {
        using var t = new TempTree();
        var outside = t.WriteFile("secret/passwd.mkv", "nope");
        var outcome = await _importer.ImportAsync(Req(t, outside), CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Contains("outside", outcome.Message);
        Assert.Empty(Directory.GetFiles(t.Media, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../../escape")]
    [InlineData("/etc")]
    public async Task DestinationTraversalIsRefused(string rel)
    {
        using var t = new TempTree();
        var src = t.WriteFile("torrents/movie/a.mkv", "x");
        var outcome = await _importer.ImportAsync(Req(t, src, rel), CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Empty(Directory.GetFiles(t.Media, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SymlinkSourceIsRefused()
    {
        using var t = new TempTree();
        var real = t.WriteFile("torrents/movie/real.mkv", "x");
        var link = Path.Combine(t.Torrents, "movie", "link.mkv");
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Windows without symlink privilege: this refusal path cannot be exercised here. Not counted as verified.
            return;
        }
        var outcome = await _importer.ImportAsync(Req(t, link), CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Contains("symbolic link", outcome.Message);
    }

    [Fact]
    public void PlanDestinationStaysUnderRoot()
    {
        // Built from the temp dir so the test behaves the same on Linux and Windows (no hard-coded "/data" paths).
        var root = Path.Combine(Path.GetTempPath(), "onearr-media");
        var absoluteElsewhere = Path.Combine(Path.GetPathRoot(root)!, "etc");
        Assert.Throws<UnauthorizedAccessException>(() => SafeImporter.PlanDestination(root, "../x", "a.mkv"));
        Assert.Throws<UnauthorizedAccessException>(() => SafeImporter.PlanDestination(root, absoluteElsewhere, "a.mkv"));
        Assert.Throws<UnauthorizedAccessException>(() => SafeImporter.PlanDestination(root, "ok", "../../a.mkv"));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(root, "Show", "Season 01", "e.mkv")),
            SafeImporter.PlanDestination(root, Path.Combine("Show", "Season 01"), "e.mkv"));
    }
}

public class LegacyMigrationReadOnlyTests
{
    [Fact]
    public async Task RadarrReaderIssuesOnlyGetAndMapsFixtureFields()
    {
        var json = """[{"title":"The Matrix","year":1999,"tmdbId":603,"imdbId":"tt0133093","path":"/movies/The Matrix","monitored":true}]""";
        var handler = new FakeHandler(_ => FakeHandler.Text(json));
        var reader = new LegacyArrReader(new FakeFactory(handler));
        var items = await reader.ReadAsync(new LegacyArrSource("radarr", "http://radarr:7878", "KEY"), CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(603, item.TmdbId);
        Assert.Equal(MediaType.Movie, item.Type);
        Assert.Null(item.Error);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Equal("/api/v3/movie", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("KEY", handler.Requests[0].Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public async Task SonarrSeriesWithoutTmdbIdIsReportedNotGuessed()
    {
        var json = """[{"title":"No Tmdb","tvdbId":1234,"monitored":true},{"title":"Has Tmdb","tmdbId":1399,"tvdbId":2,"monitored":false}]""";
        var reader = new LegacyArrReader(new FakeFactory(new FakeHandler(_ => FakeHandler.Text(json))));
        var items = await reader.ReadAsync(new LegacyArrSource("sonarr", "http://sonarr:8989", "K"), CancellationToken.None);
        Assert.Equal(2, items.Count);
        Assert.NotNull(items[0].Error);
        Assert.Equal(1399, items[1].TmdbId);
        Assert.False(items[1].Monitored);
    }

    [Fact]
    public async Task AuthFailureOnLegacyAppIsExplicit()
    {
        var reader = new LegacyArrReader(new FakeFactory(new FakeHandler(_ => FakeHandler.Text("nope", HttpStatusCode.Unauthorized, "text/plain"))));
        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => reader.ReadAsync(new LegacyArrSource("radarr", "http://radarr:7878", "bad"), CancellationToken.None));
        Assert.Contains("rejected the API key", ex.Message);
    }
}

public class TmdbProviderTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public async Task SearchesAndMapsMovieFixture()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text(Fixture("tmdb_movie_search.json")));
        var tmdb = new TmdbMetadataProvider(new FakeFactory(handler), () => Task.FromResult<string?>("0123456789abcdef"));
        var results = await tmdb.SearchAsync(MediaType.Movie, "matrix", CancellationToken.None);
        var r = Assert.Single(results);
        Assert.Equal("603", r.ProviderId);
        Assert.Equal(1999, r.Year);
        Assert.Contains("api_key=0123456789abcdef", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task BearerTokenKeysUseAuthorizationHeaderNotQuery()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text(Fixture("tmdb_movie_search.json")));
        var jwt = "eyJhbGciOiJIUzI1NiJ9.payload.sig";
        var tmdb = new TmdbMetadataProvider(new FakeFactory(handler), () => Task.FromResult<string?>(jwt));
        await tmdb.SearchAsync(MediaType.Movie, "matrix", CancellationToken.None);
        Assert.DoesNotContain("api_key", handler.Requests[0].RequestUri!.Query);
        Assert.Equal("Bearer " + jwt, handler.Requests[0].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task MissingKeyGivesHelpfulSetupMessage()
    {
        var tmdb = new TmdbMetadataProvider(new FakeFactory(new FakeHandler(_ => throw new InvalidOperationException("no network"))), () => Task.FromResult<string?>(null));
        var ex = await Assert.ThrowsAsync<ProviderNotConfiguredException>(() => tmdb.SearchAsync(MediaType.Movie, "x", CancellationToken.None));
        Assert.Contains("Settings", ex.Message);
    }

    [Fact]
    public async Task NetworkFailureRetriesThenFailsWithoutLeakingKey()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => { calls++; return FakeHandler.Text("boom", HttpStatusCode.InternalServerError, "text/plain"); });
        var tmdb = new TmdbMetadataProvider(new FakeFactory(handler), () => Task.FromResult<string?>("SECRETKEY123"));
        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => tmdb.SearchAsync(MediaType.Movie, "x", CancellationToken.None));
        Assert.Equal(3, calls); // initial + 2 bounded retries
        Assert.DoesNotContain("SECRETKEY123", ex.Message);
    }

    [Fact]
    public async Task SeriesSeasonsFixtureSkipsSpecials()
    {
        var map = new Dictionary<string, string>
        {
            ["tv/1399"] = Fixture("tmdb_tv_show.json"),
            ["tv/1399/season/1"] = Fixture("tmdb_tv_season1.json"),
            ["tv/1399/season/2"] = Fixture("tmdb_tv_season2.json"),
        };
        var handler = new FakeHandler(req =>
        {
            var key = req.RequestUri!.AbsolutePath.Replace("/3/", "");
            return map.TryGetValue(key, out var body) ? FakeHandler.Text(body) : FakeHandler.Text("{}", HttpStatusCode.NotFound);
        });
        var tmdb = new TmdbMetadataProvider(new FakeFactory(handler), () => Task.FromResult<string?>("0123456789abcdef"));
        var seasons = await tmdb.GetSeasonsAsync("1399", CancellationToken.None);
        Assert.Equal(new[] { 1, 2 }, seasons.Select(s => s.SeasonNumber));
        Assert.Equal(2, seasons[0].Episodes.Count);
        Assert.Equal("5001", seasons[0].Episodes[0].ProviderId);
    }
}

public class CatalogAndJobTests
{
    private sealed class StubMetadata : IMetadataProvider
    {
        public int DetailCalls;
        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(MediaType type, string query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);
        public Task<MetadataDetails> GetDetailsAsync(MediaType type, string providerId, CancellationToken ct)
        {
            DetailCalls++;
            return Task.FromResult(new MetadataDetails(providerId, "Test Show", 2011, "o", null, null, null, 1));
        }
        public Task<IReadOnlyList<SeasonInfo>> GetSeasonsAsync(string providerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeasonInfo>>([new SeasonInfo(1, [new EpisodeInfo(1, "5001", "Pilot", new DateOnly(2011, 4, 17)), new EpisodeInfo(2, "5002", "Two", null)])]);
    }

    [Fact]
    public async Task AddingSameTitleTwiceDeduplicatesAndKeepsStableIds()
    {
        var (factory, keep) = TestDb.Create();
        using var _ = keep;
        await using (var db = factory.CreateDbContext())
        {
            db.QualityProfiles.Add(new QualityProfile { Name = "p" });
            await db.SaveChangesAsync();
        }
        var meta = new StubMetadata();
        var media = new MediaService(factory, meta, TimeProvider.System);
        var req = new AddMediaRequest(MediaType.Series, "1399", 1, OperatingSystem.IsWindows() ? @"D:\Media\TV" : "/data/media/tv", true, null, true);

        var first = await media.AddAsync(req, CancellationToken.None);
        var second = await media.AddAsync(req, CancellationToken.None);
        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Item.Id, second.Item.Id);
        Assert.Equal(1, meta.DetailCalls); // second add never hits the network

        await using var check = factory.CreateDbContext();
        Assert.Equal(1, await check.MediaItems.CountAsync());
        Assert.Equal(2, await check.Episodes.CountAsync());
    }

    [Fact]
    public async Task SameProviderIdDifferentTypeDoesNotCollide()
    {
        var (factory, keep) = TestDb.Create();
        using var _ = keep;
        await using (var db = factory.CreateDbContext())
        {
            db.QualityProfiles.Add(new QualityProfile { Name = "p" });
            await db.SaveChangesAsync();
        }
        var media = new MediaService(factory, new StubMetadata(), TimeProvider.System);
        var movie = await media.AddAsync(new AddMediaRequest(MediaType.Movie, "100", 1, OperatingSystem.IsWindows() ? @"D:\Media\Movies" : "/data/media/movies", true, null, true), CancellationToken.None);
        var series = await media.AddAsync(new AddMediaRequest(MediaType.Series, "100", 1, OperatingSystem.IsWindows() ? @"D:\Media\TV" : "/data/media/tv", true, null, true), CancellationToken.None);
        Assert.True(movie.Created);
        Assert.True(series.Created);
        Assert.NotEqual(movie.Item.Id, series.Item.Id);
    }

    [Fact]
    public async Task OverlappingJobRunsAreSkippedAndHistoryRecorded()
    {
        var (factory, keep) = TestDb.Create();
        using var _ = keep;
        var runner = new JobRunner(factory, TimeProvider.System);
        var gate = new TaskCompletionSource();
        var first = runner.TryRunAsync("import", async ct => { await gate.Task; return "done"; }, CancellationToken.None);
        await Task.Delay(50);
        var second = await runner.TryRunAsync("import", _ => Task.FromResult("should not run"), CancellationToken.None);
        gate.SetResult();
        Assert.True(await first);
        Assert.False(second);

        await using var db = factory.CreateDbContext();
        var rows = await db.JobHistories.ToListAsync();
        Assert.Single(rows);
        Assert.True(rows[0].Success);
        Assert.NotNull(rows[0].FinishedAt);
    }

    [Fact]
    public async Task FailedJobIsRecordedNotThrown()
    {
        var (factory, keep) = TestDb.Create();
        using var _ = keep;
        var runner = new JobRunner(factory, TimeProvider.System);
        Assert.True(await runner.TryRunAsync("queue-sync", _ => throw new InvalidOperationException("qb down"), CancellationToken.None));
        await using var db = factory.CreateDbContext();
        var row = await db.JobHistories.SingleAsync();
        Assert.False(row.Success);
        Assert.Contains("qb down", row.Message);
    }

    [Fact]
    public void PasswordHashVerifiesAndRejectsWrongPassword()
    {
        var hash = PasswordHasher.Hash("correct horse battery");
        Assert.True(PasswordHasher.Verify("correct horse battery", hash));
        Assert.False(PasswordHasher.Verify("wrong", hash));
    }

    [Fact]
    public void TokensValidateAndTamperingFails()
    {
        var svc = new TokenService(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        var (token, _) = svc.Issue(now);
        Assert.True(svc.Validate(token, now));
        var tampered = token[..^1] + (token[^1] == 'A' ? "B" : "A");
        Assert.False(svc.Validate(tampered, now));
        Assert.False(svc.Validate(token, now.AddDays(8)));
    }

    [Fact]
    public void RedactionRemovesApiKeys()
    {
        var text = OutboundHttp.Redact("GET https://x/api?t=search&apikey=SECRET&q=a failed");
        Assert.DoesNotContain("SECRET", text);
        Assert.Contains("apikey=***", text);
    }
}
