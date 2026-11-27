using System.Net;
using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Persistence;
using OneArr.Infrastructure.Providers;
using OneArr.Infrastructure.Services;
using Xunit;

namespace OneArr.Tests;

/// <summary>Exact air times: TVmaze parsing and lookup, how refresh stores them, and how they decide "aired".</summary>
public class AirTimeTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // Shape of https://api.tvmaze.com/shows/112/episodes, checked against the live API on 2026-11-26.
    private const string EpisodesJson = """
        [
          {"id":1,"season":28,"number":7,"airdate":"2026-11-25","airtime":"22:00","airstamp":"2026-11-26T03:00:00+00:00","type":"regular"},
          {"id":2,"season":28,"number":8,"airdate":"2026-12-02","airtime":"22:00","airstamp":"2026-12-03T03:00:00+00:00","type":"regular"},
          {"id":3,"season":28,"number":null,"airdate":"2026-12-09","airtime":"22:00","airstamp":"2026-12-10T03:00:00+00:00","type":"insignificant_special"},
          {"id":4,"season":28,"number":9,"airdate":null,"airtime":"","airstamp":null,"type":"regular"}
        ]
        """;

    [Fact]
    public void Parses_numbered_episodes_with_an_airstamp_only()
    {
        var times = TvmazeAirTimeProvider.ParseEpisodes(EpisodesJson);

        Assert.Equal(2, times.Count);
        Assert.Equal(new EpisodeAirTime(28, 7, new DateTimeOffset(2026, 11, 26, 3, 0, 0, TimeSpan.Zero)), times[0]);
    }

    [Theory]
    [InlineData("/shows/112", 112)]
    [InlineData("https://api.tvmaze.com/shows/112", 112)]
    [InlineData("https://api.tvmaze.com/shows/112/episodes", null)]
    [InlineData("/people/5", null)]
    public void Reads_the_show_id_from_the_lookup_redirect(string location, int? expected) =>
        Assert.Equal(expected, TvmazeAirTimeProvider.ShowIdFromPath(new Uri(location, UriKind.RelativeOrAbsolute)));

    [Fact]
    public async Task Looks_up_by_tvdb_then_imdb_without_following_the_redirect()
    {
        var http = new RoutingHandler();
        http.Route("api.tvmaze.com", req =>
        {
            var q = req.RequestUri!.Query;
            if (q.Contains("thetvdb=")) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var r = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
            r.Headers.Location = new Uri("https://api.tvmaze.com/shows/112");
            return r;
        });
        var provider = new TvmazeAirTimeProvider(new FakeFactory(http));

        var id = await provider.FindShowAsync(75897, "tt0121955", Ct);

        Assert.Equal(112, id);
        Assert.Equal(2, http.Requests.Count);
        Assert.Contains("imdb=tt0121955", http.Requests[1].RequestUri!.Query);
    }

    private sealed class AirTimesStub : IAirTimeProvider
    {
        public List<EpisodeAirTime> Times { get; } = new();
        public bool Fail { get; set; }
        public int Lookups { get; private set; }
        public Task<int?> FindShowAsync(int? tvdbId, string? imdbId, CancellationToken ct)
        {
            Lookups++;
            return Fail ? throw new ExternalServiceException("TVmaze returned HTTP 503") : Task.FromResult<int?>(tvdbId is null ? null : 112);
        }
        public Task<IReadOnlyList<EpisodeAirTime>> GetAirTimesAsync(int showId, CancellationToken ct) =>
            Fail ? throw new ExternalServiceException("TVmaze returned HTTP 503") : Task.FromResult<IReadOnlyList<EpisodeAirTime>>(Times);
    }

    private sealed class SeriesMeta : IMetadataProvider
    {
        public List<EpisodeInfo> Episodes { get; } = new();
        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(MediaType type, string query, CancellationToken ct) => Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);
        public Task<MetadataDetails> GetDetailsAsync(MediaType type, string providerId, CancellationToken ct) =>
            Task.FromResult(new MetadataDetails(providerId, "South Park", 1997, null, null, "tt0121955", 75897, 1));
        public Task<IReadOnlyList<SeasonInfo>> GetSeasonsAsync(string providerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeasonInfo>>([new SeasonInfo(28, Episodes)]);
    }

    private static async Task<(IDbContextFactory<OneArrDbContext> Db, IDisposable Keep, int SeriesId)> SeedSeriesAsync()
    {
        var (factory, keep) = TestDb.Create();
        await using var db = factory.CreateDbContext();
        var profile = new QualityProfile { Name = "HD" };
        db.QualityProfiles.Add(profile);
        await db.SaveChangesAsync();
        var series = new MediaItem
        {
            MediaType = MediaType.Series, Provider = "tmdb", ProviderId = "2190", Title = "South Park", Year = 1997, Monitored = true,
            QualityProfileId = profile.Id, RootFolder = OperatingSystem.IsWindows() ? @"D:\TV" : "/tv", CreatedAt = DateTimeOffset.UtcNow,
        };
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        return (factory, keep, series.Id);
    }

    [Fact]
    public async Task Refresh_stores_air_times_that_agree_with_the_tmdb_date_and_remembers_the_show()
    {
        var (factory, keep, seriesId) = await SeedSeriesAsync();
        using var _ = keep;
        var meta = new SeriesMeta();
        meta.Episodes.Add(new EpisodeInfo(7, "e7", "Seven", new DateOnly(2026, 11, 25)));
        meta.Episodes.Add(new EpisodeInfo(8, "e8", "Eight", new DateOnly(2026, 12, 2)));
        var air = new AirTimesStub();
        air.Times.Add(new EpisodeAirTime(28, 7, new DateTimeOffset(2026, 11, 26, 3, 0, 0, TimeSpan.Zero)));
        // Numbering mismatch: the other source's episode 8 is months away from TMDb's date, so it is not trusted.
        air.Times.Add(new EpisodeAirTime(28, 8, new DateTimeOffset(2027, 3, 1, 3, 0, 0, TimeSpan.Zero)));

        await new MediaService(factory, meta, TimeProvider.System, air).RefreshMetadataAsync(seriesId, Ct);
        await new MediaService(factory, meta, TimeProvider.System, air).RefreshMetadataAsync(seriesId, Ct);

        await using var db = factory.CreateDbContext();
        var eps = await db.Episodes.OrderBy(e => e.EpisodeNumber).ToListAsync();
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 3, 0, 0, TimeSpan.Zero), eps[0].AirDateUtc);
        Assert.Null(eps[1].AirDateUtc);
        Assert.Equal(112, (await db.MediaItems.SingleAsync()).TvmazeId);
        Assert.Equal(1, air.Lookups);
    }

    [Fact]
    public async Task Refresh_still_succeeds_when_air_times_are_unavailable()
    {
        var (factory, keep, seriesId) = await SeedSeriesAsync();
        using var _ = keep;
        var meta = new SeriesMeta();
        meta.Episodes.Add(new EpisodeInfo(7, "e7", "Seven", new DateOnly(2026, 11, 25)));

        var result = await new MediaService(factory, meta, TimeProvider.System, new AirTimesStub { Fail = true }).RefreshMetadataAsync(seriesId, Ct);

        Assert.Equal(1, result.EpisodesAdded);
        await using var db = factory.CreateDbContext();
        Assert.Null((await db.Episodes.SingleAsync()).AirDateUtc);
    }

    [Fact]
    public async Task A_kept_air_time_is_dropped_when_the_date_moves_and_no_new_time_is_available()
    {
        var (factory, keep, seriesId) = await SeedSeriesAsync();
        using var _ = keep;
        var meta = new SeriesMeta();
        meta.Episodes.Add(new EpisodeInfo(7, "e7", "Seven", new DateOnly(2026, 11, 25)));
        var air = new AirTimesStub();
        air.Times.Add(new EpisodeAirTime(28, 7, new DateTimeOffset(2026, 11, 26, 3, 0, 0, TimeSpan.Zero)));
        await new MediaService(factory, meta, TimeProvider.System, air).RefreshMetadataAsync(seriesId, Ct);

        meta.Episodes[0] = new EpisodeInfo(7, "e7", "Seven", new DateOnly(2026, 12, 9));
        air.Fail = true;
        await new MediaService(factory, meta, TimeProvider.System, air).RefreshMetadataAsync(seriesId, Ct);

        await using var db = factory.CreateDbContext();
        Assert.Null((await db.Episodes.SingleAsync()).AirDateUtc);
    }

    [Theory]
    [InlineData("2026-11-26T02:59:00Z", false)]
    [InlineData("2026-11-26T03:00:00Z", true)]
    public void Exact_time_decides_aired_when_known(string now, bool aired) =>
        Assert.Equal(aired, Episode.HasAired(new DateOnly(2026, 11, 25), new DateTimeOffset(2026, 11, 26, 3, 0, 0, TimeSpan.Zero), DateTimeOffset.Parse(now)));

    [Fact]
    public void Date_decides_aired_without_a_time() =>
        Assert.True(Episode.HasAired(new DateOnly(2026, 11, 25), null, new DateTimeOffset(2026, 11, 25, 0, 1, 0, TimeSpan.Zero)));

    [Fact]
    public async Task Wanted_search_waits_for_the_exact_air_time()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        var (_, episodes) = await w.AddSeriesAsync("Evening Show", episodes: 1);
        var now = w.Time.GetUtcNow();
        await using (var db = w.Db.CreateDbContext())
        {
            var ep = await db.Episodes.SingleAsync(e => e.Id == episodes[0]);
            // Dated today, but it airs this evening.
            ep.AirDate = DateOnly.FromDateTime(now.UtcDateTime);
            ep.AirDateUtc = now.AddHours(10);
            await db.SaveChangesAsync();
        }
        await w.ReconcileLibraryAsync();

        Assert.Equal(0, (await w.Search.RunOnceAsync(Ct)).Considered);

        w.Time.Advance(TimeSpan.FromHours(10));
        await w.ReconcileLibraryAsync();
        Assert.Equal(1, (await w.Search.RunOnceAsync(Ct)).Searched);
    }

    [Fact]
    public async Task Episode_out_in_utc_before_its_local_date_is_searched()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        var (_, episodes) = await w.AddSeriesAsync("Tokyo Show", episodes: 1);
        var now = w.Time.GetUtcNow();
        await using (var db = w.Db.CreateDbContext())
        {
            var ep = await db.Episodes.SingleAsync(e => e.Id == episodes[0]);
            // Dated tomorrow in Japan, already aired in UTC.
            ep.AirDate = DateOnly.FromDateTime(now.UtcDateTime).AddDays(1);
            ep.AirDateUtc = now.AddHours(-1);
            await db.SaveChangesAsync();
        }
        await w.ReconcileLibraryAsync();

        Assert.Equal(1, (await w.Search.RunOnceAsync(Ct)).Searched);
    }
}
