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
/// Dismissing a problem the operator fixed by hand. It hides the problem only, never changes a job, a file or the catalog,
/// never applies to a problem that blocks grabbing, and reopens when the underlying download changes.
/// </summary>
[Collection(ApiHostCollection.Name)]
public class ProblemDismissalTests
{
    private const string Password = "correct horse battery";

    private static async Task<HttpClient> SignedInAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        var setup = await client.PostAsJsonAsync("/api/auth/setup", new { password = Password });
        using var doc = JsonDocument.Parse(await setup.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("token").GetString());
        return client;
    }

    private sealed record Seeded(int FailedImportJobId, int MissingJobId, int UnmatchedRecordId);

    private static async Task<Seeded> SeedAsync(ApiFactory factory)
    {
        await using var db = factory.Services.GetRequiredService<IDbContextFactory<OneArrDbContext>>().CreateDbContext();
        var failed = new DownloadJob { MediaType = MediaType.Movie, ReleaseTitle = "Arrival.2016.1080p", Status = DownloadStatus.Failed, ImportFailed = true, Error = "One or more files failed to import", Category = "onearr-movies" };
        var missing = new DownloadJob { MediaType = MediaType.Movie, ReleaseTitle = "Dune.2021.1080p", Status = DownloadStatus.Missing, Category = "onearr-movies" };
        db.DownloadJobs.AddRange(failed, missing);
        await db.SaveChangesAsync();
        var record = new ImportRecord { DownloadJobId = failed.Id, SourcePath = "/downloads/Mystery.mkv", DestinationPath = "", Action = ImportAction.Unmatched, Success = false };
        db.ImportRecords.Add(record);
        await db.SaveChangesAsync();
        return new Seeded(failed.Id, missing.Id, record.Id);
    }

    private static async Task<List<JsonElement>> ProblemsAsync(HttpClient client)
    {
        var body = await (await client.GetAsync("/api/activity/problems")).Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static bool HasKey(List<JsonElement> problems, string key) =>
        problems.Any(p => p.TryGetProperty("dismissKey", out var k) && k.GetString() == key);

    [Fact]
    public async Task A_dismissed_import_failure_disappears_and_nothing_else_changes()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);
        var seeded = await SeedAsync(factory);
        var key = $"import-failed:{seeded.FailedImportJobId}";
        Assert.True(HasKey(await ProblemsAsync(client), key));

        var response = await client.PostAsJsonAsync("/api/activity/problems/dismiss", new { key });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(HasKey(await ProblemsAsync(client), key));
        await using var db = factory.Services.GetRequiredService<IDbContextFactory<OneArrDbContext>>().CreateDbContext();
        var job = await db.DownloadJobs.SingleAsync(j => j.Id == seeded.FailedImportJobId);
        Assert.Equal(DownloadStatus.Failed, job.Status);
        Assert.True(job.ImportFailed);
    }

    [Fact]
    public async Task A_dismissed_problem_returns_when_the_download_fails_differently()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);
        var seeded = await SeedAsync(factory);
        var key = $"import-failed:{seeded.FailedImportJobId}";
        await client.PostAsJsonAsync("/api/activity/problems/dismiss", new { key });

        await using (var db = factory.Services.GetRequiredService<IDbContextFactory<OneArrDbContext>>().CreateDbContext())
        {
            var job = await db.DownloadJobs.SingleAsync(j => j.Id == seeded.FailedImportJobId);
            job.Error = "Verification failed for a new copy";
            await db.SaveChangesAsync();
        }

        Assert.True(HasKey(await ProblemsAsync(client), key));
    }

    [Fact]
    public async Task A_missing_download_cannot_be_dismissed_because_it_blocks_grabbing()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);
        var seeded = await SeedAsync(factory);
        var key = $"download-missing:{seeded.MissingJobId}";

        var response = await client.PostAsJsonAsync("/api/activity/problems/dismiss", new { key });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = factory.Services.GetRequiredService<IDbContextFactory<OneArrDbContext>>().CreateDbContext();
        Assert.Equal(DownloadStatus.Missing, (await db.DownloadJobs.SingleAsync(j => j.Id == seeded.MissingJobId)).Status);
    }

    [Fact]
    public async Task An_unmatched_file_can_be_dismissed_and_an_unknown_problem_is_refused()
    {
        using var factory = new ApiFactory();
        var client = await SignedInAsync(factory);
        var seeded = await SeedAsync(factory);
        var key = $"import-unmatched:{seeded.UnmatchedRecordId}";
        Assert.True(HasKey(await ProblemsAsync(client), key));

        var dismissed = await client.PostAsJsonAsync("/api/activity/problems/dismiss", new { key });
        var unknown = await client.PostAsJsonAsync("/api/activity/problems/dismiss", new { key = "import-unmatched:999999" });
        var garbage = await client.PostAsJsonAsync("/api/activity/problems/dismiss", new { key = "not-a-key" });

        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);
        Assert.False(HasKey(await ProblemsAsync(client), key));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
    }

    [Fact]
    public async Task Dismissing_requires_sign_in()
    {
        using var factory = new ApiFactory();
        var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/api/activity/problems/dismiss", new { key = "import-failed:1" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
