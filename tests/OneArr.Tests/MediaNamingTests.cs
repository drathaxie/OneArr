using OneArr.Application;
using Xunit;

namespace OneArr.Tests;

public class MediaNamingTests
{
    [Fact]
    public void MovieTemplateFormatsMetadataAndPreservesExtension()
    {
        var file = MediaNaming.Preview(new MediaNaming.Request(
            "movie", MediaNaming.MovieTemplate, "Dune: Part Two", "Dune.Part.Two.2160p.MKV",
            Year: 2024, Quality: "2160p BluRay"));
        Assert.Equal("Dune Part Two (2024) - 2160p BluRay.mkv", file);
    }

    [Fact]
    public void SeriesTemplateUsesPaddedEpisodeNumbersAndSafeEpisodeTitle()
    {
        var file = MediaNaming.Preview(new MediaNaming.Request(
            "series", MediaNaming.EpisodeTemplate, "The Show", "Show.S01E02.mp4",
            Season: 1, Episode: 2, EpisodeTitle: "Who/What? : Test", Quality: "1080p WEB-DL"));
        Assert.Equal("The Show - S01E02 - Who What Test - 1080p WEB-DL.mp4", file);
    }

    [Theory]
    [InlineData("../{Title}")]
    [InlineData("C:\\Users\\{Title}")]
    [InlineData("{Title}/{Year}")]
    [InlineData("{Title} {Unknown}")]
    [InlineData("{Title} {Season:00}")]
    [InlineData("{Title} {")]
    public void UnsafeOrUnknownTemplateIsRejected(string template)
    {
        Assert.Throws<ArgumentException>(() => MediaNaming.Preview(new MediaNaming.Request(
            "movie", template, "Movie", "source.mkv", Year: 2026)));
    }

    [Theory]
    [InlineData("../../evil.mkv")]
    [InlineData("C:\\staging\\movie.mkv")]
    [InlineData("file.exe")]
    [InlineData("noextension")]
    public void OriginalFilenameMustBeOneMediaFile(string file)
    {
        Assert.Throws<ArgumentException>(() => MediaNaming.Preview(new MediaNaming.Request(
            "movie", "{Title}", "Movie", file)));
    }

    [Fact]
    public void WindowsReservedFilenameIsPrefixedAndLongFilenamesStayBounded()
    {
        Assert.Equal("_CON.mkv", MediaNaming.Preview(new MediaNaming.Request("movie",
            "{Title}", "CON", "x.mkv")));
        var longName = MediaNaming.Preview(new MediaNaming.Request("movie", "{Title}",
            new string('X', 300), "foo.mkv"));
        Assert.True(longName.Length <= 220);
        Assert.EndsWith(".mkv", longName);
    }

    [Fact]
    public void MissingMetadataFailsInsteadOfInventingEpisodeNumbers()
    {
        Assert.Throws<ArgumentException>(() => MediaNaming.Preview(new MediaNaming.Request(
            "series", MediaNaming.EpisodeTemplate, "Show", "source.mkv", Season: 1, Episode: null,
            EpisodeTitle: "Start", Quality: "1080p")));
    }
}
