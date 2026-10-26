using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Services;
using Xunit;

namespace OneArr.Tests;

/// <summary>
/// Series availability and per-type defaults. Availability is episode-based: monitored, aired episodes
/// only. A series with one downloaded episode is never Complete because its series-level HasFile flag
/// is true, and unaired or unmonitored episodes never count as missing.
/// </summary>
public class AvailabilityTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class StubMetadata : IMetadataProvider
    {
        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(MediaType type, string query, CancellationToken ct) => throw new NotImplementedException();
        public Task<MetadataDetails> GetDetailsAsync(MediaType type, string providerId, CancellationToken ct) =>
            Task.FromResult(new MetadataDetails(providerId, "Test Title", 2020, null, null, null, null, 0));
        public Task<IReadOnlyList<SeasonInfo>> GetSeasonsAsync(string providerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeasonInfo>>([]);
    }

    private static async Task SetEpisodeAsync(TestWorld w, int episodeId, Action<Episode> edit)
    {
        await using var db = w.Db.CreateDbContext();
        edit(await db.Episodes.SingleAsync(e => e.Id == episodeId));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CompletePartialMissingFromEpisodeFiles()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        var (completeId, completeEps) = await w.AddSeriesAsync("Complete", 2);
        var (partialId, partialEps) = await w.AddSeriesAsync("Partial", 2);
        var (missingId, _) = await w.AddSeriesAsync("Missing", 2);
        await SetEpisodeAsync(w, completeEps[0], e => e.HasFile = true);
        await SetEpisodeAsync(w, completeEps[1], e => e.HasFile = true);
        await SetEpisodeAsync(w, partialEps[0], e => e.HasFile = true);

        var media = new MediaService(w.Db, new StubMetadata(), w.Time);
        var availability = await media.SeriesAvailabilityAsync(Ct);

        Assert.Equal(("Complete", 2, 2), (availability[completeId].Status, availability[completeId].AvailableEpisodes, availability[completeId].ApplicableEpisodes));
        Assert.Equal(("Partial", 1, 2), (availability[partialId].Status, availability[partialId].AvailableEpisodes, availability[partialId].ApplicableEpisodes));
        Assert.Equal(("Missing", 0, 2), (availability[missingId].Status, availability[missingId].AvailableEpisodes, availability[missingId].ApplicableEpisodes));
    }

    [Fact]
    public async Task SeriesWithNoEpisodesIsUnknownNotMissing()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        await using (var db = w.Db.CreateDbContext())
        {
            db.MediaItems.Add(new MediaItem { MediaType = MediaType.Series, Provider = "tmdb", ProviderId = "999", Title = "Empty", Monitored = true, QualityProfileId = w.ProfileId, RootFolder = "/tv" });
            await db.SaveChangesAsync();
        }

        var media = new MediaService(w.Db, new StubMetadata(), w.Time);
        var availability = await media.SeriesAvailabilityAsync(Ct);

        // No episode rows at all: the grouped query has nothing for this series, and the list
        // endpoint reports it as Unknown rather than inventing a Missing status.
        await using var db2 = w.Db.CreateDbContext();
        var emptyId = (await db2.MediaItems.SingleAsync(m => m.Title == "Empty")).Id;
        Assert.False(availability.ContainsKey(emptyId));
        Assert.Equal("Unknown", MediaDefaults.ClassifySeries(0, 0));
    }

    [Fact]
    public async Task UnairedAndUnmonitoredEpisodesNeverCountAsMissing()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        // TestWorld time is 2026-10-01, so a 2027 air date is unaired.
        var (seriesId, episodeIds) = await w.AddSeriesAsync("Mixed", 3);
        await SetEpisodeAsync(w, episodeIds[0], e => e.HasFile = true);
        await SetEpisodeAsync(w, episodeIds[1], e => e.AirDate = new DateOnly(2027, 1, 1));
        await SetEpisodeAsync(w, episodeIds[2], e => e.Monitored = false);

        var media = new MediaService(w.Db, new StubMetadata(), w.Time);
        var availability = await media.SeriesAvailabilityAsync(Ct);

        // Only episode 1 is monitored and aired, and it has a file: Complete 1/1, not Partial 1/3.
        Assert.Equal(("Complete", 1, 1), (availability[seriesId].Status, availability[seriesId].AvailableEpisodes, availability[seriesId].ApplicableEpisodes));
    }

    [Fact]
    public async Task EpisodesWithUnknownAirDateMakeTheSeriesUnknown()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        var (seriesId, episodeIds) = await w.AddSeriesAsync("Dateless", 2);
        await SetEpisodeAsync(w, episodeIds[0], e => e.AirDate = null);
        await SetEpisodeAsync(w, episodeIds[1], e => e.AirDate = null);

        var media = new MediaService(w.Db, new StubMetadata(), w.Time);
        var availability = await media.SeriesAvailabilityAsync(Ct);

        Assert.Equal(("Unknown", 0, 0), (availability[seriesId].Status, availability[seriesId].AvailableEpisodes, availability[seriesId].ApplicableEpisodes));
    }

    [Fact]
    public void DefaultRootsNeverCrossMediaTypesAndNeverFallBackToASharedRoot()
    {
        Assert.Equal("/movies", MediaDefaults.ResolveRoot(MediaType.Movie, "/movies", "/tv"));
        Assert.Equal("/tv", MediaDefaults.ResolveRoot(MediaType.Series, "/movies", "/tv"));
        // No per-type default means empty — never the other type's folder, and never a shared root.
        // A stored media root of D:\Media\Movies must not silently become a series root.
        Assert.Equal("", MediaDefaults.ResolveRoot(MediaType.Movie, null, "/tv"));
        Assert.Equal("", MediaDefaults.ResolveRoot(MediaType.Series, "/movies", null));
        Assert.Equal("", MediaDefaults.ResolveRoot(MediaType.Series, null, null));
        Assert.Equal("/movies", MediaDefaults.ResolveRoot(MediaType.Movie, "  /movies  ", null));
    }

    [Fact]
    public void ClassifySeriesBoundaries()
    {
        Assert.Equal("Unknown", MediaDefaults.ClassifySeries(0, 0));
        Assert.Equal("Complete", MediaDefaults.ClassifySeries(180, 180));
        Assert.Equal("Missing", MediaDefaults.ClassifySeries(0, 40));
        Assert.Equal("Partial", MediaDefaults.ClassifySeries(126, 180));
    }

    [Fact]
    public async Task ReAddingAnExistingTitleNeverOverwritesItsRootOrProfile()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        var media = new MediaService(w.Db, new StubMetadata(), w.Time);
        var first = await media.AddAsync(new AddMediaRequest(MediaType.Movie, "4242", w.ProfileId, "/original", true, null, true), Ct);
        Assert.True(first.Created);

        var second = await media.AddAsync(new AddMediaRequest(MediaType.Movie, "4242", w.ProfileId, "/different", false, null, true), Ct);

        Assert.False(second.Created);
        await using var db = w.Db.CreateDbContext();
        var item = await db.MediaItems.SingleAsync(m => m.Id == first.Item.Id);
        Assert.Equal("/original", item.RootFolder);
        Assert.True(item.Monitored);
    }
}

/// <summary>Per-type default settings over the real host: persistence, independence, validation, clearing.</summary>
[Collection(ApiHostCollection.Name)]
public class SettingsDefaultsApiTests
{
    private const string Password = "correct horse battery";

    private static async Task<HttpClient> SignedInAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        var setup = await client.PostAsJsonAsync("/api/auth/setup", new { password = Password });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var token = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> GetSettingsAsync(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>("/api/settings");

    [Fact]
    public async Task PerTypeDefaultsStartEmptyAndPersistIndependently()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);
        var before = await GetSettingsAsync(client);
        Assert.True(before.GetProperty("defaultMovieRoot").ValueKind == JsonValueKind.Null);
        Assert.True(before.GetProperty("defaultSeriesRoot").ValueKind == JsonValueKind.Null);
        Assert.True(before.GetProperty("defaultMovieProfileId").ValueKind == JsonValueKind.Null);
        Assert.True(before.GetProperty("defaultSeriesProfileId").ValueKind == JsonValueKind.Null);

        var movieRoot = Path.Combine(Path.GetTempPath(), "onearr-movies");
        var seriesRoot = Path.Combine(Path.GetTempPath(), "onearr-tv");
        var put = await client.PutAsJsonAsync("/api/settings", new { defaultMovieRoot = movieRoot, defaultSeriesRoot = seriesRoot });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var after = await GetSettingsAsync(client);
        Assert.Equal(movieRoot, after.GetProperty("defaultMovieRoot").GetString());
        Assert.Equal(seriesRoot, after.GetProperty("defaultSeriesRoot").GetString());

        // Changing only the movie default leaves the series default untouched.
        var otherMovieRoot = Path.Combine(Path.GetTempPath(), "onearr-movies-2");
        await client.PutAsJsonAsync("/api/settings", new { defaultMovieRoot = otherMovieRoot });
        var later = await GetSettingsAsync(client);
        Assert.Equal(otherMovieRoot, later.GetProperty("defaultMovieRoot").GetString());
        Assert.Equal(seriesRoot, later.GetProperty("defaultSeriesRoot").GetString());
    }

    [Fact]
    public async Task EmptyStringClearsARootDefaultAndRelativeRootsAreRejected()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);
        var root = Path.Combine(Path.GetTempPath(), "onearr-movies");
        await client.PutAsJsonAsync("/api/settings", new { defaultMovieRoot = root });
        Assert.Equal(root, (await GetSettingsAsync(client)).GetProperty("defaultMovieRoot").GetString());

        var cleared = await client.PutAsJsonAsync("/api/settings", new { defaultMovieRoot = "" });
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.True((await GetSettingsAsync(client)).GetProperty("defaultMovieRoot").ValueKind == JsonValueKind.Null);

        var relative = await client.PutAsJsonAsync("/api/settings", new { defaultSeriesRoot = "relative/tv" });
        Assert.Equal(HttpStatusCode.BadRequest, relative.StatusCode);
    }

    [Fact]
    public async Task FailedPutWritesNothingIncludingGrabbing()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);
        var torrentRoot = Path.Combine(Path.GetTempPath(), "onearr-torrents");
        var ok = await client.PutAsJsonAsync("/api/settings", new { torrentRoot, automaticGrabbing = true });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        // A later field fails validation: the whole save is rejected, including the grabbing toggle
        // sent earlier in the same request. A partial save here could silently flip grabbing on.
        var badRoot = await client.PutAsJsonAsync("/api/settings", new { automaticGrabbing = false, defaultSeriesRoot = "relative/tv" });
        Assert.Equal(HttpStatusCode.BadRequest, badRoot.StatusCode);
        var afterBadRoot = await GetSettingsAsync(client);
        Assert.True(afterBadRoot.GetProperty("automaticGrabbing").GetBoolean());
        Assert.Equal(torrentRoot, afterBadRoot.GetProperty("torrentRoot").GetString());
        Assert.True(afterBadRoot.GetProperty("defaultSeriesRoot").ValueKind == JsonValueKind.Null);

        // Same guarantee when the failing field is a profile id, not a path.
        var badProfile = await client.PutAsJsonAsync("/api/settings", new { automaticGrabbing = false, defaultMovieProfileId = 999999 });
        Assert.Equal(HttpStatusCode.BadRequest, badProfile.StatusCode);
        Assert.True((await GetSettingsAsync(client)).GetProperty("automaticGrabbing").GetBoolean());
    }

    [Fact]
    public async Task DefaultProfilesMustExist()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);

        var unknown = await client.PutAsJsonAsync("/api/settings", new { defaultMovieProfileId = 999999 });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var created = await client.PostAsJsonAsync("/api/quality-profiles", new { name = "Test", minResolution = 720, cutoffResolution = 1080, allowWebSources = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var profileId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var put = await client.PutAsJsonAsync("/api/settings", new { defaultMovieProfileId = profileId, defaultSeriesProfileId = profileId });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var settings = await GetSettingsAsync(client);
        Assert.Equal(profileId, settings.GetProperty("defaultMovieProfileId").GetInt32());
        Assert.Equal(profileId, settings.GetProperty("defaultSeriesProfileId").GetInt32());
    }
}
