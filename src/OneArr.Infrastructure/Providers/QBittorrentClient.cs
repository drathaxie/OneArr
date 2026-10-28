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
/// qBittorrent WebUI API v2 client. The complete session cookie (SID or QBT_SID_port) is cached and renewed on 403.
/// Only adds torrents to a configured category and lists torrents in that category; it never deletes.
/// </summary>
public sealed class QBittorrentClient : IDownloadClient
{
    private readonly IHttpClientFactory _factory;
    private readonly ConcurrentDictionary<int, string> _sessions = new();
    // A login with no session cookie may be a localhost/whitelisted-subnet bypass.
    // Don't call it authenticated until a protected API request actually succeeds.
    // Store only this diagnostic flag, never a password or a response body.
    private readonly ConcurrentDictionary<int, bool> _loginsWithoutSid = new();

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
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"qBittorrent rejected the torrent (HTTP {(int)response.StatusCode}, {body.Length} bytes of body)");
        // Legacy: "Ok.". New WebAPI: structured counts; HTTP 202
        // indicates that the torrent is accepted but still pending.
        if (body.Equals("Ok.", StringComparison.OrdinalIgnoreCase)) return hash ?? "";
        if (body.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    (ResultCount(root, "success_count") > 0 || ResultCount(root, "pending_count") > 0))
                    return hash ?? "";
            }
            catch (JsonException) { /* Reject malformed responses below. */ }
        }
        throw new ExternalServiceException($"qBittorrent returned an unexpected torrent-add response (HTTP {(int)response.StatusCode}, {body.Length} bytes of body)");
    }

    /// <summary>Upload private-tracker .torrent bytes without disclosing session cookies to qBittorrent.</summary>
    public async Task<string> AddTorrentAsync(DownloadClientConfig client, byte[] torrent, string category, CancellationToken ct)
    {
        if (torrent.Length < 5 || torrent.Length > 16 * 1024 * 1024 || torrent[0] != (byte)'d')
            throw new ExternalServiceException("Invalid or oversized authenticated torrent payload");
        using var response = await SendAsync(client, ct, () =>
        {
            var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(torrent);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
            content.Add(file, "torrents", "release.torrent");
            content.Add(new StringContent(category), "category");
            return content;
        }, "api/v2/torrents/add", HttpMethod.Post);
        var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"qBittorrent rejected the uploaded torrent (HTTP {(int)response.StatusCode})");
        if (body.Equals("Ok.", StringComparison.OrdinalIgnoreCase)) return "";
        if (body.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    (ResultCount(root, "success_count") > 0 || ResultCount(root, "pending_count") > 0))
                    return "";
            }
            catch (JsonException) { /* fail closed below */ }
        }
        throw new ExternalServiceException("qBittorrent did not confirm the uploaded torrent");
    }

    public async Task<IReadOnlyList<DownloadClientItem>> ListAsync(DownloadClientConfig client, string category, CancellationToken ct)
    {
        var response = await SendAsync(client, ct, null, $"api/v2/torrents/info?category={Uri.EscapeDataString(category)}", HttpMethod.Get);
        if (!response.IsSuccessStatusCode) throw new ExternalServiceException($"qBittorrent list failed with HTTP {(int)response.StatusCode} after login");

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
            _loginsWithoutSid.TryRemove(client.Id, out _);
            await LoginAsync(client, http, ct);
            response = await SendOnceAsync(http, client.Id, body, path, method, ct);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                // Credentials were already accepted by the login endpoint
                // (LoginAsync throws on a real credential failure, and the
                // server logs a successful login in this shape), so a second
                // 403 is not a username/password failure. The usual causes
                // are qBittorrent host-header validation, CSRF protection, or
                // the session/bypass rules for this source IP and host, which
                // is why the two source IPs in the Execution Log matter:
                // a login that succeeds from one IP/host is not proof the
                // next request is accepted from the same one.
                var withoutSid = _loginsWithoutSid.ContainsKey(client.Id);
                response.Dispose();
                throw new ExternalServiceException(withoutSid
                    ? $"qBittorrent login returned a success-shaped response with no session cookie, but {path} returned HTTP 403 after re-login. Authentication could not be verified; this is not a username/password failure verdict. Check the WebUI address, authentication bypass settings, host-header validation and CSRF protection."
                    : $"qBittorrent signed in but {path} returned HTTP 403 after re-login. This is not a username/password failure; check host-header validation, CSRF protection, or whether the session is valid for this source IP and host.");
            }
        }
        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpClient http, int clientId, Func<HttpContent>? body, string path, HttpMethod method, CancellationToken ct)
    {
        var sessionCookie = _sessions.GetValueOrDefault(clientId) ?? "";
        return await OutboundHttp.SendWithRetryAsync(http, () =>
        {
            var req = new HttpRequestMessage(method, path);
            // Keep the name issued by qBittorrent: SID or QBT_SID_<port>.
            // Do not send an empty cookie for localhost/whitelisted bypasses.
            if (sessionCookie.Length > 0) req.Headers.Add("Cookie", sessionCookie);
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

        var body = (await response.Content.ReadAsStringAsync(ct)).Trim();

        // qBittorrent <=5.1: HTTP 200 + "Ok." / "Fails.", SID cookie.
        // qBittorrent >=5.2: HTTP 204 with no body, QBT_SID_<port> cookie.
        // Some intermediaries turn 204 into 200 without adding a response body.
        // If no cookie is issued (e.g. auth bypass), a protected read must
        // still succeed before the connection test reports success.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new ExternalServiceException("qBittorrent login returned HTTP 401. Check the username and password in Settings.");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new ExternalServiceException("qBittorrent login returned HTTP 403. Check whether the client IP is banned or WebUI access is denied; do not keep retrying.");
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"qBittorrent login returned HTTP {(int)response.StatusCode}. Check the service URL, reverse proxy, and qBittorrent version.");
        if (body.Equals("Fails.", StringComparison.OrdinalIgnoreCase))
            throw new ExternalServiceException("qBittorrent login failed. Check the username and password in Settings.");

        var recognized = body.Equals("Ok.", StringComparison.OrdinalIgnoreCase)
            || (body.Length == 0 && response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent);
        if (!recognized)
            throw new ExternalServiceException($"qBittorrent login returned HTTP {(int)response.StatusCode} with an unexpected body ({body.Length} bytes, neither 'Ok.' nor 'Fails.'). This is not treated as a username/password failure; check the WebUI address or reverse proxy.");

        // Preserve the exact cookie name as well as its value. Sending
        // a QBT_SID_<port> token under the old SID name leads to a 403.
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';', 2)[0].Trim()).FirstOrDefault(IsQbitSessionCookie)
            : null;
        if (cookie is null)
        {
            _sessions[client.Id] = "";
            _loginsWithoutSid[client.Id] = true;
            return;
        }
        _loginsWithoutSid.TryRemove(client.Id, out _);
        _sessions[client.Id] = cookie;
    }

    private static int ResultCount(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count)
            ? count : 0;

    private static bool IsQbitSessionCookie(string cookie)
    {
        var equalsAt = cookie.IndexOf('=');
        if (equalsAt <= 0 || equalsAt == cookie.Length - 1) return false;
        var name = cookie[..equalsAt];
        if (name == "SID") return true;
        if (!name.StartsWith("QBT_SID_", StringComparison.Ordinal)) return false;
        var port = name["QBT_SID_".Length..];
        return port.Length > 0 && port.All(char.IsAsciiDigit);
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
