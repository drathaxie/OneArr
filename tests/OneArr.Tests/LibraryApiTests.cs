using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OneArr.Domain;
using OneArr.Infrastructure.Persistence;
using Xunit;

namespace OneArr.Tests;

/// <summary>
/// Every API test host sets the ONEARR_CONFIG_DIR environment variable, which is process-wide. Tests in this collection run one at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApiHostCollection
{
    public const string Name = "ApiHost";
}

/// <summary>Library endpoints over the real host: path validation, conflicts, mapping rules, sign-in, and plan safety.</summary>
[Collection(ApiHostCollection.Name)]
public class LibraryApiTests
{
    private const string Password = "correct horse battery";

    /// <summary>A folder path that is valid on this OS and a different kind of path that is invalid here.</summary>
    private static readonly string ValidRoot = OperatingSystem.IsWindows() ? @"D:\Media\Movies" : "/media/movies";
    private static readonly string InvalidRoot = OperatingSystem.IsWindows() ? "/data/media/movies" : @"D:\Media\Movies";

    private static async Task<HttpClient> SignedInAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        var setup = await client.PostAsJsonAsync("/api/auth/setup", new { password = Password });
        Assert.True(setup.IsSuccessStatusCode, await setup.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await setup.Content.ReadAsStringAsync());
        var token = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
        Assert.False(string.IsNullOrEmpty(token));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Library_endpoints_require_sign_in()
    {
        using var factory = new ApiFactory();
        var anonymous = factory.CreateClient();

        var status = await anonymous.GetAsync("/api/library/status");
        var preview = await anonymous.PostAsync("/api/library/preview", null);

        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, preview.StatusCode);
    }

    [Fact]
    public async Task A_folder_is_rejected_when_its_path_style_does_not_match_this_os()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);

        var response = await client.PostAsJsonAsync("/api/library/roots", new { type = "movie", path = InvalidRoot, label = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_folder_is_added_once_and_a_duplicate_is_refused()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);

        var first = await client.PostAsJsonAsync("/api/library/roots", new { type = "movie", path = ValidRoot, label = "Main" });
        // Windows paths are case-insensitive, so a different case is the same folder. Linux paths are case-sensitive, so only the exact path is a duplicate.
        var sameFolder = OperatingSystem.IsWindows() ? ValidRoot.ToUpperInvariant() : ValidRoot;
        var duplicate = await client.PostAsJsonAsync("/api/library/roots", new { type = "movie", path = sameFolder, label = (string?)null });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var status = JsonDocument.Parse(await (await client.GetAsync("/api/library/status")).Content.ReadAsStringAsync());
        Assert.Equal(1, status.RootElement.GetProperty("roots").GetArrayLength());
    }

    [Fact]
    public async Task Removing_a_folder_only_removes_the_record()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);
        using var added = JsonDocument.Parse(await (await client.PostAsJsonAsync("/api/library/roots", new { type = "movie", path = ValidRoot, label = (string?)null })).Content.ReadAsStringAsync());
        var id = added.RootElement.GetProperty("id").GetInt32();

        var response = await client.DeleteAsync($"/api/library/roots/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var db = factory.Services.GetRequiredService<IDbContextFactory<OneArrDbContext>>().CreateDbContext();
        Assert.Empty(db.LibraryRoots);
    }

    [Fact]
    public async Task Mappings_must_start_from_a_source_path_and_cannot_repeat_one()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);
        var target = ValidRoot;

        var relative = await client.PutAsJsonAsync("/api/library/mappings", new[] { new { type = "movie", sourcePrefix = "data/media", targetPrefix = target } });
        var repeated = await client.PutAsJsonAsync("/api/library/mappings", new[]
        {
            new { type = "movie", sourcePrefix = "/data/media/movies", targetPrefix = target },
            new { type = "movie", sourcePrefix = "/data/media/movies", targetPrefix = target },
        });
        var valid = await client.PutAsJsonAsync("/api/library/mappings", new[] { new { type = "movie", sourcePrefix = "/data/media/movies", targetPrefix = target } });

        Assert.Equal(HttpStatusCode.BadRequest, relative.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Fact]
    public async Task Applying_an_unknown_preview_is_refused_and_a_preview_with_no_folders_changes_nothing()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);

        var preview = await client.PostAsync("/api/library/preview", null);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var body = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("plan").GetProperty("links").GetArrayLength());

        var missing = await client.PostAsJsonAsync("/api/library/plans/999999/apply", new { excludePaths = (string[]?)null });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Changing_a_root_folder_is_validated_and_existing_files_are_not_touched()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);
        int id;
        await using (var db = factory.Services.GetRequiredService<IDbContextFactory<OneArrDbContext>>().CreateDbContext())
        {
            var profile = new QualityProfile { Name = "HD" };
            db.QualityProfiles.Add(profile);
            await db.SaveChangesAsync();
            var seeded = new MediaItem { MediaType = MediaType.Movie, Provider = "tmdb", ProviderId = "1", Title = "Dune", Year = 2021, QualityProfileId = profile.Id, RootFolder = ValidRoot, HasFile = true };
            db.MediaItems.Add(seeded);
            await db.SaveChangesAsync();
            id = seeded.Id;
        }

        var invalid = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/media/{id}")
        {
            Content = JsonContent.Create(new { rootFolder = "relative\\folder" }),
        });
        var valid = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/media/{id}")
        {
            Content = JsonContent.Create(new { rootFolder = ValidRoot.Replace(@"Movies", "Films") }),
        });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, valid.StatusCode);
        await using var check = factory.Services.GetRequiredService<IDbContextFactory<OneArrDbContext>>().CreateDbContext();
        var item = await check.MediaItems.SingleAsync();
        Assert.True(item.HasFile);
        Assert.Equal(ValidRoot.Replace(@"Movies", "Films"), item.RootFolder);
    }

    private static async Task<int> SeedProfileAsync(ApiFactory factory)
    {
        await using var db = factory.Services.GetRequiredService<IDbContextFactory<OneArrDbContext>>().CreateDbContext();
        var profile = new QualityProfile { Name = "HD" };
        db.QualityProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    private static async Task<int> SeedMovieAsync(ApiFactory factory, string tmdb, string title, string rootFolder, string? legacyPath, bool monitored = true)
    {
        await using var db = factory.Services.GetRequiredService<IDbContextFactory<OneArrDbContext>>().CreateDbContext();
        var profileId = await db.QualityProfiles.Select(q => q.Id).FirstAsync();
        var item = new MediaItem
        {
            MediaType = MediaType.Movie, Provider = "tmdb", ProviderId = tmdb, Title = title, Year = 2000, Monitored = monitored,
            QualityProfileId = profileId, RootFolder = rootFolder, LegacyPath = legacyPath,
        };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    /// <summary>An item imported before legacy paths were kept: it has no LegacyPath, and the user has since edited it.</summary>
    [Fact]
    public async Task Re_importing_an_old_catalog_backfills_legacy_paths_without_overwriting_edits_or_duplicating()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);
        await SeedProfileAsync(factory);
        var id = await SeedMovieAsync(factory, "438631", "Dune", ValidRoot, legacyPath: null, monitored: false);

        var first = await client.PostAsJsonAsync("/api/migration/import", new
        {
            items = new[] { new { type = "movie", tmdbId = 438631, title = "Dune", monitored = true, legacyPath = "/data/media/movies/Dune (2021)", legacySource = "radarr" } },
            qualityProfileId = 1, rootFolder = OperatingSystem.IsWindows() ? @"E:\\Other" : "/other/root",
        });
        var second = await client.PostAsJsonAsync("/api/migration/import", new
        {
            items = new[] { new { type = "movie", tmdbId = 438631, title = "Dune", monitored = true, legacyPath = "/elsewhere/Dune", legacySource = "sonarr" } },
            qualityProfileId = 1, rootFolder = ValidRoot,
        });

        using var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.Equal("exists", body.RootElement.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        await using var db = factory.Services.GetRequiredService<IDbContextFactory<OneArrDbContext>>().CreateDbContext();
        var item = await db.MediaItems.SingleAsync();
        Assert.Equal(id, item.Id);
        Assert.Equal("/data/media/movies/Dune (2021)", item.LegacyPath);
        Assert.Equal("radarr", item.LegacySource);
        Assert.False(item.Monitored);
        Assert.Equal(ValidRoot, item.RootFolder);
        Assert.Equal(1, await db.MediaItems.CountAsync());
    }

    [Fact]
    public async Task Bulk_root_reassignment_moves_only_the_selected_entries_and_validates_the_target()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);
        await SeedProfileAsync(factory);
        await SeedMovieAsync(factory, "1", "One", "/data/media/movies", null);
        await SeedMovieAsync(factory, "2", "Two", "/data/media/movies", null);
        await SeedMovieAsync(factory, "3", "Three", ValidRoot, null);

        var invalid = await client.PostAsJsonAsync("/api/media/root-folder", new { mediaType = "movie", fromRootFolder = "/data/media/movies", toRootFolder = "relative\\x", ids = (List<int>?)null });
        var moved = await client.PostAsJsonAsync("/api/media/root-folder", new { mediaType = "movie", fromRootFolder = "/data/media/movies", toRootFolder = ValidRoot, ids = (List<int>?)null });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var body = JsonDocument.Parse(await moved.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("updated").GetInt32());
        await using var db = factory.Services.GetRequiredService<IDbContextFactory<OneArrDbContext>>().CreateDbContext();
        Assert.Equal(3, await db.MediaItems.CountAsync(m => m.RootFolder == ValidRoot));
    }

    [Fact]
    public async Task Mapping_suggestions_group_stored_legacy_folders_and_count_entries_still_without_one()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);
        await SeedProfileAsync(factory);
        await SeedMovieAsync(factory, "1", "Dune", ValidRoot, "/data/media/movies/Dune (2021)");
        await SeedMovieAsync(factory, "2", "Arrival", ValidRoot, "/data/media/movies/Arrival (2016)");
        await SeedMovieAsync(factory, "3", "Old", ValidRoot, null);

        using var body = JsonDocument.Parse(await (await client.GetAsync("/api/library/mapping-suggestions")).Content.ReadAsStringAsync());

        var suggestion = body.RootElement.GetProperty("suggestions").EnumerateArray().Single();
        Assert.Equal("/data/media/movies", suggestion.GetProperty("sourcePrefix").GetString());
        Assert.Equal(2, suggestion.GetProperty("count").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("entriesWithoutLegacyPath").GetInt32());
    }
}
