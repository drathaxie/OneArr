using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Security;
using OneArr.Infrastructure.Http;
using OneArr.Infrastructure.Persistence;

namespace OneArr.Infrastructure.Services;

public sealed record Notification(string Event, string Title, string Message);

/// <summary>
/// Announces grabs, imports and failures to one webhook. Discord webhooks get a chat message; any other URL gets JSON
/// {event, title, message, time}. Each download's status change is considered once (DownloadJob.NotifiedStatus), so turning
/// notifications on later never replays old history, and a failed send is logged, not retried in a loop.
/// </summary>
public sealed class NotificationService
{
    public const string ClientName = "notify";
    private const int MaxPerRun = 10;
    private readonly IDbContextFactory<OneArrDbContext> _db;
    private readonly SettingsStore _settings;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<NotificationService>? _log;

    public NotificationService(IDbContextFactory<OneArrDbContext> db, SettingsStore settings, IHttpClientFactory http, ILogger<NotificationService>? log = null)
    {
        _db = db;
        _settings = settings;
        _http = http;
        _log = log;
    }

    public static bool IsDiscord(Uri uri) =>
        uri.Host is "discord.com" or "discordapp.com" or "ptb.discord.com" or "canary.discord.com" && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);

    public async Task<string> RunAsync(CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var changed = await db.DownloadJobs.Where(j => j.NotifiedStatus == null || j.NotifiedStatus != j.Status).ToListAsync(ct);
        if (changed.Count == 0) return "Nothing to announce";
        var webhook = await _settings.GetAsync(SettingKeys.NotifyWebhook, ct);
        var onGrab = await _settings.GetBoolAsync(SettingKeys.NotifyOnGrab, true, ct);
        var onImport = await _settings.GetBoolAsync(SettingKeys.NotifyOnImport, true, ct);
        var onFailure = await _settings.GetBoolAsync(SettingKeys.NotifyOnFailure, true, ct);

        var itemIds = changed.Where(j => j.MediaItemId != null).Select(j => j.MediaItemId!.Value).Distinct().ToList();
        var titles = await db.MediaItems.AsNoTracking().Where(m => itemIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Title, ct);
        var episodeIds = changed.Where(j => j.EpisodeId != null).Select(j => j.EpisodeId!.Value).Distinct().ToList();
        var episodes = await db.Episodes.AsNoTracking().Where(e => episodeIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => $"S{e.SeasonNumber:00}E{e.EpisodeNumber:00}", ct);

        var pending = new List<Notification>();
        foreach (var job in changed)
        {
            var name = (job.MediaItemId is int id && titles.TryGetValue(id, out var t) ? t : "Unknown item")
                       + (job.EpisodeId is int e && episodes.TryGetValue(e, out var label) ? " " + label : "");
            var note = (job.NotifiedStatus, job.Status) switch
            {
                (null, DownloadStatus.Queued or DownloadStatus.Downloading) when onGrab => new Notification("grab", "Grabbed: " + name, job.ReleaseTitle),
                (_, DownloadStatus.Imported) when onImport => new Notification("import", "Imported: " + name, job.ReleaseTitle),
                (_, DownloadStatus.Failed) when onFailure => new Notification("failure", "Failed: " + name, OutboundHttp.Redact(job.Error ?? job.ReleaseTitle)),
                (_, DownloadStatus.Missing) when onFailure => new Notification("failure", "Missing from the download client: " + name, job.ReleaseTitle),
                _ => null,
            };
            if (note is not null) pending.Add(note);
            job.NotifiedStatus = job.Status;
        }
        await db.SaveChangesAsync(ct);

        if (string.IsNullOrWhiteSpace(webhook) || pending.Count == 0) return $"{pending.Count} event(s), no webhook set";
        var sent = 0;
        foreach (var n in pending.Take(MaxPerRun))
            if (await TrySendAsync(webhook, n, ct)) sent++;
        if (pending.Count > MaxPerRun && await TrySendAsync(webhook, new Notification("summary", $"{pending.Count - MaxPerRun} more event(s)", "See Activity in OneArr"), ct)) sent++;
        return $"Sent {sent} notification(s)";
    }

    /// <summary>Sends one message now; for the Test button. Throws with a readable message on failure.</summary>
    public async Task SendAsync(string webhook, Notification n, CancellationToken ct)
    {
        var uri = UrlGuard.RequireServiceUrl(webhook);
        var client = _http.CreateClient(ClientName);
        object body = IsDiscord(uri)
            ? new { content = $"**{n.Title}**\n{n.Message}" }
            : new { @event = n.Event, title = n.Title, message = n.Message, time = DateTimeOffset.UtcNow };
        using var response = await client.PostAsJsonAsync(uri, body, ct);
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"The webhook answered HTTP {(int)response.StatusCode}");
    }

    private async Task<bool> TrySendAsync(string webhook, Notification n, CancellationToken ct)
    {
        try { await SendAsync(webhook, n, ct); return true; }
        catch (Exception ex) when (ex is ExternalServiceException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log?.LogWarning("Notification not sent: {Message}", OutboundHttp.Redact(ex.Message));
            return false;
        }
    }
}
