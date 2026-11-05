using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace OneArr.Tests;

[Collection(ApiHostCollection.Name)]
public class DownloadPathMappingApiTests
{
    [Fact]
    public async Task AuthenticatedClientRuleCanBeTestedSavedAndRemovedWithoutFileWrites()
    {
        using var dirs = new TempTree();
        using var app = new ApiFactory();
        var client = app.CreateClient();
        var unauthorized = await client.GetAsync("/api/download-clients/1/path-mapping");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        var setup = await client.PostAsJsonAsync("/api/auth/setup", new { password = "strong-test-password" });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var token = (await setup.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var setRoot = await client.PutAsJsonAsync("/api/settings", new { torrentRoot = dirs.Torrents });
        Assert.Equal(HttpStatusCode.NoContent, setRoot.StatusCode);
        var create = await client.PostAsJsonAsync("/api/download-clients", new {
            name = "qBittorrent", baseUrl = "http://127.0.0.1:8080", username = "admin",
            password = "test-secret", movieCategory = "onearr-movies", tvCategory = "onearr-tv", enabled = true
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();
        var bad = await client.PutAsJsonAsync($"/api/download-clients/{id}/path-mapping", new {
            remotePrefix = "/downloads", localPrefix = dirs.Media
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var put = await client.PutAsJsonAsync($"/api/download-clients/{id}/path-mapping", new {
            remotePrefix = "/downloads", localPrefix = dirs.Torrents
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var get = await client.GetFromJsonAsync<JsonElement>($"/api/download-clients/{id}/path-mapping");
        Assert.Equal("/downloads", get.GetProperty("mapping").GetProperty("remotePrefix").GetString());

        var sample = "/downloads/Show/episode.mkv";
        var preview = await client.PostAsJsonAsync(
            $"/api/download-clients/{id}/path-mapping/preview", new {
                remotePrefix = "/downloads", localPrefix = dirs.Torrents, reportedPath = sample
            });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var json = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Path.Combine(dirs.Torrents, "Show", "episode.mkv"),
            json.GetProperty("localPath").GetString());
        Assert.False(json.GetProperty("exists").GetBoolean());
        Assert.False(json.GetProperty("applied").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(dirs.Torrents, "Show")));

        var removed = await client.DeleteAsync($"/api/download-clients/{id}/path-mapping");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>($"/api/download-clients/{id}/path-mapping");
        Assert.Equal(JsonValueKind.Null, after.GetProperty("mapping").ValueKind);
    }
}
