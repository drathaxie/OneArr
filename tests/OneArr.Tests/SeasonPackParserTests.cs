using OneArr.Application;

namespace OneArr.Tests;

public class SeasonPackParserTests
{
    private readonly ReleaseParser _parser = new();

    [Theory]
    [InlineData("Adventure.Time.Side.Quests.S01.1080p.WEB-DL-GROUP", "Adventure Time Side Quests", 1)]
    [InlineData("The.Office.US.S06.720p.BluRay-GROUP", "The Office US", 6)]
    [InlineData("One.Piece.S23.1080p.WEB-DL-GROUP", "One Piece", 23)]
    public void SeasonPack_ParsesTitleAndSeason(string release, string expectedTitle, int expectedSeason)
    {
        var parsed = _parser.Parse(release);
        Assert.True(parsed.IsSeasonPack);
        Assert.Equal(expectedSeason, parsed.Season);
        Assert.Equal(ReleaseParser.NormalizeTitle(expectedTitle), ReleaseParser.NormalizeTitle(parsed.Title));
        Assert.Empty(parsed.Episodes);
    }

    [Theory]
    [InlineData("Adventure.Time.Side.Quests.S01E03.1080p.WEB-DL-GROUP")]
    [InlineData("Adventure.Time.Side.Quests.S01E03-E04.1080p.WEB-DL-GROUP")]
    public void EpisodeRelease_IsNotASeasonPack(string release)
    {
        Assert.False(_parser.Parse(release).IsSeasonPack);
    }
}
