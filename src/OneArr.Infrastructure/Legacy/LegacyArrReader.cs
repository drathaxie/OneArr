using System.Text.Json;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Http;
using OneArr.Infrastructure.Security;

namespace OneArr.Infrastructure.Legacy;

/// <summary>
/// Read-only Sonarr v3 (/api/v3/series) and Radarr v3 (/api/v3/movie) reader. Issues GET requests only and
/// never writes to the legacy application, its database or its files.
/// </summary>
public sealed class LegacyArrReader : ILegacyArrReader
{
    private readonly IHttpClientFactory _factory;

    public LegacyArrReader(IHttpClientFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<LegacyItem>> ReadAsync(LegacyArrSource source, CancellationToken ct)
    {
        var kind = source.Kind.ToLowerInvariant();
        if (kind is not ("sonarr" or "radarr")) throw new ExternalServiceException("Unsupported legacy source; use sonarr or radarr");
        var baseUri = UrlGuard.RequireServiceUrl(source.BaseUrl);
        var client = _factory.CreateClient(OutboundHttp.LegacyClient);
        client.BaseAddress = new Uri(baseUri, "/");

        var path = kind == "sonarr" ? "api/v3/series" : "api/v3/movie";
        using var response = await OutboundHttp.SendWithRetryAsync(client, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Add("X-Api-Key", source.ApiKey);
            req.Headers.Accept.ParseAdd("application/json");
            return req;
        }, ct);

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new ExternalServiceException($"{kind} rejected the API key");
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"{kind} returned HTTP {(int)response.StatusCode}");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new ExternalServiceException($"{kind} returned an unexpected payload");

        var items = new List<LegacyItem>();
        var type = kind == "sonarr" ? MediaType.Series : MediaType.Movie;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            try
            {
                var title = Str(e, "title");
                if (string.IsNullOrWhiteSpace(title)) throw new FormatException("missing title");
                var tmdb = Int(e, "tmdbId");
                var seasons = new List<LegacySeason>();
                if (e.TryGetProperty("seasons", out var seasonsJson) && seasonsJson.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in seasonsJson.EnumerateArray())
                    {
                        if (Int(s, "seasonNumber") is int n)
                            seasons.Add(new LegacySeason(n, Bool(s, "monitored")));
                    }
                }
                if (tmdb is null)
                {
                    items.Add(new LegacyItem(kind, type, title, Int(e, "year"), null, Int(e, "tvdbId"), Str(e, "imdbId"), Str(e, "path"), Bool(e, "monitored"),
                        "No TMDb id on this item; cannot map it to the TMDb catalog", seasons));
                    continue;
                }
                items.Add(new LegacyItem(kind, type, title, Int(e, "year"), tmdb, Int(e, "tvdbId"), Str(e, "imdbId"), Str(e, "path"), Bool(e, "monitored"), null, seasons));
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                items.Add(new LegacyItem(kind, type, "(unreadable item)", null, null, null, null, null, false, "Malformed legacy record: " + ex.Message));
            }
        }
        return items;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();
}
