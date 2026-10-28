using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Http;
using OneArr.Infrastructure.Security;

namespace OneArr.Infrastructure.Providers;

/// <summary>
/// Native TorrentLeech support: account login, session cookies, JSON search and
/// an authenticated torrent-file download for qBittorrent multipart upload.
/// This is NOT a general Cardigann interpreter. Unknown definitions stay disabled.
/// </summary>
public sealed class NativeTrackerClient
{
    private readonly IHttpClientFactory _factory;
    private readonly TrackerSecretBox _secrets;
    private readonly ConcurrentDictionary<int, Session> _sessions = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _loginGates = new();
    private sealed record Session(string Cookies, DateTimeOffset ExpiresAt);
    public const string ClientName = "native-tracker";

    public NativeTrackerClient(IHttpClientFactory factory, TrackerSecretBox secrets)
    {
        _factory = factory;
        _secrets = secrets;
    }

    private static Uri ValidateBase(string url)
    {
        var uri = UrlGuard.RequireServiceUrl(url);
        if (!uri.Scheme.Equals("https", StringComparison.Ordinal) ||
            uri.Port != 443 || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ExternalServiceException("TorrentLeech requires its HTTPS website root");
        var host = uri.Host.ToLowerInvariant();
        if (host is not ("www.torrentleech.org" or "www.torrentleech.cc" or
            "www.torrentleech.me" or "www.tleechreload.org" or "www.tlgetin.cc"))
            throw new ExternalServiceException("Unsupported TorrentLeech hostname; credentials will not be sent");
        return uri;
    }

    public static bool IsTorrentLeechDownload(Uri link) =>
        link.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
        Regex.IsMatch(link.AbsolutePath, @"^/download/[0-9]+/[^/]+$", RegexOptions.CultureInvariant) &&
        link.Port == 443 &&
        link.Host.ToLowerInvariant() is ("www.torrentleech.org" or "www.torrentleech.cc" or
            "www.torrentleech.me" or "www.tleechreload.org" or "www.tlgetin.cc");

    private HttpClient NewClient(Uri uri)
    {
        var http = _factory.CreateClient(ClientName);
        http.BaseAddress = uri;
        return http;
    }

    private static void CollectCookies(HttpResponseMessage response, IDictionary<string, string> jar)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
        foreach (var value in values)
        {
            var pair = value.Split(';', 2)[0].Trim();
            var equals = pair.IndexOf('=');
            if (equals < 1) continue;
            var name = pair[..equals];
            if (!Regex.IsMatch(name, @"^[A-Za-z0-9_-]{1,80}$")) continue;
            if (pair.Length > 2048) continue;
            jar[name] = pair[(equals + 1)..];
        }
    }

    private static string CookieHeader(IDictionary<string, string> jar) =>
        string.Join("; ", jar.Select(kv => kv.Key + "=" + kv.Value));

    private static async Task<HttpResponseMessage> RequestAsync(
        HttpClient http, Uri baseUri, string path, HttpMethod method, string? cookie,
        HttpContent? body, CancellationToken ct)
    {
        var target = new Uri(baseUri, path);
        if (!target.Scheme.Equals(baseUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !target.Authority.Equals(baseUri.Authority, StringComparison.OrdinalIgnoreCase))
            throw new ExternalServiceException("Refusing to send tracker credentials to an external host");
        using var request = new HttpRequestMessage(method, target) { Content = body };
        request.Headers.Referrer = baseUri;
        if (!string.IsNullOrEmpty(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static string? HtmlField(string html, string field)
    {
        foreach (Match input in Regex.Matches(html, @"<input\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = input.Value;
            string? Attribute(string key)
            {
                var m = Regex.Match(tag,
                    @"\b" + Regex.Escape(key) + @"\s*=\s*[""'](?<v>[^""']*)[""']",
                    RegexOptions.IgnoreCase);
                return m.Success ? WebUtility.HtmlDecode(m.Groups["v"].Value) : null;
            }
            if (string.Equals(Attribute("name"), field, StringComparison.Ordinal))
                return Attribute("value");
        }
        return null;
    }

    private static Dictionary<string, string> HiddenFields(string html)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match input in Regex.Matches(html, @"<input\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = input.Value;
            if (!Regex.IsMatch(tag, @"\btype\s*=\s*[""']hidden[""']", RegexOptions.IgnoreCase)) continue;
            var match = Regex.Match(tag, @"\bname\s*=\s*[""'](?<n>[^""']+)[""']", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var key = WebUtility.HtmlDecode(match.Groups["n"].Value);
            if (key.Length > 100) continue;
            var value = Regex.Match(tag, @"\bvalue\s*=\s*[""'](?<v>[^""']*)[""']", RegexOptions.IgnoreCase);
            fields[key] = value.Success ? WebUtility.HtmlDecode(value.Groups["v"].Value) : "";
        }
        return fields;
    }

    private async Task<Session> LoginAsync(IndexerConfig indexer, CancellationToken ct)
    {
        var baseUri = ValidateBase(indexer.BaseUrl);
        var config = _secrets.Read(indexer.ApiKey);
        if (config.TrackerId != "torrentleech")
            throw new ExternalServiceException("Unsupported native tracker");
        using var http = NewClient(baseUri);
        var jar = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var loginPage = await RequestAsync(http, baseUri, "user/account/login/", HttpMethod.Get, null, null, ct))
        {
            CollectCookies(loginPage, jar);
            if (!loginPage.IsSuccessStatusCode)
                throw new ExternalServiceException($"TorrentLeech login page returned HTTP {(int)loginPage.StatusCode}");
            var html = await loginPage.Content.ReadAsStringAsync(ct);
            if (html.Length > 2_000_000)
                throw new ExternalServiceException("TorrentLeech returned an unexpectedly large login page");
            var fields = HiddenFields(html);
            fields["username"] = config.Username;
            fields["password"] = config.Password;
            if (!string.IsNullOrWhiteSpace(config.Alt2FaToken))
                fields["alt2FAToken"] = config.Alt2FaToken;
            using var form = new FormUrlEncodedContent(fields);
            using var posted = await RequestAsync(http, baseUri, "user/account/login/",
                HttpMethod.Post, CookieHeader(jar), form, ct);
            CollectCookies(posted, jar);
            if (posted.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new ExternalServiceException($"TorrentLeech login was blocked (HTTP {(int)posted.StatusCode}); check site rate limits, account access or anti-bot challenge");
            if (!posted.IsSuccessStatusCode && (int)posted.StatusCode is not (301 or 302 or 303))
                throw new ExternalServiceException($"TorrentLeech login returned HTTP {(int)posted.StatusCode}");
        }
        // Verify authenticated access rather than trusting a redirect or cookie.
        using var home = await RequestAsync(http, baseUri, "/", HttpMethod.Get, CookieHeader(jar), null, ct);
        CollectCookies(home, jar);
        var homeHtml = await home.Content.ReadAsStringAsync(ct);
        if (!home.IsSuccessStatusCode ||
            !homeHtml.Contains("/user/account/logout", StringComparison.OrdinalIgnoreCase))
            throw new ExternalServiceException("TorrentLeech did not confirm the account login; check credentials, 2FA or site challenge");
        return new Session(CookieHeader(jar), DateTimeOffset.UtcNow.AddMinutes(20));
    }

    private async Task<Session> GetSessionAsync(IndexerConfig indexer, CancellationToken ct)
    {
        ValidateBase(indexer.BaseUrl);
        if (indexer.Id > 0 && _sessions.TryGetValue(indexer.Id, out var cached) &&
            cached.ExpiresAt > DateTimeOffset.UtcNow) return cached;
        var gate = _loginGates.GetOrAdd(indexer.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (indexer.Id > 0 && _sessions.TryGetValue(indexer.Id, out cached) &&
                cached.ExpiresAt > DateTimeOffset.UtcNow) return cached;
            var fresh = await LoginAsync(indexer, ct);
            if (indexer.Id > 0) _sessions[indexer.Id] = fresh;
            return fresh;
        }
        finally { gate.Release(); }
    }

    public async Task<string> TestAsync(IndexerConfig indexer, CancellationToken ct)
    {
        // Using an unsaved ID ensures a real login, not a cached session.
        var isolated = new IndexerConfig { Id = 0, Name = indexer.Name,
            BaseUrl = indexer.BaseUrl, ApiKey = indexer.ApiKey };
        _ = await GetSessionAsync(isolated, ct);
        return "TorrentLeech accepted the account login and returned an authenticated page.";
    }

    private async Task<HttpResponseMessage> AuthGetAsync(IndexerConfig indexer, string path, CancellationToken ct)
    {
        var baseUri = ValidateBase(indexer.BaseUrl);
        var session = await GetSessionAsync(indexer, ct);
        var http = NewClient(baseUri);
        try
        {
            var response = await RequestAsync(http, baseUri, path, HttpMethod.Get, session.Cookies, null, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Found)
            {
                response.Dispose();
                if (indexer.Id > 0) _sessions.TryRemove(indexer.Id, out _);
                var fresh = await GetSessionAsync(indexer, ct);
                response = await RequestAsync(http, baseUri, path, HttpMethod.Get, fresh.Cookies, null, ct);
            }
            return response;
        }
        finally { http.Dispose(); }
    }

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(IndexerConfig indexer, ReleaseSearch search, CancellationToken ct)
    {
        var keyword = search.Title;
        if (search.Type == MediaType.Series && search.Season is int season)
            keyword += search.Episode is int episode ? $" S{season:00}E{episode:00}" : $" S{season:00}";
        var path = search.Type == MediaType.Movie
            ? "torrents/browse/list/categories/8,9,11,12,13,14,15,29,37,43,47/exact/1/query/"
            : "torrents/browse/list/categories/26,27,32,34,35/exact/1/query/";
        path += Uri.EscapeDataString(keyword) + "/orderby/added/order/desc";
        using var response = await AuthGetAsync(indexer, path, ct);
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"TorrentLeech search returned HTTP {(int)response.StatusCode}");
        var data = await response.Content.ReadAsStringAsync(ct);
        if (data.Length > 8_000_000) throw new ExternalServiceException("TorrentLeech search response too large");
        var origin = ValidateBase(indexer.BaseUrl);
        return ParseSearch(data, indexer.Name)
            .Select(item => item with { Link = new Uri(origin, item.Link).AbsoluteUri })
            .ToList();
    }

    public static IReadOnlyList<ReleaseCandidate> ParseSearch(string data, string indexerName)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(data); }
        catch (JsonException) { throw new ExternalServiceException("TorrentLeech returned invalid JSON"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("torrentList", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new ExternalServiceException("TorrentLeech returned an unexpected search response");
            var items = new List<ReleaseCandidate>();
            foreach (var row in list.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                string? Read(string key) => row.TryGetProperty(key, out var v)
                    ? v.ValueKind == JsonValueKind.String ? v.GetString()
                      : v.ValueKind == JsonValueKind.Number ? v.ToString() : null : null;
                var id = Read("fid");
                var file = Read("filename");
                var title = Read("name");
                if (string.IsNullOrWhiteSpace(id) || !id.All(char.IsAsciiDigit) ||
                    string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(title)) continue;
                var category = int.TryParse(Read("categoryID"), out var cat) ? cat : 0;
                var mediaCat = category is 26 or 27 or 32 or 34 or 35 ? 5000 : 2000;
                _ = int.TryParse(Read("seeders"), out var seeders);
                _ = long.TryParse(Read("size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
                DateTimeOffset? date = null;
                if (DateTimeOffset.TryParse(Read("addedTimestamp"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed)) date = parsed.ToUniversalTime();
                var url = "/download/" + id + "/" + Uri.EscapeDataString(file);
                // The caller substitutes the exact trusted configured origin.
                items.Add(new ReleaseCandidate(title, url, size, seeders, null, indexerName, [mediaCat], date));
            }
            return items;
        }
    }

    public async Task<byte[]> DownloadTorrentAsync(IndexerConfig indexer, Uri link, CancellationToken ct)
    {
        var origin = ValidateBase(indexer.BaseUrl);
        if (!IsTorrentLeechDownload(link) || !link.Authority.Equals(origin.Authority, StringComparison.OrdinalIgnoreCase))
            throw new ExternalServiceException("Refusing an untrusted TorrentLeech download URL");
        using var response = await AuthGetAsync(indexer, link.PathAndQuery, ct);
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"TorrentLeech torrent download returned HTTP {(int)response.StatusCode}");
        const int max = 16 * 1024 * 1024;
        if (response.Content.Headers.ContentLength is long length && length > max)
            throw new ExternalServiceException("Torrent file exceeds the 16 MiB safety limit");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, ct);
            if (count == 0) break;
            if (buffer.Length + count > max)
                throw new ExternalServiceException("Torrent file exceeds the 16 MiB safety limit");
            buffer.Write(chunk, 0, count);
        }
        var bytes = buffer.ToArray();
        if (bytes.Length < 5 || bytes[0] != (byte)'d')
            throw new ExternalServiceException("TorrentLeech returned content that is not a torrent file");
        return bytes;
    }
}
