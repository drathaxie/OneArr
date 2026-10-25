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
        if (candidate.Size <= 0) reasons.Add("Release reports no size");
        if (candidate.Link.Length == 0) reasons.Add("Release has no magnet or torrent link");

        if (ReleaseParser.NormalizeTitle(parsed.Title) != ReleaseParser.NormalizeTitle(target.Title))
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

        var resolution = parsed.Resolution;
        if (resolution is null)
            reasons.Add("Unknown resolution");
        else if (resolution < target.Profile.MinResolution)
            reasons.Add($"Resolution {resolution}p below minimum {target.Profile.MinResolution}p");

        if (target.AlreadyHasFile && target.CurrentResolution is int current)
        {
            if (current >= target.Profile.CutoffResolution)
                reasons.Add($"Already have {current}p, cutoff {target.Profile.CutoffResolution}p met");
            else if (resolution is int cand && cand <= current)
                reasons.Add($"Not an upgrade: have {current}p, release is {cand}p");
        }
        else if (target.AlreadyHasFile)
        {
            reasons.Add("Already has a file");
        }

        if (candidate.InfoHash is { Length: > 0 } hash && target.KnownInfoHashes.Contains(hash.ToLowerInvariant()))
            reasons.Add("Duplicate: already queued or downloaded");

        if (candidate.Seeders == 0) reasons.Add("No seeders");

        var score = Score(parsed, candidate);
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
        return resolution + source + seeders + pack;
    }

    private static string FormatEpisodes(IReadOnlyList<int> episodes) =>
        episodes.Count == 0 ? "no episode" : string.Join(", ", episodes.Select(e => $"E{e:00}"));
}
