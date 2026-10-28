using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Http;
using OneArr.Infrastructure.Security;

namespace OneArr.Infrastructure.Providers;

/// <summary>
/// Public RSS is a read-only latest-releases feed, not a searchable API.
/// An administrator explicitly selects RSS as the indexer protocol. Nothing
/// is saved from feed content, and no cookies or site credentials are sent.
/// </summary>
public sealed class PublicRssClient
{
    // Kept in the existing ApiKey column to avoid a destructive/indexer-table
    // migration. This marker is not a credential and must never be sent upstream.
    public const string ProtocolMarker = "rss:public:v1";
    private const int MaxFeedBytes = 4 * 1024 * 1024;
    private readonly IHttpClientFactory _factory;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Expires, IReadOnlyList<ReleaseCandidate> Entries)> _cache = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public PublicRssClient(IHttpClientFactory factory) => _factory = factory;

    public static bool IsPublicRss(string? stored) => stored == ProtocolMarker;

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(IndexerConfig indexer, ReleaseSearch search, CancellationToken ct)
    {
        var releases = await CachedFeedAsync(indexer.BaseUrl, indexer.Name, search.Type, ct);
        // The feed contains only a recent window, unlike a searchable Torznab API.
        // Ranking still performs its normal title/season/episode/quality checks.
        var terms = search.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return releases.Where(r => terms.All(term => r.Title.Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    // OneArr may search many monitored episodes against the same latest-releases
    // RSS feed. Cache only for two minutes so one library sweep does not send
    // a network request for every movie/episode. Explicit Test bypasses cache.
    private async Task<IReadOnlyList<ReleaseCandidate>> CachedFeedAsync(
        string url, string name, MediaType type, CancellationToken ct)
    {
        var key = ((int)type).ToString(CultureInfo.InvariantCulture) + "|" + url;
        if (_cache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Entries;
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out cached) && cached.Expires > DateTimeOffset.UtcNow)
                return cached.Entries;
            var result = await FetchAsync(url, name, type, ct);
            if (_cache.Count > 128)
            {
                foreach (var stale in _cache.Where(x => x.Value.Expires <= DateTimeOffset.UtcNow))
                    _cache.TryRemove(stale.Key, out _);
                if (_cache.Count > 128) _cache.Clear();
            }
            _cache[key] = (DateTimeOffset.UtcNow.AddMinutes(2), result);
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<ReleaseCandidate>> FetchAsync(string url, string name, MediaType mediaType, CancellationToken ct)
    {
        var uri = UrlGuard.RequireServiceUrl(url);
        var http = _factory.CreateClient(OutboundHttp.TorznabClient);
        using var response = await OutboundHttp.SendWithRetryAsync(http,
            () => new HttpRequestMessage(HttpMethod.Get, uri), ct);
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"RSS feed '{name}' returned HTTP {(int)response.StatusCode}");
        if (response.Content.Headers.ContentLength is long length && length > MaxFeedBytes)
            throw new ExternalServiceException($"RSS feed '{name}' exceeds the 4 MiB limit");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, ct);
            if (count == 0) break;
            if (buffer.Length + count > MaxFeedBytes)
                throw new ExternalServiceException($"RSS feed '{name}' exceeds the 4 MiB limit");
            buffer.Write(chunk, 0, count);
        }
        return ParseFeed(Encoding.UTF8.GetString(buffer.ToArray()), name, mediaType);
    }

    public static IReadOnlyList<ReleaseCandidate> ParseFeed(string xml, string name, MediaType mediaType)
    {
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxFeedBytes,
            });
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException)
        {
            throw new ExternalServiceException($"RSS feed '{name}' returned invalid XML");
        }
        if (doc.Root?.Name.LocalName is not ("rss" or "feed"))
            throw new ExternalServiceException($"RSS feed '{name}' did not return RSS or Atom XML");
        if (doc.Root.Name.LocalName == "rss" && doc.Root.Elements().All(e => e.Name.LocalName != "channel"))
            throw new ExternalServiceException($"RSS feed '{name}' has no channel");

        static string? Value(XElement element, string local) =>
            element.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(local, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
        static string? Attr(XElement element, string local) =>
            element.Attributes().FirstOrDefault(a => a.Name.LocalName == local)?.Value;

        var isAtom = doc.Root.Name.LocalName == "feed";
        var rows = isAtom
            ? doc.Root.Elements().Where(e => e.Name.LocalName == "entry")
            : doc.Root.Descendants().Where(e => e.Name.LocalName == "item");
        var results = new List<ReleaseCandidate>();
        foreach (var item in rows.Take(1000))
        {
            var title = Value(item, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;
            var enclosure = item.Elements().FirstOrDefault(e => e.Name.LocalName == "enclosure") ??
                item.Elements().FirstOrDefault(e =>
                    e.Name.LocalName == "link" && string.Equals(Attr(e, "rel"), "enclosure", StringComparison.OrdinalIgnoreCase));
            var link = enclosure is not null ? Attr(enclosure, "url") ?? Attr(enclosure, "href") :
                isAtom ? item.Elements().FirstOrDefault(e => e.Name.LocalName == "link" && Attr(e, "rel") is null)?.Attribute("href")?.Value
                       : Value(item, "link");
            if (string.IsNullOrWhiteSpace(link) || !UrlGuard.IsAllowedDownloadLink(link)) continue;
            // Do not submit links to HTML detail pages to qBittorrent.
            var isTorrent = link.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)
                || (Uri.TryCreate(link, UriKind.Absolute, out var downloadUri) &&
                    downloadUri.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                || string.Equals(Attr(enclosure ?? item, "type"), "application/x-bittorrent", StringComparison.OrdinalIgnoreCase);
            if (!isTorrent) continue;

            var attrs = item.Descendants().Where(e => e.Name.LocalName == "attr").ToList();
            string? TorznabAttr(string key) => attrs.FirstOrDefault(e =>
                string.Equals(Attr(e, "name"), key, StringComparison.OrdinalIgnoreCase))?.Attribute("value")?.Value;
            var hash = Value(item, "infoHash") ?? TorznabAttr("infohash");
            var seedsText = Value(item, "seeders") ?? TorznabAttr("seeders");
            _ = int.TryParse(seedsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seeders);
            var sizeText = Attr(enclosure ?? item, "length") ?? Value(item, "size") ?? TorznabAttr("size");
            var size = ParseSize(sizeText);
            DateTimeOffset? published = null;
            if (DateTimeOffset.TryParse(Value(item, isAtom ? "updated" : "pubDate"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var date)) published = date.ToUniversalTime();
            results.Add(new ReleaseCandidate(title, link, size, Math.Max(0, seeders),
                string.IsNullOrWhiteSpace(hash) ? null : hash.ToLowerInvariant(),
                name, [mediaType == MediaType.Movie ? 2000 : 5000], published));
        }
        return results;
    }

    private static long ParseSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
            return Math.Max(0, bytes);
        var pieces = value.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length != 2 || !double.TryParse(pieces[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            return 0;
        var power = pieces[1].ToLowerInvariant() switch
        {
            "b" => 0, "kb" or "kib" => 1, "mb" or "mib" => 2,
            "gb" or "gib" => 3, "tb" or "tib" => 4, _ => -1,
        };
        if (power < 0 || amount <= 0 || amount >= long.MaxValue / Math.Pow(1024, power)) return 0;
        return (long)(amount * Math.Pow(1024, power));
    }
}
