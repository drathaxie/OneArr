using System.Globalization;
using System.Text.Json;
using OneArr.Application;
using OneArr.Infrastructure.Http;

namespace OneArr.Infrastructure.Providers;

/// <summary>
/// Exact episode air times from TVmaze's public API (no key). Shows are found by TVDB id, then IMDb id; the lookup answers
/// with a redirect to the show, whose id is read from the Location header rather than following it.
/// </summary>
public sealed class TvmazeAirTimeProvider : IAirTimeProvider
{
    public const string ClientName = "tvmaze";
    private static readonly Uri BaseUri = new("https://api.tvmaze.com/");
    private readonly IHttpClientFactory _factory;

    public TvmazeAirTimeProvider(IHttpClientFactory factory) => _factory = factory;

    public async Task<int?> FindShowAsync(int? tvdbId, string? imdbId, CancellationToken ct)
    {
        if (tvdbId is int tvdb && tvdb > 0 && await LookupAsync("thetvdb=" + tvdb.ToString(CultureInfo.InvariantCulture), ct) is int byTvdb)
            return byTvdb;
        if (!string.IsNullOrWhiteSpace(imdbId) && imdbId.StartsWith("tt", StringComparison.Ordinal) && imdbId[2..].All(char.IsAsciiDigit))
            return await LookupAsync("imdb=" + imdbId, ct);
        return null;
    }

    private async Task<int?> LookupAsync(string query, CancellationToken ct)
    {
        using var response = await SendAsync("lookup/shows?" + query, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if ((int)response.StatusCode is >= 300 and < 400)
            return ShowIdFromPath(response.Headers.Location);
        Ensure(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt32() : null;
    }

    /// <summary>"/shows/112" or "https://api.tvmaze.com/shows/112" -> 112.</summary>
    public static int? ShowIdFromPath(Uri? location)
    {
        if (location is null) return null;
        var path = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[0] == "shows" && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    public async Task<IReadOnlyList<EpisodeAirTime>> GetAirTimesAsync(int showId, CancellationToken ct)
    {
        using var response = await SendAsync($"shows/{showId.ToString(CultureInfo.InvariantCulture)}/episodes", ct);
        Ensure(response);
        return ParseEpisodes(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Numbered episodes with an airstamp. Specials without a number are skipped.</summary>
    public static IReadOnlyList<EpisodeAirTime> ParseEpisodes(string body)
    {
        JsonDocument json;
        try { json = JsonDocument.Parse(body); }
        catch (JsonException) { throw new ExternalServiceException("TVmaze returned invalid JSON"); }
        using (json)
        {
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw new ExternalServiceException("TVmaze did not return an episode list");
            var list = new List<EpisodeAirTime>();
            foreach (var e in json.RootElement.EnumerateArray())
            {
                if (!e.TryGetProperty("season", out var s) || s.ValueKind != JsonValueKind.Number) continue;
                if (!e.TryGetProperty("number", out var n) || n.ValueKind != JsonValueKind.Number) continue;
                if (!e.TryGetProperty("airstamp", out var a) || a.ValueKind != JsonValueKind.String) continue;
                if (!DateTimeOffset.TryParse(a.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stamp)) continue;
                list.Add(new EpisodeAirTime(s.GetInt32(), n.GetInt32(), stamp.ToUniversalTime()));
            }
            return list;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string relative, CancellationToken ct)
    {
        var client = _factory.CreateClient(ClientName);
        client.BaseAddress = BaseUri;
        return await OutboundHttp.SendWithRetryAsync(client, () => new HttpRequestMessage(HttpMethod.Get, relative), ct);
    }

    private static void Ensure(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new ExternalServiceException("TVmaze is rate limiting; try again later");
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"TVmaze returned HTTP {(int)response.StatusCode}");
    }
}
