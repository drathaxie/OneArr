using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Http;
using OneArr.Infrastructure.Security;

namespace OneArr.Infrastructure.Providers;

/// <summary>
/// qBittorrent WebUI API v2 client. Cookie (SID) login is cached per configured client and re-established on 403.
/// Only adds torrents to a configured category and lists torrents in that category; it never deletes.
/// </summary>
public sealed class QBittorrentClient : IDownloadClient
{
    private readonly IHttpClientFactory _factory;
    private readonly ConcurrentDictionary<int, string> _sessions = new();

    public QBittorrentClient(IHttpClientFactory factory)
    {
        _factory = factory;
    }

    public async Task<string> AddAsync(DownloadClientConfig client, string link, string category, CancellationToken ct)
    {
        if (!UrlGuard.IsAllowedDownloadLink(link)) throw new ExternalServiceException("Refusing to submit a non-magnet, non-http link");
        var hash = link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? MagnetInfoHash(link) : null;

        var response = await SendAsync(client, ct, () => new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["urls"] = link,
            ["category"] = category
        }), "api/v2/torrents/add", HttpMethod.Post);

        var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
        if (!response.IsSuccessStatusCode || !body.Equals("Ok.", StringComparison.OrdinalIgnoreCase))
            throw new ExternalServiceException($"qBittorrent rejected the torrent (HTTP {(int)response.StatusCode}, {body.Length} bytes of body)");
        return hash ?? "";
    }

    public async Task<IReadOnlyList<DownloadClientItem>> ListAsync(DownloadClientConfig client, string category, CancellationToken ct)
    {
        var response = await SendAsync(client, ct, null, $"api/v2/torrents/info?category={Uri.EscapeDataString(category)}", HttpMethod.Get);
        if (!response.IsSuccessStatusCode) throw new ExternalServiceException($"qBittorrent list failed with HTTP {(int)response.StatusCode}");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var items = new List<DownloadClientItem>();
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            // Defensive parsing: a client field missing in one torrent must not abort the whole
            // queue sync. Torrents without a hash cannot be matched to jobs and are skipped.
            if (!t.TryGetProperty("hash", out var hashProp) || hashProp.GetString() is not { } hashValue) continue;
            var state = t.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
            var contentPath = t.TryGetProperty("content_path", out var cp) ? cp.GetString() : null;
            items.Add(new DownloadClientItem(
                hashValue.ToLowerInvariant(),
                t.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                state,
                t.TryGetProperty("progress", out var pr) ? pr.GetDouble() : 0,
                contentPath,
                t.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "",
                t.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
                t.TryGetProperty("dlspeed", out var dl) ? dl.GetInt64() : 0,
                t.TryGetProperty("eta", out var eta) ? eta.GetInt64() : -1));
        }
        return items;
    }

    /// <summary>Maps qBittorrent states to OneArr statuses.</summary>
    public static DownloadStatus MapState(string state, double progress) => state switch
    {
        "error" or "missingFiles" => DownloadStatus.Failed,
        "uploading" or "stalledUP" or "queuedUP" or "forcedUP" or "pausedUP" or "stoppedUP" or "checkingUP" => DownloadStatus.Completed,
        "queuedDL" or "metaDL" => DownloadStatus.Queued,
        "downloading" or "stalledDL" or "forcedDL" or "checkingDL" or "pausedDL" or "stoppedDL" or "allocating" => progress >= 1 ? DownloadStatus.Completed : DownloadStatus.Downloading,
        _ => DownloadStatus.Unknown
    };

    /// <summary>Extracts a lowercase hex info hash from a magnet URI (accepts hex and base32 forms).</summary>
    public static string? MagnetInfoHash(string magnet)
    {
        var query = new Uri(magnet).Query.TrimStart('?');
        foreach (var part in query.Split('&'))
        {
            if (!part.StartsWith("xt=urn:btih:", StringComparison.OrdinalIgnoreCase)) continue;
            var value = part["xt=urn:btih:".Length..];
            if (value.Length == 40 && value.All(Uri.IsHexDigit)) return value.ToLowerInvariant();
            if (value.Length == 32) return Convert.ToHexString(Base32Decode(value)).ToLowerInvariant();
        }
        return null;
    }

    private async Task<HttpResponseMessage> SendAsync(DownloadClientConfig client, CancellationToken ct, Func<HttpContent>? body, string path, HttpMethod method)
    {
        var baseUri = UrlGuard.RequireServiceUrl(client.BaseUrl);
        var http = _factory.CreateClient(OutboundHttp.QbittorrentClient);
        http.BaseAddress = new Uri(baseUri, "/");

        if (!_sessions.ContainsKey(client.Id)) await LoginAsync(client, http, ct);

        var response = await SendOnceAsync(http, client.Id, body, path, method, ct);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            response.Dispose();
            _sessions.TryRemove(client.Id, out _);
            await LoginAsync(client, http, ct);
            response = await SendOnceAsync(http, client.Id, body, path, method, ct);
        }
        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpClient http, int clientId, Func<HttpContent>? body, string path, HttpMethod method, CancellationToken ct)
    {
        var sid = _sessions.GetValueOrDefault(clientId) ?? "";
        return await OutboundHttp.SendWithRetryAsync(http, () =>
        {
            var req = new HttpRequestMessage(method, path);
            req.Headers.Add("Cookie", "SID=" + sid);
            req.Headers.Referrer = http.BaseAddress;
            if (body is not null) req.Content = body();
            return req;
        }, ct, maxRetries: method == HttpMethod.Get ? 2 : 0);
    }

    private async Task LoginAsync(DownloadClientConfig client, HttpClient http, CancellationToken ct)
    {
        using var response = await OutboundHttp.SendWithRetryAsync(http, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "api/v2/auth/login")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = client.Username,
                    ["password"] = client.Password
                })
            };
            req.Headers.Referrer = http.BaseAddress;
            return req;
        }, ct, maxRetries: 0);

        var text = (await response.Content.ReadAsStringAsync(ct)).Trim();
        if (!response.IsSuccessStatusCode || !text.Equals("Ok.", StringComparison.OrdinalIgnoreCase))
            throw new ExternalServiceException("qBittorrent login failed. Check the username and password in Settings.");

        var cookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith("SID=", StringComparison.Ordinal))
            : null;
        if (cookie is null) throw new ExternalServiceException("qBittorrent did not return a session cookie");
        _sessions[client.Id] = cookie["SID=".Length..];
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value = 0;
        var output = new List<byte>();
        foreach (var c in input.ToUpperInvariant())
        {
            var idx = alphabet.IndexOf(c);
            if (idx < 0) continue;
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(value >> (bits - 8)));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
