using OneArr.Application;
using OneArr.Domain;
using Xunit;

namespace OneArr.Tests;

public class ReleaseParserTests
{
    private readonly ReleaseParser _p = new();

    [Theory]
    [InlineData("The.Matrix.1999.2160p.BluRay.x265-GRP", "The Matrix", 1999, 2160, "bluray")]
    [InlineData("Some.Movie.2020.1080p.WEB-DL.DDP5.1", "Some Movie", 2020, 1080, "webdl")]
    [InlineData("Old.Film.1994.720p.HDTV.x264", "Old Film", 1994, 720, "hdtv")]
    public void ParsesMovieTitleYearResolutionAndSource(string name, string title, int year, int res, string source)
    {
        var r = _p.Parse(name);
        Assert.Equal(title, r.Title);
        Assert.Equal(year, r.Year);
        Assert.Equal(res, r.Resolution);
        Assert.Equal(source, r.Source);
        Assert.Null(r.Season);
        Assert.False(r.IsCam);
    }

    [Fact]
    public void ParsesSingleEpisode()
    {
        var r = _p.Parse("Test.Show.S01E02.720p.WEB-DL.x264-NTb");
        Assert.Equal("Test Show", r.Title);
        Assert.Equal(1, r.Season);
        Assert.Equal(new[] { 2 }, r.Episodes);
        Assert.False(r.IsSeasonPack);
    }

    [Fact]
    public void ParsesMultiEpisodeRange()
    {
        var r = _p.Parse("Test.Show.S02E03-E05.1080p.WEBRip");
        Assert.Equal(2, r.Season);
        Assert.Equal(new[] { 3, 4, 5 }, r.Episodes);
        Assert.Equal("webrip", r.Source);
    }

    [Fact]
    public void ParsesAlternateNxMNotation()
    {
        var r = _p.Parse("Test Show 3x07 720p HDTV");
        Assert.Equal(3, r.Season);
        Assert.Equal(new[] { 7 }, r.Episodes);
    }

    [Fact]
    public void DetectsSeasonPack()
    {
        var r = _p.Parse("Test.Show.S03.1080p.BluRay");
        Assert.Equal(3, r.Season);
        Assert.True(r.IsSeasonPack);
        Assert.Empty(r.Episodes);
    }

    [Theory]
    [InlineData("Movie.2010.CAM.x264")]
    [InlineData("Movie.2010.TELESYNC.x264")]
    public void FlagsCamSources(string name) => Assert.True(_p.Parse(name).IsCam);

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("Movie.2010.1080p\\..\\evil")]
    [InlineData("Movie.2010.1080p.exe")]
    public void FlagsUnsafeNames(string name) => Assert.True(_p.Parse(name).IsUnsafeName);
}

public class ReleaseRankerTests
{
    private static readonly QualityProfile Profile = new() { Name = "HD", MinResolution = 720, CutoffResolution = 1080, AllowWebSources = true };
    private readonly ReleaseRanker _ranker = new(new ReleaseParser());

    private static ReleaseCandidate C(string title, int seeders = 10, string? hash = null, long size = 1_000_000_000) =>
        new(title, "magnet:?xt=urn:btih:" + new string('a', 40), size, seeders, hash, "test", []);

    private static ReleaseTarget Movie(int? currentRes = null, bool hasFile = false, IReadOnlySet<string>? hashes = null) =>
        new(MediaType.Movie, "The Matrix", 1999, null, null, Profile, currentRes, hasFile, hashes ?? new HashSet<string>());

    private static ReleaseTarget Episode(int season = 1, int episode = 2) =>
        new(MediaType.Series, "Test Show", 2011, season, episode, Profile, null, false, new HashSet<string>());

    [Fact]
    public void AcceptsGoodMovieRelease()
    {
        var d = _ranker.Evaluate(C("The.Matrix.1999.1080p.BluRay.x264"), Movie());
        Assert.True(d.Accepted, string.Join("; ", d.Reasons));
    }

    [Fact]
    public void RejectsWrongMovieTitle()
    {
        var d = _ranker.Evaluate(C("Another.Movie.1999.1080p.BluRay"), Movie());
        Assert.False(d.Accepted);
        Assert.Contains(d.Reasons, r => r.StartsWith("Title mismatch"));
    }

    [Fact]
    public void RejectsBelowMinimumResolutionAndUnknownResolution()
    {
        Assert.Contains(_ranker.Evaluate(C("The.Matrix.1999.480p.DVDRip"), Movie()).Reasons, r => r.Contains("below minimum"));
        Assert.Contains(_ranker.Evaluate(C("The.Matrix.1999.BluRay"), Movie()).Reasons, r => r == "Unknown resolution");
    }

    [Fact]
    public void RejectsCamAndUnsafeNamesAndNoSeeders()
    {
        var cam = _ranker.Evaluate(C("The.Matrix.1999.1080p.CAM"), Movie());
        Assert.Contains(cam.Reasons, r => r == "Cam or telesync source");
        Assert.Contains(_ranker.Evaluate(C("The.Matrix.1999.1080p.exe"), Movie()).Reasons, r => r.StartsWith("Unsafe"));
        Assert.Contains(_ranker.Evaluate(C("The.Matrix.1999.1080p.BluRay", seeders: 0), Movie()).Reasons, r => r == "No seeders");
    }

    [Fact]
    public void RejectsWrongSeriesEpisodeAndSeason()
    {
        var wrongEp = _ranker.Evaluate(C("Test.Show.S01E05.720p.WEB-DL"), Episode());
        Assert.Contains(wrongEp.Reasons, r => r.StartsWith("Wrong episode"));
        var wrongSeason = _ranker.Evaluate(C("Test.Show.S02E02.720p.WEB-DL"), Episode());
        Assert.Contains(wrongSeason.Reasons, r => r.StartsWith("Wrong season"));
        var wrongShow = _ranker.Evaluate(C("Other.Show.S01E02.720p.WEB-DL"), Episode());
        Assert.Contains(wrongShow.Reasons, r => r.StartsWith("Title mismatch"));
    }

    [Fact]
    public void AcceptsMultiEpisodeReleaseContainingTarget()
    {
        var d = _ranker.Evaluate(C("Test.Show.S01E01-E03.1080p.WEB-DL"), Episode(1, 2));
        Assert.True(d.Accepted, string.Join("; ", d.Reasons));
    }

    [Fact]
    public void PreventsEndlessRegrabOnceCutoffMetOrFileExists()
    {
        var cutoff = _ranker.Evaluate(C("The.Matrix.1999.1080p.BluRay"), Movie(currentRes: 1080, hasFile: true));
        Assert.Contains(cutoff.Reasons, r => r.Contains("cutoff"));
        var notUpgrade = _ranker.Evaluate(C("The.Matrix.1999.720p.BluRay"), Movie(currentRes: 1080, hasFile: true));
        Assert.Contains(notUpgrade.Reasons, r => r.StartsWith("Not an upgrade") || r.Contains("cutoff"));
        var upgrade = _ranker.Evaluate(C("The.Matrix.1999.2160p.BluRay"), new ReleaseTarget(MediaType.Movie, "The Matrix", 1999, null, null, new QualityProfile { MinResolution = 720, CutoffResolution = 2160 }, 1080, true, new HashSet<string>()));
        Assert.True(upgrade.Accepted, string.Join("; ", upgrade.Reasons));
    }

    [Fact]
    public void RejectsDuplicateInfoHash()
    {
        var hash = "abcdef0123456789abcdef0123456789abcdef01";
        var d = _ranker.Evaluate(C("The.Matrix.1999.1080p.BluRay", hash: hash), Movie(hashes: new HashSet<string> { hash }));
        Assert.Contains(d.Reasons, r => r.StartsWith("Duplicate"));
    }

    [Fact]
    public void RanksHigherResolutionAboveLowerWhenBothAccepted()
    {
        var hd = _ranker.Evaluate(C("The.Matrix.1999.1080p.BluRay", seeders: 50), Movie());
        var uhd = _ranker.Evaluate(C("The.Matrix.1999.2160p.BluRay", seeders: 50), Movie());
        Assert.True(uhd.Score > hd.Score);
    }
}
