using OneArr.Infrastructure.Files;
using OneArr.Infrastructure.Services;
using Xunit;

namespace OneArr.Tests;

/// <summary>Sample clips are never imported or matched, and real titles that contain the word are.</summary>
public class SampleFilesTests
{
    [Theory]
    [InlineData("sample-american.dad.s16e09.720p.web.h264-trump.mkv")]
    [InlineData("american.dad.s16e09.720p-sample.mkv")]
    [InlineData("american.dad.s16e09.sample.mkv")]
    [InlineData("sample.mkv")]
    [InlineData("Show.S01E01.[sample].mkv")]
    [InlineData("Show/Sample/clip.mkv")]
    [InlineData("Show/Samples/clip.mkv")]
    public void Sample_clips_are_recognised(string path) => Assert.True(SampleFiles.IsSample(path));

    [Theory]
    [InlineData("american.dad.s16e09.720p.web.h264-trump.mkv")]
    [InlineData("The Sample Show S01E01.mkv")]
    [InlineData("Sample Show/Show S01E01.mkv")]
    [InlineData("Dune (2021) 1080p.mkv")]
    public void Real_episodes_and_titles_with_the_word_are_not_samples(string path) => Assert.False(SampleFiles.IsSample(path));

    [Fact]
    public void A_download_imports_only_the_real_file_and_never_its_sample()
    {
        using var tree = new TempTree();
        var real = tree.WriteFile("download/american.dad.s16e09.720p.web.h264-trump.mkv", "episode-bytes");
        tree.WriteFile("download/sample-american.dad.s16e09.720p.web.h264-trump.mkv", "clip");
        tree.WriteFile("download/Sample/extra.mkv", "clip");
        tree.WriteFile("download/readme.nfo", "text");

        var files = DownloadService.ListFiles(Path.Combine(tree.Root, "download"), tree.Root);

        Assert.Equal([Path.GetFullPath(real)], files);
    }

    [Fact]
    public void A_download_holding_only_a_sample_yields_nothing_to_import()
    {
        using var tree = new TempTree();
        var sample = tree.WriteFile("download/sample-show.s01e01.mkv", "clip");

        Assert.Empty(DownloadService.ListFiles(sample, tree.Root));
    }
}
