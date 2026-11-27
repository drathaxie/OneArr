using System.Text.RegularExpressions;
using OneArr.Domain;

namespace OneArr.Application;

/// <summary>Deterministic release-name parser covering the cases OneArr v1 needs: resolution, source, year, SxxEyy and multi-episode.</summary>
public sealed partial class ReleaseParser : IReleaseParser
{
    public const string SourceBluRay = "bluray";
    public const string SourceWebDl = "webdl";
    public const string SourceWebRip = "webrip";
    public const string SourceHdtv = "hdtv";
    public const string SourceCam = "cam";
    public const string SourceUnknown = "unknown";

    private static readonly Regex EpisodeRx = new(@"(?i)(?<![a-z0-9])S(?<s>\d{1,2})E(?<e>\d{1,3})(?:(?:-|E|-E)(?<e2>\d{1,3}))?(?![a-z0-9])", RegexOptions.Compiled);
    private static readonly Regex AltEpisodeRx = new(@"(?i)(?<![a-z0-9])(?<s>\d{1,2})x(?<e>\d{1,3})(?![a-z0-9])", RegexOptions.Compiled);
    private static readonly Regex SeasonPackRx = new(@"(?i)(?<![a-z0-9])S(?<s>\d{1,2})(?![a-z0-9])", RegexOptions.Compiled);
    private static readonly Regex YearRx = new(@"(?<![0-9])(19|20)\d{2}(?![0-9])", RegexOptions.Compiled);
    private static readonly Regex ResolutionRx = new(@"(?i)(?<![a-z0-9])(2160p|1080p|720p|480p|4k)(?![a-z0-9])", RegexOptions.Compiled);
    private static readonly Regex SourceRx = new(@"(?i)(?<![a-z0-9])(BluRay|BDRip|BRRip|Remux|WEB-?DL|WEB-?Rip|HDTV|HDCAM|CAMRip|CAM|TELESYNC|TS|TC)(?![a-z0-9])", RegexOptions.Compiled);
    // "[Group] Show Title - 05 (1080p)", "[Group] Show - 1120v2 [1080p][HEVC]", "Show - 05-06 [720p]". Only used when the name has
    // no SxxEyy or 1x01: an absolute number is then the episode's position across the whole series.
    private static readonly Regex AnimeRx = new(@"^(?:\[(?<group>[^\]]+)\]\s*)?(?<title>.+?)\s+-\s+(?<e>\d{1,4})(?:-(?<e2>\d{1,4}))?(?:v\d)?(?=\s|\.|\[|\(|$)", RegexOptions.Compiled);
    private static readonly Regex ProperRx = new(@"(?i)(?<![a-z0-9])(PROPER|REPACK|RERIP)(?![a-z0-9])", RegexOptions.Compiled);
    private static readonly Regex VersionRx = new(@"(?i)(?<![a-z0-9])(?:\d{1,4})?v(?<v>[2-9])(?![a-z0-9])", RegexOptions.Compiled);
    private static readonly Regex UnsafeRx = new(@"(\.\.|[\\/:*?""<>|])|\.(exe|scr|bat|cmd|msi|lnk)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ParsedRelease Parse(string releaseName)
    {
        var name = releaseName.Trim();
        var unsafeName = UnsafeRx.IsMatch(name) || name.Contains("sample", StringComparison.OrdinalIgnoreCase) && name.Length < 40 && name.Contains("sample.", StringComparison.OrdinalIgnoreCase);

        int? resolution = null;
        var resMatch = ResolutionRx.Match(name);
        if (resMatch.Success)
        {
            resolution = resMatch.Groups[1].Value.ToLowerInvariant() switch
            {
                "2160p" or "4k" => 2160,
                "1080p" => 1080,
                "720p" => 720,
                "480p" => 480,
                _ => null
            };
        }

        var source = SourceUnknown;
        var srcMatch = SourceRx.Match(name);
        if (srcMatch.Success)
        {
            source = srcMatch.Groups[1].Value.ToUpperInvariant().Replace("-", "") switch
            {
                "BLURAY" or "BDRIP" or "BRRIP" or "REMUX" => SourceBluRay,
                "WEBDL" => SourceWebDl,
                "WEBRIP" => SourceWebRip,
                "HDTV" => SourceHdtv,
                _ => SourceCam
            };
        }

        var episodes = new List<int>();
        int? season = null;
        bool seasonPack = false;
        var epMatch = EpisodeRx.Match(name);
        if (epMatch.Success)
        {
            season = int.Parse(epMatch.Groups["s"].Value);
            var first = int.Parse(epMatch.Groups["e"].Value);
            var last = epMatch.Groups["e2"].Success ? int.Parse(epMatch.Groups["e2"].Value) : first;
            if (last < first || last - first > 30) last = first;
            for (var e = first; e <= last; e++) episodes.Add(e);
        }
        else
        {
            var alt = AltEpisodeRx.Match(name);
            if (alt.Success)
            {
                season = int.Parse(alt.Groups["s"].Value);
                episodes.Add(int.Parse(alt.Groups["e"].Value));
            }
            else
            {
                var pack = SeasonPackRx.Match(name);
                if (pack.Success)
                {
                    season = int.Parse(pack.Groups["s"].Value);
                    seasonPack = true;
                }
            }
        }

        int? year = null;
        var yearMatch = YearRx.Match(name);
        if (yearMatch.Success) year = int.Parse(yearMatch.Value);

        // Title is everything before the first structural marker (year, SxxEyy, resolution, source).
        var markers = new List<int>();
        foreach (var m in new[] { epMatch.Success ? epMatch.Index : -1, altEpisodeIndex(name), seasonPack ? SeasonPackRx.Match(name).Index : -1, resMatch.Success ? resMatch.Index : -1, srcMatch.Success ? srcMatch.Index : -1, yearMatch.Success ? yearMatch.Index : -1 })
            if (m > 0) markers.Add(m);
        var titleEnd = markers.Count > 0 ? markers.Min() : name.Length;
        var rawTitle = name[..titleEnd].Replace('.', ' ').Replace('_', ' ').Trim(' ', '-', '(', ')', '[', ']');
        var title = MultiSpace().Replace(rawTitle, " ").Trim();

        IReadOnlyList<int>? absolute = null;
        if (season is null)
        {
            var anime = AnimeRx.Match(name);
            if (anime.Success)
            {
                var first = int.Parse(anime.Groups["e"].Value);
                var last = anime.Groups["e2"].Success ? int.Parse(anime.Groups["e2"].Value) : first;
                // A lone number in a year's range is read as a year ("Movie - 2019"), not as episode 2019.
                var looksLikeYear = first is >= 1900 and <= 2099 && !anime.Groups["e2"].Success;
                if (!looksLikeYear && first > 0 && last >= first && last - first <= 30)
                {
                    absolute = Enumerable.Range(first, last - first + 1).ToList();
                    title = MultiSpace().Replace(anime.Groups["title"].Value.Replace('_', ' '), " ").Trim();
                }
            }
        }

        return new ParsedRelease(
            title,
            year,
            resolution,
            source,
            season,
            episodes,
            seasonPack,
            source == SourceCam,
            unsafeName,
            RevisionOf(name),
            absolute);
    }

    /// <summary>
    /// 1 for an original release. Each PROPER, REPACK or RERIP adds one (REAL.PROPER counts once per word); an anime "v2"/"v3"
    /// tag sets it directly. A higher revision of the same quality fixes a broken original.
    /// </summary>
    public static int RevisionOf(string name)
    {
        var revision = 1 + ProperRx.Matches(name).Count;
        var version = VersionRx.Match(name);
        if (version.Success) revision = Math.Max(revision, int.Parse(version.Groups["v"].Value));
        return Math.Min(revision, 9);
    }

    /// <summary>Source rank for comparing qualities: Blu-ray above WEB-DL above WEBRip above HDTV.</summary>
    public static int SourceRank(string? source) => source switch
    {
        SourceBluRay => 4,
        SourceWebDl => 3,
        SourceWebRip => 2,
        SourceHdtv => 1,
        _ => 0,
    };

    /// <summary>
    /// Lowercase alphanumeric key for title comparison, e.g. "The.Matrix" and "The Matrix!" both become "thematrix". Accents are
    /// folded ("Shōgun" matches "Shogun") and "&amp;" counts as "and" ("Law &amp; Order" matches "Law.and.Order").
    /// </summary>
    public static string NormalizeTitle(string value)
    {
        var folded = value.Replace("&", " and ").Normalize(System.Text.NormalizationForm.FormD);
        var chars = folded.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray();
        return new string(chars);
    }

    private static int altEpisodeIndex(string name) { var match = AltEpisodeRx.Match(name); return match.Success ? match.Index : -1; }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();
}
