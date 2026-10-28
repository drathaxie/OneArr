using System.Globalization;
using System.Xml.Linq;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Http;
using OneArr.Infrastructure.Security;

namespace OneArr.Infrastructure.Providers;

/// <summary>Generic Torznab client (also works for Newznab-style RSS with enclosure links). Parses RSS and torznab:attr fields.</summary>
public sealed class TorznabClient : IIndexerClient
{
    private static readonly XNamespace Torznab = "http://torznab.com/schemas/2015/feed";
    private readonly IHttpClientFactory _factory;

    public TorznabClient(IHttpClientFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Nyaa's public RSS feed is a native indexer, not a Torznab API.</summary>
    public static bool IsDirectNyaa(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("nyaa.si", StringComparison.OrdinalIgnoreCase) &&
        (uri.AbsolutePath is "/" or "");

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(IndexerConfig indexer, ReleaseSearch search, CancellationToken ct)
    {
        var baseUri = UrlGuard.RequireServiceUrl(indexer.BaseUrl);
        if (IsDirectNyaa(baseUri))
            return await SearchNyaaAsync(indexer.Name, search, ct);
        var parameters = new List<string>
        {
            "t=" + (search.Type == MediaType.Series && search.Season is not null ? "tvsearch" : "search"),
            "q=" + Uri.EscapeDataString(search.Title),
            "cat=" + Uri.EscapeDataString(search.Category),
            "apikey=" + Uri.EscapeDataString(indexer.ApiKey)
        };
        if (search.Type == MediaType.Series && search.Season is int s) parameters.Add("season=" + s);
        if (search.Type == MediaType.Series && search.Episode is int ep) parameters.Add("ep=" + ep);

        var relative = "api?" + string.Join("&", parameters);
        var client = _factory.CreateClient(OutboundHttp.TorznabClient);
        client.BaseAddress = new Uri(baseUri, "/");

        using var response = await OutboundHttp.SendWithRetryAsync(client, () => new HttpRequestMessage(HttpMethod.Get, relative), ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new ExternalServiceException($"Indexer '{indexer.Name}' is rate limiting; try again later");
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"Indexer '{indexer.Name}' returned HTTP {(int)response.StatusCode}");

        return Parse(body, indexer.Name);
    }

    private async Task<IReadOnlyList<ReleaseCandidate>> SearchNyaaAsync(string name, ReleaseSearch search, CancellationToken ct)
    {
        var client = _factory.CreateClient(OutboundHttp.TorznabClient);
        client.BaseAddress = new Uri("https://nyaa.si/");
        var url = "?page=rss&q=" + Uri.EscapeDataString(search.Title);
        using var response = await OutboundHttp.SendWithRetryAsync(client,
            () => new HttpRequestMessage(HttpMethod.Get, url), ct);
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"Nyaa RSS returned HTTP {(int)response.StatusCode}");
        return ParseNyaaRss(await response.Content.ReadAsStringAsync(ct), name);
    }

    /// <summary>Parse Nyaa's read-only RSS response, validating torrent download URLs.</summary>
    public static IReadOnlyList<ReleaseCandidate> ParseNyaaRss(string xml, string indexerName)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml, LoadOptions.None); }
        catch (System.Xml.XmlException) { throw new ExternalServiceException("Nyaa returned invalid RSS XML"); }
        if (doc.Root?.Name.LocalName != "rss" || doc.Root.Element("channel") is null)
            throw new ExternalServiceException("Nyaa did not return an RSS feed");
        XNamespace nyaa = "https://nyaa.si/xmlns/nyaa";
        var items = new List<ReleaseCandidate>();
        foreach (var item in doc.Root.Element("channel")!.Elements("item"))
        {
            var title = item.Element("title")?.Value.Trim();
            var url = item.Element("link")?.Value.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(title) || !UrlGuard.IsAllowedDownloadLink(url)) continue;
            var hash = item.Element(nyaa + "infoHash")?.Value.Trim();
            var seedText = item.Element(nyaa + "seeders")?.Value;
            int.TryParse(seedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seeders);
            var sizeText = item.Element(nyaa + "size")?.Value.Trim() ?? "";
            var tokens = sizeText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long size = 0;
            if (tokens.Length > 0 && double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                var power = tokens.Length > 1 ? tokens[1].ToLowerInvariant() switch
                {
                    "kib" or "kb" => 1, "mib" or "mb" => 2, "gib" or "gb" => 3,
                    "tib" or "tb" => 4, _ => 0
                } : 0;
                size = number > 0 && number < long.MaxValue / Math.Pow(1024, power)
                    ? (long)(number * Math.Pow(1024, power)) : 0;
            }
            DateTimeOffset? published = null;
            if (DateTimeOffset.TryParse(item.Element("pubDate")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var date))
                published = date.ToUniversalTime();
            // Nyaa is primarily an anime/TV source. Use the broad TV Torznab
            // category so existing TV search/ranking paths can include results.
            items.Add(new ReleaseCandidate(title, url, size, seeders,
                string.IsNullOrWhiteSpace(hash) ? null : hash.ToLowerInvariant(),
                indexerName, [5000], published));
        }
        return items;
    }

    /// <summary>Parses a Torznab/Newznab RSS feed. Throws ExternalServiceException on invalid XML or an indexer error element.</summary>
    public static IReadOnlyList<ReleaseCandidate> Parse(string xml, string indexerName)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException)
        {
            throw new ExternalServiceException($"Indexer '{indexerName}' returned invalid XML");
        }

        if (doc.Root?.Name.LocalName == "error")
        {
            var desc = doc.Root.Attribute("description")?.Value ?? "unknown error";
            throw new ExternalServiceException($"Indexer '{indexerName}' error: {OutboundHttp.Redact(desc)}");
        }
        if (doc.Root?.Name.LocalName != "rss")
            throw new ExternalServiceException($"Indexer '{indexerName}' did not return an RSS feed");

        var results = new List<ReleaseCandidate>();
        foreach (var item in doc.Root.Descendants("item"))
        {
            var title = item.Element("title")?.Value?.Trim();
            if (string.IsNullOrEmpty(title)) continue;

            var attrs = item.Elements(Torznab + "attr")
                .GroupBy(a => a.Attribute("name")?.Value ?? "")
                .ToDictionary(g => g.Key, g => g.Last().Attribute("value")?.Value ?? "", StringComparer.OrdinalIgnoreCase);

            long size = 0;
            var sizeText = attrs.GetValueOrDefault("size") ?? item.Element("size")?.Value ?? item.Element("enclosure")?.Attribute("length")?.Value;
            long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out size);

            int seeders = 0;
            int.TryParse(attrs.GetValueOrDefault("seeders"), NumberStyles.Integer, CultureInfo.InvariantCulture, out seeders);

            var categories = item.Elements("category").Select(c => c.Value).Concat(attrs.Where(kv => kv.Key == "category").Select(kv => kv.Value))
                .Select(v => int.TryParse(v, out var n) ? n : -1).Where(n => n >= 0).Distinct().ToList();

            var link = attrs.GetValueOrDefault("magneturl")
                       ?? item.Element("enclosure")?.Attribute("url")?.Value
                       ?? item.Element("link")?.Value
                       ?? "";
            if (!UrlGuard.IsAllowedDownloadLink(link)) link = "";

            var hash = attrs.GetValueOrDefault("infohash");
            // Age comes from the feed's pubDate when present; naive dates are read as UTC so age
            // never depends on the server's local timezone. Absent or unparsable means unknown.
            DateTimeOffset? published = null;
            var pubDate = item.Element("pubDate")?.Value?.Trim();
            if (!string.IsNullOrEmpty(pubDate) && DateTimeOffset.TryParse(pubDate, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                published = parsed.ToUniversalTime();
            results.Add(new ReleaseCandidate(title, link, size, seeders, string.IsNullOrWhiteSpace(hash) ? null : hash.ToLowerInvariant(), indexerName, categories, published));
        }
        return results;
    }

    /// <summary>Parses a Torznab caps document. Returns top-level categories and which search types the indexer offers.</summary>
    public static TorznabCaps ParseCaps(string xml, string indexerName)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException)
        {
            throw new ExternalServiceException($"Indexer '{indexerName}' returned invalid XML");
        }

        if (doc.Root?.Name.LocalName != "caps")
            throw new ExternalServiceException($"Indexer '{indexerName}' did not return a caps document");

        var categories = new List<TorznabCategory>();
        foreach (var c in doc.Descendants("category"))
        {
            if (!int.TryParse(c.Attribute("id")?.Value, out var id)) continue;
            categories.Add(new TorznabCategory(id, c.Attribute("name")?.Value ?? ""));
        }

        var searching = doc.Root.Element("searching");
        bool Available(string name) => searching?.Element(name)?.Attribute("available")?.Value == "yes";
        return new TorznabCaps(categories, Available("search"), Available("tv-search"), Available("movie-search"));
    }
}

public sealed record TorznabCategory(int Id, string Name);
public sealed record TorznabCaps(IReadOnlyList<TorznabCategory> Categories, bool SearchAvailable, bool TvSearchAvailable, bool MovieSearchAvailable);
