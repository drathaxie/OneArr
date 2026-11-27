using OneArr.Domain;

namespace OneArr.Application;

/// <summary>
/// Deterministic accept/reject decision with human-readable reasons. Rejections are ordered by
/// safety first (unsafe names, wrong media), then quality, then duplicates, so the UI can explain each one.
/// </summary>
public sealed class ReleaseRanker : IReleaseRanker
{
    private readonly IReleaseParser _parser;

    public ReleaseRanker(IReleaseParser parser)
    {
        _parser = parser;
    }

    public ReleaseDecision Evaluate(ReleaseCandidate candidate, ReleaseTarget target)
    {
        var parsed = _parser.Parse(candidate.Title);
        var reasons = new List<string>();

        if (parsed.IsUnsafeName) reasons.Add("Unsafe release name (path characters or executable extension)");
        if (target.Blocklisted is { Count: > 0 } blocked
            && (blocked.Contains("title:" + candidate.Title.Trim().ToLowerInvariant())
                || (candidate.InfoHash is { Length: > 0 } bh && blocked.Contains(bh.ToLowerInvariant()))))
            reasons.Add("Blocklisted: this release failed or was rejected before");
        if (candidate.Size <= 0) reasons.Add("Release reports no size");
        if (candidate.Link.Length == 0) reasons.Add("Release has no magnet or torrent link");

        var releaseTitle = ReleaseParser.NormalizeTitle(parsed.Title);
        if (releaseTitle != ReleaseParser.NormalizeTitle(target.Title)
            && !(target.Aliases ?? []).Any(a => ReleaseParser.NormalizeTitle(a) is { Length: > 1 } alias && alias == releaseTitle))
            reasons.Add($"Title mismatch: release is '{parsed.Title}', wanted '{target.Title}'");

        if (target.Type == MediaType.Movie)
        {
            if (parsed.Year is int ry && target.Year is int ty && Math.Abs(ry - ty) > 1)
                reasons.Add($"Year mismatch: {ry} vs {ty}");
        }
        else
        {
            if (parsed.Season is null)
                reasons.Add("No season information in release");
            else if (target.Season is int ws && parsed.Season != ws)
                reasons.Add($"Wrong season: S{parsed.Season:00}, wanted S{ws:00}");

            if (target.Episode is int we && !parsed.IsSeasonPack && !parsed.Episodes.Contains(we))
                reasons.Add($"Wrong episode: release has {FormatEpisodes(parsed.Episodes)}, wanted E{we:00}");
        }

        if (parsed.IsCam) reasons.Add("Cam or telesync source");
        if (!target.Profile.AllowWebSources && parsed.Source is ReleaseParser.SourceWebDl or ReleaseParser.SourceWebRip)
            reasons.Add("Web sources not allowed by quality profile");
        var allowedSources = QualityProfile.Words(target.Profile.AllowedSources);
        if (allowedSources.Count > 0 && !allowedSources.Contains(parsed.Source, StringComparer.OrdinalIgnoreCase))
            reasons.Add($"Source {parsed.Source} not allowed by quality profile ({string.Join(", ", allowedSources)})");

        // Size limits apply to a movie or a single episode; a pack's size depends on how many episodes it holds.
        if (!parsed.IsSeasonPack && candidate.Size > 0 && parsed.Episodes.Count <= 1)
        {
            var mb = candidate.Size / (1024 * 1024);
            if (target.Profile.MinSizeMb > 0 && mb < target.Profile.MinSizeMb)
                reasons.Add($"Size {mb} MB below the profile minimum {target.Profile.MinSizeMb} MB");
            if (target.Profile.MaxSizeMb > 0 && mb > target.Profile.MaxSizeMb)
                reasons.Add($"Size {mb} MB above the profile maximum {target.Profile.MaxSizeMb} MB");
        }

        var must = QualityProfile.Words(target.Profile.MustContain);
        if (must.Count > 0 && !must.Any(w => HasWord(candidate.Title, w)))
            reasons.Add("Missing a required word: " + string.Join(", ", must));
        var banned = QualityProfile.Words(target.Profile.MustNotContain).FirstOrDefault(w => HasWord(candidate.Title, w));
        if (banned is not null) reasons.Add($"Contains the banned word '{banned}'");

        var resolution = parsed.Resolution;
        if (resolution is null)
            reasons.Add("Unknown resolution");
        else if (resolution < target.Profile.MinResolution)
            reasons.Add($"Resolution {resolution}p below minimum {target.Profile.MinResolution}p");

        if (target.AlreadyHasFile && target.CurrentResolution is int current)
        {
            var candRes = resolution ?? 0;
            var candRank = ReleaseParser.SourceRank(parsed.Source);
            var haveRank = ReleaseParser.SourceRank(target.CurrentSource);
            // A PROPER/REPACK of exactly the quality already on disk fixes a broken release, even at the cutoff.
            var isRevision = candRes == current && candRank == haveRank && parsed.Revision > target.CurrentRevision;
            if (!isRevision)
            {
                if (current >= target.Profile.CutoffResolution)
                    reasons.Add($"Already have {current}p, cutoff {target.Profile.CutoffResolution}p met");
                else if (candRes < current || (candRes == current && candRank <= haveRank))
                    reasons.Add($"Not an upgrade: have {current}p {target.CurrentSource ?? "unknown source"}, release is {candRes}p {parsed.Source}");
            }
        }
        else if (target.AlreadyHasFile)
        {
            reasons.Add("Already has a file");
        }

        if (candidate.InfoHash is { Length: > 0 } hash && target.KnownInfoHashes.Contains(hash.ToLowerInvariant()))
            reasons.Add("Duplicate: already queued or downloaded");

        if (candidate.Seeders == 0) reasons.Add("No seeders");

        var score = Score(parsed, candidate) + 15 * QualityProfile.Words(target.Profile.PreferredWords).Count(w => HasWord(candidate.Title, w));
        return new ReleaseDecision(candidate, parsed, reasons.Count == 0, score, reasons);
    }

    /// <summary>Ranking used to order accepted releases: resolution dominates, then source, then seeders, then size stability.</summary>
    public static int Score(ParsedRelease parsed, ReleaseCandidate candidate)
    {
        var resolution = (parsed.Resolution ?? 0) / 10;
        var source = parsed.Source switch
        {
            ReleaseParser.SourceBluRay => 30,
            ReleaseParser.SourceWebDl => 25,
            ReleaseParser.SourceWebRip => 20,
            ReleaseParser.SourceHdtv => 10,
            _ => 0
        };
        var seeders = Math.Min(candidate.Seeders, 100) / 10;
        var pack = parsed.IsSeasonPack ? 2 : 0;
        var revision = (parsed.Revision - 1) * 5;
        return resolution + source + seeders + pack + revision;
    }

    /// <summary>Whole-word, case-insensitive: "x265" matches "Show.S01E01.x265-GRP" but "HDR" does not match "HDRip".</summary>
    public static bool HasWord(string title, string word) =>
        word.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(title,
            @"(?<![A-Za-z0-9])" + System.Text.RegularExpressions.Regex.Escape(word) + @"(?![A-Za-z0-9])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string FormatEpisodes(IReadOnlyList<int> episodes) =>
        episodes.Count == 0 ? "no episode" : string.Join(", ", episodes.Select(e => $"E{e:00}"));
}
