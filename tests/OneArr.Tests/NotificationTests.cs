using System.Net;
using Microsoft.EntityFrameworkCore;
using OneArr.Domain;
using OneArr.Infrastructure.Services;
using Xunit;

namespace OneArr.Tests;

/// <summary>Each download status change is announced once, only to the configured webhook; old history is never replayed.</summary>
public class NotificationTests
{
    private const string Hook = "https://discord.com/api/webhooks/123/token-abc";

    private static (NotificationService Service, List<string> Bodies) Notifier(TestWorld w, HttpStatusCode status = HttpStatusCode.NoContent)
    {
        var bodies = new List<string>();
        w.Http.Route("discord.com", req =>
        {
            bodies.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(status);
        });
        return (new NotificationService(w.Db, w.Settings, new FakeFactory(w.Http)), bodies);
    }

    [Fact]
    public async Task A_grab_and_its_import_are_announced_once_each()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        await w.Settings.SetAsync(SettingKeys.NotifyWebhook, Hook);
        var (notify, bodies) = Notifier(w);
        w.Indexer.Body = TestWorld.Torznab(("The.Matrix.1999.1080p.BluRay.x264-GRP", "abcdef0123456789abcdef0123456789abcdef01", 10));
        await w.AddMovieAsync("The Matrix", 1999);
        await w.ReconcileLibraryAsync();
        await w.Search.RunOnceAsync(default);

        await notify.RunAsync(default);
        await notify.RunAsync(default);
        await using (var db = w.Db.CreateDbContext())
        {
            (await db.DownloadJobs.SingleAsync()).Status = DownloadStatus.Imported;
            await db.SaveChangesAsync();
        }
        await notify.RunAsync(default);

        Assert.Equal(2, bodies.Count);
        Assert.Contains("Grabbed: The Matrix", bodies[0]);
        Assert.Contains("\"content\"", bodies[0]);
        Assert.Contains("Imported: The Matrix", bodies[1]);
    }

    [Fact]
    public async Task Without_a_webhook_changes_are_marked_seen_and_not_replayed_later()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        var (notify, bodies) = Notifier(w);
        var id = await w.AddMovieAsync("Dune", 2021);
        await using (var db = w.Db.CreateDbContext())
        {
            db.DownloadJobs.Add(new DownloadJob { MediaItemId = id, MediaType = MediaType.Movie, ReleaseTitle = "Dune.2021.1080p", Status = DownloadStatus.Queued });
            await db.SaveChangesAsync();
        }
        await notify.RunAsync(default);
        await w.Settings.SetAsync(SettingKeys.NotifyWebhook, Hook);
        await notify.RunAsync(default);

        Assert.Empty(bodies);
    }

    [Fact]
    public async Task A_failing_webhook_is_logged_not_retried_forever()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        await w.Settings.SetAsync(SettingKeys.NotifyWebhook, Hook);
        var (notify, bodies) = Notifier(w, HttpStatusCode.BadRequest);
        var id = await w.AddMovieAsync("Dune", 2021);
        await using (var db = w.Db.CreateDbContext())
        {
            db.DownloadJobs.Add(new DownloadJob { MediaItemId = id, MediaType = MediaType.Movie, ReleaseTitle = "Dune.2021.1080p", Status = DownloadStatus.Failed, Error = "boom" });
            await db.SaveChangesAsync();
        }
        Assert.Equal("Sent 0 notification(s)", await notify.RunAsync(default));
        await notify.RunAsync(default);
        Assert.Single(bodies);
    }
}
