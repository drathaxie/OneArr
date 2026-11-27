using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Persistence;

namespace OneArr.Infrastructure.Services;

public sealed record ListPreviewItem(string Type, string ProviderId, string Title, int? Year, bool InCatalog, string? Source = null);
public sealed record ListPreview(IReadOnlyList<ListPreviewItem> Items, IReadOnlyList<string> NotFound);
public sealed record ListAddItem(string Type, string ProviderId);
public sealed record ListAddResult(int Added, int AlreadyThere, IReadOnlyList<string> Errors);

/// <summary>
/// One-off import of a TMDb list, or of IMDb ids (an IMDb list export, or ids pasted one per line). Preview first, then add
/// only the chosen titles with the default root folder and quality profile for each type. Nothing is added on a schedule.
/// </summary>
public sealed partial class ListImportService
{
    public const int MaxImdbIds = 300;
    private readonly IDbContextFactory<OneArrDbContext> _db;
    private readonly IMetadataProvider _metadata;
    private readonly MediaService _media;
    private readonly SettingsStore _settings;

    public ListImportService(IDbContextFactory<OneArrDbContext> db, IMetadataProvider metadata, MediaService media, SettingsStore settings)
    {
        _db = db;
        _metadata = metadata;
        _media = media;
        _settings = settings;
    }

    /// <summary>"8312345", "https://www.themoviedb.org/list/8312345-best-of" → "8312345".</summary>
    public static string? TmdbListId(string value)
    {
        var m = TmdbListRx().Match(value.Trim());
        return m.Success ? m.Groups["id"].Value : null;
    }

    public static IReadOnlyList<string> ImdbIds(string text) =>
        ImdbRx().Matches(text).Select(m => m.Value).Distinct().ToList();

    public async Task<ListPreview> PreviewAsync(string source, string value, CancellationToken ct)
    {
        List<ListItem> items;
        var notFound = new List<string>();
        if (source == "tmdb")
        {
            var id = TmdbListId(value) ?? throw new ArgumentException("Give a TMDb list number or its themoviedb.org/list/… address");
            items = (await _metadata.GetListAsync(id, ct)).ToList();
        }
        else if (source == "imdb")
        {
            var ids = ImdbIds(value);
            if (ids.Count == 0) throw new ArgumentException("No IMDb ids (tt1234567) found. Paste an IMDb list export or ids, one per line");
            if (ids.Count > MaxImdbIds) throw new ArgumentException($"At most {MaxImdbIds} IMDb ids at a time");
            items = [];
            foreach (var imdb in ids)
            {
                ct.ThrowIfCancellationRequested();
                if (await _metadata.FindByImdbAsync(imdb, ct) is { } found) items.Add(found);
                else notFound.Add(imdb);
            }
        }
        else throw new ArgumentException("Source must be tmdb or imdb");

        await using var db = await _db.CreateDbContextAsync(ct);
        var have = (await db.MediaItems.AsNoTracking().Select(m => new { m.MediaType, m.ProviderId }).ToListAsync(ct))
            .Select(m => (m.MediaType, m.ProviderId)).ToHashSet();
        var preview = items.DistinctBy(i => (i.Type, i.ProviderId))
            .Select(i => new ListPreviewItem(i.Type == MediaType.Movie ? "movie" : "series", i.ProviderId, i.Title, i.Year, have.Contains((i.Type, i.ProviderId))))
            .ToList();
        return new ListPreview(preview, notFound);
    }

    public async Task<ListAddResult> AddAsync(IReadOnlyList<ListAddItem> items, bool monitored, CancellationToken ct)
    {
        if (items.Count is 0 or > 500) throw new ArgumentException("Choose between 1 and 500 titles");
        var movieRoot = await _settings.GetAsync(SettingKeys.DefaultMovieRoot, ct);
        var seriesRoot = await _settings.GetAsync(SettingKeys.DefaultSeriesRoot, ct);
        var movieProfile = ProfileId(await _settings.GetAsync(SettingKeys.DefaultMovieProfile, ct));
        var seriesProfile = ProfileId(await _settings.GetAsync(SettingKeys.DefaultSeriesProfile, ct));
        int added = 0, there = 0;
        var errors = new List<string>();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var type = item.Type == "movie" ? MediaType.Movie : item.Type == "series" ? MediaType.Series : throw new ArgumentException("Type must be movie or series");
            var root = type == MediaType.Movie ? movieRoot : seriesRoot;
            var profile = type == MediaType.Movie ? movieProfile : seriesProfile;
            if (string.IsNullOrWhiteSpace(root) || profile is null)
            {
                errors.Add($"{item.Type} {item.ProviderId}: set a default {item.Type} root folder and quality profile in Settings first");
                continue;
            }
            try
            {
                var result = await _media.AddAsync(new AddMediaRequest(type, item.ProviderId, profile.Value, root, monitored, null, MonitorAllSeasons: true), ct);
                if (result.Created) added++; else there++;
            }
            catch (Exception ex) when (ex is ArgumentException or ExternalServiceException or ProviderNotConfiguredException)
            {
                errors.Add($"{item.Type} {item.ProviderId}: {ex.Message}");
            }
        }
        return new ListAddResult(added, there, errors);
    }

    private static int? ProfileId(string? value) => int.TryParse(value, out var id) ? id : null;

    [GeneratedRegex(@"^(?:https?://(?:www\.)?themoviedb\.org/list/)?(?<id>\d{1,10})(?:[-/?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TmdbListRx();

    [GeneratedRegex(@"tt\d{5,10}")]
    private static partial Regex ImdbRx();
}
