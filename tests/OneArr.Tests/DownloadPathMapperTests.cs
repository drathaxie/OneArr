using OneArr.Infrastructure.Files;
using Xunit;

namespace OneArr.Tests;

public class DownloadPathMapperTests
{
    [Fact]
    public void MapsRemotePosixPathBeneathConfiguredLocalDownloadRoot()
    {
        using var dirs = new TempTree();
        var rule = new DownloadPathMapping("/downloads", dirs.Torrents);
        DownloadPathMapper.Validate(rule, dirs.Torrents);
        var path = DownloadPathMapper.Map(rule, "/downloads/Series/Show.S01E01.mkv", dirs.Torrents);
        Assert.Equal(Path.Combine(dirs.Torrents, "Series", "Show.S01E01.mkv"), path);
    }

    [Theory]
    [InlineData("/downloads2/Series/file.mkv")]
    [InlineData("/download/Series/file.mkv")]
    [InlineData("/downloads/../secrets/file.mkv")]
    [InlineData("/downloads//Series/file.mkv")]
    [InlineData("C:\\Movies\\file.mkv")]
    [InlineData("/downloads/Series\\..\\file.mkv")]
    public void RejectsPrefixCollisionsTraversalAndWindowsPaths(string remote)
    {
        using var dirs = new TempTree();
        var rule = new DownloadPathMapping("/downloads", dirs.Torrents);
        Assert.Throws<ArgumentException>(() => DownloadPathMapper.Map(rule, remote, dirs.Torrents));
    }

    [Fact]
    public void RejectsLocalDestinationOutsideTorrentRoot()
    {
        using var dirs = new TempTree();
        var rule = new DownloadPathMapping("/downloads", dirs.Media);
        Assert.Throws<UnauthorizedAccessException>(() =>
            DownloadPathMapper.Validate(rule, dirs.Torrents));
    }

    [Fact]
    public void MappingKeysAreScopedToOwningClient()
    {
        Assert.NotEqual(DownloadPathMapper.SettingsKey(1), DownloadPathMapper.SettingsKey(2));
        Assert.Equal("downloadClient.17.remotePath", DownloadPathMapper.SettingsKey(17));
    }

    [Fact]
    public void RulesRoundTripWithoutChangingRemotePrefix()
    {
        var rule = new DownloadPathMapping("/downloads/TV", "/mnt/downloads/tv");
        Assert.Equal(rule, DownloadPathMapper.Read(DownloadPathMapper.Serialize(rule)));
        Assert.Throws<ArgumentException>(() => DownloadPathMapper.Read("{missing:json}"));
    }
}
