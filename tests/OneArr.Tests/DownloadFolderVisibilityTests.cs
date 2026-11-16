using Xunit;

namespace OneArr.Tests;

/// <summary>A download folder OneArr cannot see must be named as a configuration problem, not reported as an empty download.</summary>
public class DownloadFolderVisibilityTests
{
    [Fact]
    public void A_missing_download_folder_is_reported_as_a_path_problem()
    {
        using var tree = new TempTree();
        var missing = Path.Combine(tree.Root, "not-here");

        var ex = Assert.Throws<ArgumentException>(() => OneArr.Infrastructure.Services.DownloadService.RequireVisibleContent(missing, tree.Root));

        Assert.Contains("not visible to OneArr", ex.Message);
        Assert.Contains("download path mapping", ex.Message);
        Assert.DoesNotContain("samples are excluded", ex.Message);
    }

    [Fact]
    public void A_visible_download_folder_passes()
    {
        using var tree = new TempTree();
        var folder = Path.Combine(tree.Root, "present");
        Directory.CreateDirectory(folder);

        OneArr.Infrastructure.Services.DownloadService.RequireVisibleContent(folder, tree.Root);
    }

    /// <summary>The screenshot case: the folder exists, but the torrent root setting does not contain it, so every file would be dropped silently.</summary>
    [Fact]
    public void A_visible_folder_outside_the_torrent_root_is_named_not_silently_emptied()
    {
        using var tree = new TempTree();
        var torrentRoot = Path.Combine(tree.Root, "torrents");
        var elsewhere = Path.Combine(tree.Root, "qbit-downloads", "Demon.Slayer");
        Directory.CreateDirectory(torrentRoot);
        Directory.CreateDirectory(elsewhere);

        var ex = Assert.Throws<ArgumentException>(() => OneArr.Infrastructure.Services.DownloadService.RequireVisibleContent(elsewhere, torrentRoot));

        Assert.Contains("outside the Torrent download root", ex.Message);
        Assert.DoesNotContain("samples are excluded", ex.Message);
    }
}
