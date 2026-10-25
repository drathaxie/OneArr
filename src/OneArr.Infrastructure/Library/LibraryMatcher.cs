using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OneArr.Domain;

namespace OneArr.Infrastructure.Library;

public sealed record CandidateFile(string FullPath, string RelativePath, long SizeBytes);

public sealed record CatalogMovie(int Id, string Title, int? Year, string? TmdbId, string? LegacyKey);

public sealed record CatalogEpisode(int Id, int SeasonNumber, int EpisodeNumber);

public sealed record CatalogSeries(int Id, string Title, int? Year, string? TmdbId, string? LegacyKey, IReadOnlyList<CatalogEpisode> Episodes);

/// <summary>Lookup tables built once per scan, so matching a file never walks the whole catalog.</summary>
public sealed class CatalogIndex
{
    public CatalogIndex(IReadOnlyList<CatalogMovie> movies, IReadOnlyList<CatalogSeries> series, bool windows)
    {
        Windows = windows;
        foreach (var m in movies)
        {
            if (m.LegacyKey is not null) Add(MovieLegacy, m.LegacyKey, m);
            if (m.TmdbId is not null) Add(MovieByTmdb, m.TmdbId, m);
            if (m.Year is int y) Add(MovieByTitleYear, LibraryMatcher.TitleYearKey(m.Title, y), m);
        }
        foreach (var s in series)
        {
            if (s.LegacyKey is not null) Add(SeriesLegacy, s.LegacyKey, s);
            if (s.TmdbId is not null) Add(SeriesByTmdb, s.TmdbId, s);
            Add(SeriesByTitle, LibraryMatcher.NormalizeTitle(s.Title), s);
        }
    }

    public bool Windows { get; }
    public Dictionary<string, List<CatalogMovie>> MovieLegacy { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<CatalogSeries>> SeriesLegacy { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<CatalogMovie>> MovieByTmdb { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<CatalogSeries>> SeriesByTmdb { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<CatalogMovie>> MovieByTitleYear { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<CatalogSeries>> SeriesByTitle { get; } = new(StringComparer.Ordinal);

    /// <summary>The key itself and every ancestor folder key. A legacy folder matches any file inside it.</summary>
    public IEnumerable<string> AncestorsOf(string key)
    {
        yield return key;
        var sep = Windows ? '\\' : '/';
        for (var i = key.Length - 1; i > 0; i--)
        {
            if (key[i] == sep && i != key.Length - 1) yield return key[..i];
        }
    }

    private static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(value);
    }
}

public enum MatchOutcome { Linked, Ambiguous, Unmatched }

/// <summary>The result for one file. Only <see cref="MatchOutcome.Linked"/> may ever change the catalog.</summary>
public sealed record FileMatch(
    CandidateFile File,
    MatchOutcome Outcome,
    string Reason,
    MediaType Type,
    int? MediaItemId = null,
    IReadOnlyList<int>? EpisodeIds = null,
    IReadOnlyList<int>? CandidateIds = null,
    string? Method = null,
    int? Resolution = null);

/// <summary>
/// Identifies a file from its path alone. Rules, strongest first: the catalog's known legacy folder, a TMDb id in the path,
/// then title and year. Anything that fits more than one catalog entry is reported as ambiguous, never guessed.
/// </summary>
public static partial class LibraryMatcher
{
    private const int MaxEpisodeRange = 50;

    /// <summary>Convenience overload that builds the index for one call. Large scans should build a <see cref="CatalogIndex"/> once.</summary>
    public static FileMatch Match(CandidateFile file, MediaType type, IReadOnlyList<CatalogMovie> movies, IReadOnlyList<CatalogSeries> series, bool windows) =>
        Match(file, type, new CatalogIndex(movies, series, windows));

    /// <summary>
    /// Lookups are indexed, so each file costs about its path depth plus the size of its own candidate list.
    /// Work does not grow with catalog size times file count.
    /// </summary>
    public static FileMatch Match(CandidateFile file, MediaType type, CatalogIndex index)
    {
        var segments = file.RelativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var fileName = segments.Length > 0 ? segments[^1] : file.FullPath;
        var folders = segments.Length > 1 ? segments[..^1] : [];
        var fileKey = LibraryPaths.Key(file.FullPath, index.Windows);
        var resolution = ParseResolution(file.RelativePath);

        if (SampleTag().IsMatch(fileName)) return Unmatched(file, type, "Looks like a sample file");

        return type == MediaType.Movie
            ? MatchMovie(file, index, folders, fileName, fileKey, resolution)
            : MatchSeries(file, index, folders, fileName, fileKey, resolution);
    }

    private static FileMatch MatchMovie(CandidateFile file, CatalogIndex index, string[] folders, string fileName, string fileKey, int? resolution)
    {
        var legacy = index.AncestorsOf(fileKey).SelectMany(k => index.MovieLegacy.GetValueOrDefault(k) ?? []).ToList();
        if (legacy.Count == 1) return Linked(file, MediaType.Movie, legacy[0].Id, [], "legacy-path", resolution);
        if (legacy.Count > 1) return Ambiguous(file, MediaType.Movie, "Several movies share this folder", legacy.Select(m => m.Id).ToList());

        var tmdb = ParseTmdb(file.RelativePath, out var conflict);
        if (conflict) return Unmatched(file, MediaType.Movie, "The path names more than one TMDb id");
        if (tmdb is not null)
        {
            var byId = index.MovieByTmdb.GetValueOrDefault(tmdb) ?? [];
            if (byId.Count == 1) return Linked(file, MediaType.Movie, byId[0].Id, [], "tmdb-id", resolution);
            if (byId.Count > 1) return Ambiguous(file, MediaType.Movie, $"TMDb id {tmdb} matches several movies", byId.Select(m => m.Id).ToList());
            return Unmatched(file, MediaType.Movie, $"TMDb id {tmdb} is not in the catalog");
        }

        var source = folders.Length > 0 && ParseTitle(folders[^1]).Year is not null ? folders[^1] : fileName;
        var parsed = ParseTitle(source);
        if (parsed.Year is not int year) return Unmatched(file, MediaType.Movie, "No year in the folder or file name, so the movie cannot be identified safely");

        var candidates = index.MovieByTitleYear.GetValueOrDefault(TitleYearKey(parsed.Title, year)) ?? [];
        if (candidates.Count == 1) return Linked(file, MediaType.Movie, candidates[0].Id, [], "title-year", resolution);
        if (candidates.Count > 1) return Ambiguous(file, MediaType.Movie, $"'{parsed.Title} ({year})' matches several movies", candidates.Select(m => m.Id).ToList());
        return Unmatched(file, MediaType.Movie, $"No catalog movie titled '{parsed.Title} ({year})'");
    }

    private static FileMatch MatchSeries(CandidateFile file, CatalogIndex index, string[] folders, string fileName, string fileKey, int? resolution)
    {
        var episodes = ParseEpisodes(fileName);
        if (episodes.Count == 0) return Unmatched(file, MediaType.Series, "No season and episode number (S01E01) in the file name");

        var legacy = index.AncestorsOf(fileKey).SelectMany(k => index.SeriesLegacy.GetValueOrDefault(k) ?? []).ToList();
        IReadOnlyList<CatalogSeries> candidates = legacy;
        string method = "legacy-path";

        if (legacy.Count == 0)
        {
            var tmdb = ParseTmdb(file.RelativePath, out var conflict);
            if (conflict) return Unmatched(file, MediaType.Series, "The path names more than one TMDb id");
            if (tmdb is not null)
            {
                candidates = index.SeriesByTmdb.GetValueOrDefault(tmdb) ?? [];
                method = "tmdb-id";
                if (candidates.Count == 0) return Unmatched(file, MediaType.Series, $"TMDb id {tmdb} is not in the catalog");
            }
            else
            {
                var source = folders.Length > 0 ? folders[0] : TitleBeforeEpisode(fileName);
                var parsed = ParseTitle(source);
                if (string.IsNullOrWhiteSpace(parsed.Title)) return Unmatched(file, MediaType.Series, "No series title found in the path");
                var sameTitle = index.SeriesByTitle.GetValueOrDefault(NormalizeTitle(parsed.Title)) ?? [];
                candidates = sameTitle.Where(s => parsed.Year is null || s.Year == parsed.Year).ToList();
                method = parsed.Year is null ? "title-only" : "title-year";
                if (candidates.Count == 0) return Unmatched(file, MediaType.Series, $"No catalog series titled '{parsed.Title}'");
            }
        }

        if (candidates.Count > 1) return Ambiguous(file, MediaType.Series, "The series name matches more than one catalog entry", candidates.Select(s => s.Id).ToList());

        var show = candidates[0];
        var ids = new List<int>();
        foreach (var (season, number) in episodes)
        {
            var ep = show.Episodes.FirstOrDefault(e => e.SeasonNumber == season && e.EpisodeNumber == number);
            if (ep is null) return Unmatched(file, MediaType.Series, $"S{season:D2}E{number:D2} is not in the catalog for '{show.Title}'");
            ids.Add(ep.Id);
        }
        return Linked(file, MediaType.Series, show.Id, ids, method, resolution);
    }

    internal static string TitleYearKey(string title, int year) => $"{NormalizeTitle(title)}|{year}";

    public static string NormalizeTitle(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Title and year from a folder or file name such as "Dune (2021) {tmdb-438631}" or "Dune.2021.1080p.mkv".</summary>
    public static ParsedTitle ParseTitle(string name)
    {
        var text = Path.GetFileNameWithoutExtension(name);
        var parenYear = ParenYear().Match(text);
        Match? year = parenYear.Success ? parenYear : LastBareYear().Matches(text).LastOrDefault();
        var cut = year?.Index ?? text.Length;
        var resolution = ResolutionTag().Match(text);
        if (year is null && resolution.Success) cut = resolution.Index;
        var title = Clean(text[..cut]);
        return new ParsedTitle(title, year is null ? null : int.Parse(year.Groups["y"].Value, CultureInfo.InvariantCulture));
    }

    public static string? ParseTmdb(string path, out bool conflict)
    {
        var ids = TmdbTag().Matches(path).Select(m => m.Groups["id"].Value).Distinct().ToList();
        conflict = ids.Count > 1;
        return ids.Count == 1 ? ids[0] : null;
    }

    public static int? ParseResolution(string text)
    {
        var m = ResolutionTag().Match(text);
        if (m.Success) return int.Parse(m.Groups["r"].Value, CultureInfo.InvariantCulture);
        return FourK().IsMatch(text) ? 2160 : null;
    }

    /// <summary>Season and episode pairs. Supports S01E02, S01E02E03, S01E02-E03, S01E01-03 and 1x02.</summary>
    public static List<(int Season, int Number)> ParseEpisodes(string fileName)
    {
        var result = new List<(int, int)>();
        var m = EpisodeTag().Match(fileName);
        if (!m.Success)
        {
            m = AltEpisodeTag().Match(fileName);
            if (!m.Success) return result;
            result.Add((int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture)));
            return result;
        }

        var season = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
        var first = int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture);
        var tail = m.Groups["tail"].Value;
        result.Add((season, first));

        if (tail.StartsWith('-') && !tail.Contains('E', StringComparison.OrdinalIgnoreCase))
        {
            var last = int.Parse(tail[1..], CultureInfo.InvariantCulture);
            if (last > first && last - first <= MaxEpisodeRange)
                for (var n = first + 1; n <= last; n++) result.Add((season, n));
            return result;
        }

        foreach (Match n in Digits().Matches(tail))
            result.Add((season, int.Parse(n.Value, CultureInfo.InvariantCulture)));
        return result;
    }

    private static string TitleBeforeEpisode(string fileName)
    {
        var m = EpisodeTag().Match(fileName);
        var alt = AltEpisodeTag().Match(fileName);
        var at = m.Success ? m.Index : alt.Success ? alt.Index : fileName.Length;
        return fileName[..at];
    }

    private static string Clean(string text)
    {
        var t = BracketGroups().Replace(text, " ");
        t = t.Replace('.', ' ').Replace('_', ' ');
        return string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static FileMatch Linked(CandidateFile file, MediaType type, int itemId, IReadOnlyList<int> episodeIds, string method, int? resolution) =>
        new(file, MatchOutcome.Linked, "Matched by " + method, type, itemId, episodeIds, null, method, resolution);

    private static FileMatch Ambiguous(CandidateFile file, MediaType type, string reason, IReadOnlyList<int> candidates) =>
        new(file, MatchOutcome.Ambiguous, reason, type, CandidateIds: candidates);

    private static FileMatch Unmatched(CandidateFile file, MediaType type, string reason) =>
        new(file, MatchOutcome.Unmatched, reason, type);

    public sealed record ParsedTitle(string Title, int? Year);

    [GeneratedRegex(@"(?i)(?<![a-z0-9])sample(?![a-z0-9])")]
    private static partial Regex SampleTag();

    [GeneratedRegex(@"\((?<y>(?:19|20)\d{2})\)")]
    private static partial Regex ParenYear();

    [GeneratedRegex(@"(?<![\d])(?<y>(?:19|20)\d{2})(?![\d])")]
    private static partial Regex LastBareYear();

    [GeneratedRegex(@"(?i)(?<![a-z0-9])(?<r>2160|1080|720|480)p(?![a-z0-9])")]
    private static partial Regex ResolutionTag();

    [GeneratedRegex(@"(?i)(?<![a-z0-9])4k(?![a-z0-9])")]
    private static partial Regex FourK();

    [GeneratedRegex(@"(?i)tmdb(?:id)?[-=](?<id>\d+)")]
    private static partial Regex TmdbTag();

    [GeneratedRegex(@"(?i)(?<![a-z0-9])S(?<s>\d{1,2})E(?<e>\d{1,3})(?<tail>-\d{1,3}(?!\d)|(?:[-_]?E\d{1,3})+)?(?![a-z0-9])")]
    private static partial Regex EpisodeTag();

    [GeneratedRegex(@"(?i)(?<![a-z0-9])(?<s>\d{1,2})x(?<e>\d{1,3})(?![a-z0-9])")]
    private static partial Regex AltEpisodeTag();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}|\([^)]*\)")]
    private static partial Regex BracketGroups();
}
