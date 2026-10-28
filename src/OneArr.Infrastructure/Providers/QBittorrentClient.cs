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
    // Clients whose last login returned "Ok." but no SID cookie. That is a real,
    // documented shape (localhost / whitelisted-subnet bypass, or a production
    // HTTP handler that consumed Set-Cookie instead of exposing it), not a
    // credential failure, so it is remembered only as a boolean, never with
    // any cookie value, so a later 403 can be reported as a post-login 403
    // instead of being misreported as bad credentials.
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
        if (!response.IsSuccessStatusCode || !body.Equals("Ok.", StringComparison.OrdinalIgnoreCase))
            throw new ExternalServiceException($"qBittorrent rejected the torrent (HTTP {(int)response.StatusCode}, {body.Length} bytes of body)");
        return hash ?? "";
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
                    ? $"qBittorrent signed in (login returned Ok.) but {path} returned HTTP 403 with no session cookie issued. This is not a username/password failure; check host-header validation, CSRF protection, or the bypass subnet for this source IP, and whether the HTTP client is consuming the Set-Cookie header instead of returning it."
                    : $"qBittorrent signed in but {path} returned HTTP 403 after re-login. This is not a username/password failure; check host-header validation, CSRF protection, or whether the session is valid for this source IP and host.");
            }
        }
        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpClient http, int clientId, Func<HttpContent>? body, string path, HttpMethod method, CancellationToken ct)
    {
        var sid = _sessions.GetValueOrDefault(clientId) ?? "";
        return await OutboundHttp.SendWithRetryAsync(http, () =>
        {
            var req = new HttpRequestMessage(method, path);
            // Only send a Cookie header when a session actually exists.
            // Sending an empty "SID=" would mask a bypass case (where the
            // request is meant to succeed with no session) and, with
            // automatic cookie handling enabled, a manual Cookie header can
            // suppress the handler's own stored cookie.
            if (sid.Length > 0) req.Headers.Add("Cookie", "SID=" + sid);
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

        // Documented qBittorrent WebUI API login shapes, kept distinct on
        // purpose: collapsing them is what made a server-logged successful
        // login surface as a credential failure in the Test button.
        // - 403 on login: the client IP is banned after too many failed
        //   attempts, not a per-attempt credential verdict.
        // - 200 + "Fails.": the credential verdict.
        // - 200 + "Ok.": success, and the response should carry an SID
        //   cookie, but see the no-SID case below.
        // - Anything else: an unexpected response (wrong service, reverse
        //   proxy, version difference), not a credential verdict.
        // Only the shape (status, body length, SID present/absent) is ever
        // reported. The body itself, any cookie value, and the credentials
        // are never put in an exception message.
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new ExternalServiceException("qBittorrent login returned HTTP 403. Per the qBittorrent WebUI API this means the client IP is banned after too many failed login attempts; do not keep retrying, and check the ban before re-checking the username and password.");
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"qBittorrent login returned HTTP {(int)response.StatusCode}. This is an unexpected login response, not a username/password failure; check the service URL, reverse proxy, and qBittorrent version.");
        if (text.Equals("Fails.", StringComparison.OrdinalIgnoreCase))
            throw new ExternalServiceException("qBittorrent login failed. Check the username and password in Settings.");
        if (!text.Equals("Ok.", StringComparison.OrdinalIgnoreCase))
            throw new ExternalServiceException($"qBittorrent login returned HTTP 200 with an unexpected body ({text.Length} bytes, neither 'Ok.' nor 'Fails.'). This is not treated as a username/password failure; check whether the URL points at qBittorrent, a reverse proxy, or a different service.");

        var cookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith("SID=", StringComparison.Ordinal))
            : null;
        if (cookie is null || cookie.Length <= "SID=".Length)
        {
            // "Ok." with no SID is not a credential failure and must not
            // be reported as one. It happens when qBittorrent bypasses
            // authentication for this source IP (localhost / whitelisted
            // subnet), and it is also exactly what the client sees when a
            // production HTTP handler with automatic cookie handling
            // consumes Set-Cookie instead of exposing it, even though the
            // server logged a successful login. Validation is not weakened
            // here: the following authenticated request is the proof. If
            // it succeeds, the connection genuinely works; if it returns
            // 403, SendAsync reports that post-login 403 distinctly.
            _sessions[client.Id] = "";
            _loginsWithoutSid[client.Id] = true;
            return;
        }
        _loginsWithoutSid.TryRemove(client.Id, out _);
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
