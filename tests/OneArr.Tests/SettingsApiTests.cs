using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OneArr.Tests;

/// <summary>Runs the real API host against a throwaway config directory.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public string ConfigDir { get; }
    private readonly bool _ownsConfigDir;

    public ApiFactory(string? configDir = null)
    {
        _ownsConfigDir = configDir is null;
        ConfigDir = configDir ?? Path.Combine(Path.GetTempPath(), "onearr-api-" + Guid.NewGuid().ToString("N"));
        // Read by Program at startup, so set before the host is created.
        Environment.SetEnvironmentVariable("ONEARR_CONFIG_DIR", ConfigDir);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        // Only remove directories this factory created; a caller-supplied directory is cleaned up by the caller.
        if (_ownsConfigDir) RemoveConfigDir(ConfigDir);
    }

    /// <summary>
    /// Deletes a config directory. Windows keeps SQLite files locked while pooled connections are open, so pools are
    /// cleared first and deletion is retried briefly. Failure is reported, not swallowed.
    /// </summary>
    public static void RemoveConfigDir(string dir)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var attempt = 1; ; attempt++)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }
}

/// <summary>
/// Settings endpoint behaviour: defaults, persistence, secrets never returned, path validation, and authentication.
/// Each test starts from a fresh config directory unless it is deliberately testing persistence.
/// </summary>
[Collection(ApiHostCollection.Name)]
public class SettingsApiTests
{
    private const string Password = "correct horse battery";
    private const string TmdbSecret = "tmdb-SECRET-0123456789";

    private static async Task<HttpClient> SignedInAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        var setup = await client.PostAsJsonAsync("/api/auth/setup", new { password = Password });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var token = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task NamingPreviewEndpointRequiresAuthenticationAndDoesNotChangeFiles()
    {
        using var factory = new ApiFactory();
        var anon = factory.CreateClient();
        var sample = new { kind = "movie", template = "{Title} ({Year}) - {Quality}",
            title = "A: Movie", originalFile = "release.mkv", year = 2026, quality = "1080p" };
        var unauthorized = await anon.PostAsJsonAsync("/api/naming/preview", sample);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        var client = await SignedInAsync(factory);
        var preview = await client.PostAsJsonAsync("/api/naming/preview", sample);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var json = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("A Movie (2026) - 1080p.mkv", json.GetProperty("fileName").GetString());
        Assert.False(json.GetProperty("applied").GetBoolean());
        var bad = await client.PostAsJsonAsync("/api/naming/preview", new {
            kind = "movie", template = "{Title}/../../bad", title = "A Movie", originalFile = "release.mkv"
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task DefaultsAreSafeBeforeAnySettingIsSaved()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings");

        Assert.True(settings.GetProperty("dryRunImport").GetBoolean(), "imports must default to dry-run");
        Assert.False(settings.GetProperty("tmdbConfigured").GetBoolean());
        Assert.False(settings.GetProperty("automaticGrabbing").GetBoolean());
    }

    [Fact]
    public async Task SettingsEndpointsRequireSignIn()
    {
        using var factory = new ApiFactory();
        var anonymous = factory.CreateClient();

        var get = await anonymous.GetAsync("/api/settings");
        var put = await anonymous.PutAsJsonAsync("/api/settings", new { dryRunImport = false });

        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
    }

    [Fact]
    public async Task RenamePolicyDefaultsOffAndInvalidTemplateIsRejectedAtomically()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);
        var initial = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.False(initial.GetProperty("renameOnImport").GetBoolean());
        Assert.Contains("{Title}", initial.GetProperty("movieNamingTemplate").GetString());

        var invalid = await client.PutAsJsonAsync("/api/settings", new {
            renameOnImport = true, dryRunImport = false, movieNamingTemplate = "{Title}/../../evil"
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var afterInvalid = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.False(afterInvalid.GetProperty("renameOnImport").GetBoolean());
        Assert.True(afterInvalid.GetProperty("dryRunImport").GetBoolean());

        var saved = await client.PutAsJsonAsync("/api/settings", new {
            renameOnImport = true, movieNamingTemplate = "{Title} - {Year}",
            seriesNamingTemplate = "{Title} - S{Season:00}E{Episode:00}"
        });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        var result = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.True(result.GetProperty("renameOnImport").GetBoolean());
        Assert.Equal("{Title} - {Year}", result.GetProperty("movieNamingTemplate").GetString());
        Assert.True(result.GetProperty("dryRunImport").GetBoolean(),
            "Enabling naming must not enable automatic imports or disable dry-run.");
    }

    [Fact]
    public async Task TmdbKeyIsStoredButNeverReturned()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);

        var put = await client.PutAsJsonAsync("/api/settings", new { tmdbApiKey = TmdbSecret });
        var raw = await client.GetStringAsync("/api/settings");
        var settings = JsonDocument.Parse(raw).RootElement;

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.True(settings.GetProperty("tmdbConfigured").GetBoolean());
        Assert.DoesNotContain(TmdbSecret, raw);
    }

    [Fact]
    public async Task BlankTmdbKeyClearsTheStoredKey()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);
        await client.PutAsJsonAsync("/api/settings", new { tmdbApiKey = TmdbSecret });

        await client.PutAsJsonAsync("/api/settings", new { tmdbApiKey = "   " });
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings");

        Assert.False(settings.GetProperty("tmdbConfigured").GetBoolean());
    }

    [Fact]
    public async Task PathsMustBeAbsoluteAndCannotTraverse()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);
        var absolute = Path.Combine(Path.GetTempPath(), "onearr-ok");

        var relative = await client.PutAsJsonAsync("/api/settings", new { mediaRoot = "relative/media" });
        var traversal = await client.PutAsJsonAsync("/api/settings", new { torrentRoot = absolute + "/../escape" });
        var valid = await client.PutAsJsonAsync("/api/settings", new { mediaRoot = absolute });

        Assert.Equal(HttpStatusCode.BadRequest, relative.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, valid.StatusCode);
        var body = await relative.Content.ReadAsStringAsync();
        Assert.Contains("absolute", body);
    }

    [Fact]
    public async Task AutomaticGrabbingIsOffByDefaultAndCanBeSwitchedOn()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);
        var before = await client.GetFromJsonAsync<JsonElement>("/api/settings");

        var put = await client.PutAsJsonAsync("/api/settings", new { automaticGrabbing = true });
        var after = await client.GetFromJsonAsync<JsonElement>("/api/settings");

        Assert.False(before.GetProperty("automaticGrabbing").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.True(after.GetProperty("automaticGrabbing").GetBoolean());
    }

    [Fact]
    public async Task QualityProfilesCanBeEditedAndDeleteIsGuardedByDefaultsAndRemainingProfiles()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);
        var initial = await client.GetFromJsonAsync<JsonElement>("/api/quality-profiles");
        Assert.Equal(2, initial.GetArrayLength());
        var first = initial[0].GetProperty("id").GetInt32();
        var second = initial[1].GetProperty("id").GetInt32();

        var invalid = await client.PutAsJsonAsync($"/api/quality-profiles/{first}", new {
            name = "Nope", minResolution = 2160, cutoffResolution = 720, allowWebSources = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var badName = await client.PostAsJsonAsync("/api/quality-profiles", new {
            name = " ", minResolution = 720, cutoffResolution = 1080, allowWebSources = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, badName.StatusCode);

        var saved = await client.PutAsJsonAsync($"/api/quality-profiles/{first}", new {
            name = "My 1080p", minResolution = 720, cutoffResolution = 1080, allowWebSources = false
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var profile = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("My 1080p", profile.GetProperty("name").GetString());
        Assert.False(profile.GetProperty("allowWebSources").GetBoolean());

        await client.PutAsJsonAsync("/api/settings", new { defaultMovieProfileId = first });
        var blockedDefault = await client.DeleteAsync($"/api/quality-profiles/{first}");
        Assert.Equal(HttpStatusCode.Conflict, blockedDefault.StatusCode);
        Assert.Contains("default", await blockedDefault.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var deleteUnused = await client.DeleteAsync($"/api/quality-profiles/{second}");
        Assert.Equal(HttpStatusCode.NoContent, deleteUnused.StatusCode);
        // Even after changing the default, the only remaining profile may not be deleted.
        var deleteLast = await client.DeleteAsync($"/api/quality-profiles/{first}");
        Assert.Equal(HttpStatusCode.Conflict, deleteLast.StatusCode);
        Assert.Contains("last", await deleteLast.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QualityProfileInUseByMediaIsNeverDeleted()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);
        var profiles = await client.GetFromJsonAsync<JsonElement>("/api/quality-profiles");
        var first = profiles[0].GetProperty("id").GetInt32();
        using var scope = factory.Services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<OneArr.Infrastructure.Persistence.OneArrDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.MediaItems.Add(new OneArr.Domain.MediaItem {
                MediaType = OneArr.Domain.MediaType.Movie, Provider = "tmdb", ProviderId = "123",
                Title = "Test Film", RootFolder = "C:\\Media", QualityProfileId = first
            });
            await db.SaveChangesAsync();
        }
        var deleted = await client.DeleteAsync($"/api/quality-profiles/{first}");
        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.Contains("assigned", await deleted.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        // Editing is allowed and preserves stable references.
        var edit = await client.PutAsJsonAsync($"/api/quality-profiles/{first}", new {
            name = "Retuned", minResolution = 1080, cutoffResolution = 2160, allowWebSources = true
        });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        await using var db2 = await dbFactory.CreateDbContextAsync();
        Assert.Equal(first, (await db2.MediaItems.SingleAsync()).QualityProfileId);
    }

    [Fact]
    public async Task DryRunChoiceAndPathsSurviveARestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "onearr-restart-" + Guid.NewGuid().ToString("N"));
        var media = Path.Combine(Path.GetTempPath(), "onearr-media-restart");
        try
        {
            using (var first = new ApiFactory(dir))
            {
                var client = await SignedInAsync(first);
                await client.PutAsJsonAsync("/api/settings", new { dryRunImport = false, mediaRoot = media });
            }

            await RestartAndCheckAsync(dir, media);
        }
        finally
        {
            ApiFactory.RemoveConfigDir(dir);
        }
    }

    private static async Task RestartAndCheckAsync(string dir, string media)
    {
        using var second = new ApiFactory(dir);
        var restarted = second.CreateClient();
        var login = await restarted.PostAsJsonAsync("/api/auth/login", new { password = Password });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        restarted.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var settings = await restarted.GetFromJsonAsync<JsonElement>("/api/settings");

        Assert.False(settings.GetProperty("dryRunImport").GetBoolean());
        Assert.Equal(media, settings.GetProperty("mediaRoot").GetString());
    }
}
