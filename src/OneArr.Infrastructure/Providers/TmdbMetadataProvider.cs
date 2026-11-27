using System.Net;
using System.Text.Json;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Http;

namespace OneArr.Infrastructure.Providers;

/// <summary>
/// TMDb metadata. The key is read per call from a supplier (settings store), so changing it in the UI takes effect
/// immediately. Keys are sent as query parameters or bearer tokens, never logged or returned.
/// </summary>
public sealed class TmdbMetadataProvider : IMetadataProvider
{
    public static readonly Uri DefaultBaseUri = new("https://api.themoviedb.org/3/");
    private readonly IHttpClientFactory _factory;
    private readonly Func<Task<string?>> _apiKey;
    private readonly Uri _baseUri;

    /// <param name="baseUri">Override only for fixture or local fake servers (ONEARR_TMDB_BASE_URL); production uses the TMDb default.</param>
    public TmdbMetadataProvider(IHttpClientFactory factory, Func<Task<string?>> apiKey, Uri? baseUri = null)
    {
        _factory = factory;
        _apiKey = apiKey;
        _baseUri = baseUri ?? DefaultBaseUri;
    }

    public async Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(MediaType type, string query, CancellationToken ct)
    {
        var json = await GetJsonAsync(type == MediaType.Movie ? "search/movie" : "search/tv", ct, ("query", query), ("include_adult", "false"));
        var list = new List<MetadataSearchResult>();
        if (!json.TryGetProperty("results", out var results)) return list;
        foreach (var r in results.EnumerateArray())
        {
            list.Add(new MetadataSearchResult(
                r.GetProperty("id").GetInt32().ToString(),
                TitleOf(r, type),
                YearOf(r, type),
                Str(r, "overview"),
                Str(r, "poster_path")));
        }
        return list;
    }

    public async Task<MetadataDetails> GetDetailsAsync(MediaType type, string providerId, CancellationToken ct)
    {
        var id = ValidId(providerId);
        // One request: alternative titles (both types) and, for movies, release dates by country.
        var json = await GetJsonAsync(type == MediaType.Movie ? $"movie/{id}" : $"tv/{id}", ct,
            ("append_to_response", type == MediaType.Movie ? "alternative_titles,release_dates" : "alternative_titles"));
        string? imdb = Str(json, "imdb_id");
        int? tvdb = null;
        if (type == MediaType.Series)
        {
            var ext = await GetJsonAsync($"tv/{id}/external_ids", ct);
            imdb = Str(ext, "imdb_id");
            if (ext.TryGetProperty("tvdb_id", out var tv) && tv.ValueKind == JsonValueKind.Number) tvdb = tv.GetInt32();
        }
        var seasons = type == MediaType.Series && json.TryGetProperty("number_of_seasons", out var n) ? n.GetInt32() : 0;
        var title = TitleOf(json, type);
        return new MetadataDetails(id, title, YearOf(json, type), Str(json, "overview"), Str(json, "poster_path"), imdb, tvdb, seasons,
            AlternateTitlesOf(json, type, title), type == MediaType.Movie ? ReleaseDatesOf(json) : null);
    }

    /// <summary>
    /// The original title plus TMDb's alternative titles, for matching release names. Titles that reduce to nothing comparable
    /// (for example in a non-Latin script, which release names rarely use) are dropped, as are duplicates of the main title.
    /// </summary>
    public static IReadOnlyList<string> AlternateTitlesOf(JsonElement json, MediaType type, string title)
    {
        var names = new List<string>();
        if (Str(json, type == MediaType.Movie ? "original_title" : "original_name") is { } original) names.Add(original);
        if (json.TryGetProperty("alternative_titles", out var alt) && alt.ValueKind == JsonValueKind.Object
            && alt.TryGetProperty(type == MediaType.Movie ? "titles" : "results", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var t in list.EnumerateArray())
                if (Str(t, "title") is { } name) names.Add(name);
        var mainKey = ReleaseParser.NormalizeTitle(title);
        return names
            .Select(n => n.Trim())
            .Where(n => ReleaseParser.NormalizeTitle(n) is { Length: > 1 } key && key != mainKey && key.All(char.IsAscii))
            .DistinctBy(ReleaseParser.NormalizeTitle)
            .Take(25)
            .ToList();
    }

    /// <summary>Earliest theatrical (types 2-3), digital (4) and physical (5) release across all countries.</summary>
    public static MovieReleaseDates ReleaseDatesOf(JsonElement json)
    {
        DateOnly? theatrical = null, digital = null, physical = null;
        if (json.TryGetProperty("release_dates", out var rd) && rd.TryGetProperty("results", out var countries) && countries.ValueKind == JsonValueKind.Array)
            foreach (var country in countries.EnumerateArray())
            {
                if (!country.TryGetProperty("release_dates", out var dates) || dates.ValueKind != JsonValueKind.Array) continue;
                foreach (var d in dates.EnumerateArray())
                {
                    if (!d.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.Number) continue;
                    if (Str(d, "release_date") is not { Length: >= 10 } raw || !DateOnly.TryParse(raw[..10], System.Globalization.CultureInfo.InvariantCulture, out var date)) continue;
                    switch (t.GetInt32())
                    {
                        case 2 or 3: theatrical = Min(theatrical, date); break;
                        case 4: digital = Min(digital, date); break;
                        case 5: physical = Min(physical, date); break;
                    }
                }
            }
        return new MovieReleaseDates(theatrical, digital, physical);
    }

    private static DateOnly Min(DateOnly? a, DateOnly b) => a is DateOnly x && x < b ? x : b;

    public async Task<IReadOnlyList<SeasonInfo>> GetSeasonsAsync(string providerId, CancellationToken ct)
    {
        var id = ValidId(providerId);
        var show = await GetJsonAsync($"tv/{id}", ct);
        var result = new List<SeasonInfo>();
        if (!show.TryGetProperty("seasons", out var seasons)) return result;
        foreach (var s in seasons.EnumerateArray())
        {
            var number = s.GetProperty("season_number").GetInt32();
            // Season 0 holds specials. TMDb tracks them, so they are fetched; new specials start unmonitored.
            var season = await GetJsonAsync($"tv/{id}/season/{number}", ct);
            var eps = new List<EpisodeInfo>();
            if (season.TryGetProperty("episodes", out var episodes))
            {
                foreach (var e in episodes.EnumerateArray())
                {
                    DateOnly? air = DateOnly.TryParse(Str(e, "air_date"), out var d) ? d : null;
                    eps.Add(new EpisodeInfo(e.GetProperty("episode_number").GetInt32(), e.GetProperty("id").GetInt32().ToString(), Str(e, "name") ?? "", air));
                }
            }
            result.Add(new SeasonInfo(number, eps));
        }
        return result;
    }

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct, params (string Key, string Value)[] query)
    {
        var key = await _apiKey() ?? throw new ProviderNotConfiguredException(
            "TMDb is not configured. Add a TMDb API key in Settings (free key at themoviedb.org) to search and add titles.");

        var qs = string.Join("&", query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));
        var isBearer = key.StartsWith("eyJ", StringComparison.Ordinal);
        if (!isBearer) qs = (qs.Length > 0 ? qs + "&" : "") + "api_key=" + Uri.EscapeDataString(key);
        var relative = path + (qs.Length > 0 ? "?" + qs : "");

        var client = _factory.CreateClient(OutboundHttp.TmdbClient);
        client.BaseAddress = _baseUri;
        using var response = await OutboundHttp.SendWithRetryAsync(client, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, relative);
            req.Headers.Accept.ParseAdd("application/json");
            if (isBearer) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            return req;
        }, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new ProviderNotConfiguredException("TMDb rejected the configured API key. Check it in Settings.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new ExternalServiceException("TMDb item not found");
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"TMDb returned HTTP {(int)response.StatusCode}");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        try
        {
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ExternalServiceException("TMDb returned invalid JSON", ex);
        }
    }

    private static string ValidId(string id) =>
        id.All(char.IsDigit) && id.Length is > 0 and < 12 ? id : throw new ExternalServiceException("Invalid TMDb id");

    private static string TitleOf(JsonElement e, MediaType type) =>
        Str(e, type == MediaType.Movie ? "title" : "name") ?? "";

    private static int? YearOf(JsonElement e, MediaType type)
    {
        var date = Str(e, type == MediaType.Movie ? "release_date" : "first_air_date");
        return date is { Length: >= 4 } && int.TryParse(date[..4], out var y) ? y : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
