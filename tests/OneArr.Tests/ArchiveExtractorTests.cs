using OneArr.Infrastructure.Files;
using Xunit;

namespace OneArr.Tests;

/// <summary>
/// The RAR reader itself: first-part detection, plain-language failures for
/// unreadable archives, and the staging contract (outside the torrent folder,
/// only ever its own folder is removed). Real extraction of a valid archive is
/// covered by the pipeline tests with a stub extractor; a real RAR fixture
/// cannot be made in this sandbox (no rar/7z tooling), so the valid-archive
/// path still needs the Windows acceptance run on real downloads.
/// </summary>
public class ArchiveExtractorTests
{
    private static string WriteRar(TempTree tree, string relative, string content = "not a real rar archive")
    {
        var path = Path.Combine(tree.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void HasArchive_DetectsFirstPartOnly()
    {
        using var tree = new TempTree();
        var extractor = new SharpCompressArchiveExtractor();
        var folder = Path.Combine(tree.Torrents, "Show");
        Directory.CreateDirectory(folder);

        Assert.False(extractor.HasArchive(folder));
        WriteRar(tree, "torrents/Show/show.part02.rar");
        WriteRar(tree, "torrents/Show/show.r00");
        Assert.False(extractor.HasArchive(folder));
        WriteRar(tree, "torrents/Show/show.part01.rar");
        Assert.True(extractor.HasArchive(folder));
    }

    [Fact]
    public void HasArchive_DetectsPlainRar()
    {
        using var tree = new TempTree();
        var extractor = new SharpCompressArchiveExtractor();
        WriteRar(tree, "torrents/Show/show.rar");
        Assert.True(extractor.HasArchive(Path.Combine(tree.Torrents, "Show")));
        Assert.True(SharpCompressArchiveExtractor.IsArchiveRootPart("show.rar"));
        Assert.True(SharpCompressArchiveExtractor.IsArchiveRootPart("show.part1.rar"));
        Assert.False(SharpCompressArchiveExtractor.IsArchiveRootPart("show.part02.rar"));
        Assert.False(SharpCompressArchiveExtractor.IsArchiveRootPart("show.r00"));
    }

    [Fact]
    public async Task ListMedia_GarbageRar_ThrowsPackedReason()
    {
        using var tree = new TempTree();
        var extractor = new SharpCompressArchiveExtractor();
        var folder = Path.Combine(tree.Torrents, "Bad");
        Directory.CreateDirectory(folder);
        WriteRar(tree, "torrents/Bad/bad.rar");
        var ex = await Assert.ThrowsAsync<ArchiveExtractionException>(() =>
            extractor.ListMediaAsync(folder, tree.Torrents, default));
        Assert.StartsWith("Packed (RAR)", ex.Message);
    }

    [Fact]
    public async Task ExtractMedia_GarbageRar_ThrowsPackedReasonAndLeavesNoStaging()
    {
        using var tree = new TempTree();
        var extractor = new SharpCompressArchiveExtractor();
        var folder = Path.Combine(tree.Torrents, "Bad");
        Directory.CreateDirectory(folder);
        WriteRar(tree, "torrents/Bad/bad.rar");
        var ex = await Assert.ThrowsAsync<ArchiveExtractionException>(() =>
            extractor.ExtractMediaAsync(folder, tree.Torrents, 7, default));
        Assert.StartsWith("Packed (RAR)", ex.Message);
        Assert.False(Directory.Exists(SharpCompressArchiveExtractor.StagingDirectory(tree.Torrents, 7)));
    }

    [Fact]
    public void StagingDirectory_IsUnderTorrentRootButOutsideAnyTorrentFolder()
    {
        using var tree = new TempTree();
        var torrentFolder = Path.Combine(tree.Torrents, "Show");
        var staging = SharpCompressArchiveExtractor.StagingDirectory(tree.Torrents, 42);
        Assert.StartsWith(Path.GetFullPath(tree.Torrents), Path.GetFullPath(staging));
        Assert.DoesNotContain(Path.GetFullPath(torrentFolder), Path.GetFullPath(staging));
        Assert.EndsWith(Path.Combine(".onearr-extract", "42"), staging);
    }

    [Fact]
    public void CleanupStaging_RemovesOnlyItsOwnFolder()
    {
        using var tree = new TempTree();
        var extractor = new SharpCompressArchiveExtractor();
        var staging = SharpCompressArchiveExtractor.StagingDirectory(tree.Torrents, 9);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "video.mkv"), "staged");
        var keep = Path.Combine(tree.Torrents, "Show", "show.rar");
        Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
        File.WriteAllText(keep, "keep me");

        extractor.CleanupStaging(tree.Torrents, 9);

        Assert.False(Directory.Exists(staging));
        Assert.Equal("keep me", File.ReadAllText(keep));
        extractor.CleanupStaging(tree.Torrents, 9); // second call is a no-op, not an error
    }
}
