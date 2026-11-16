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

    /// <summary>The layout from the qBittorrent screenshot: the episode, its .nfo and .srr, and a Sample folder.</summary>
    [Fact]
    public void A_release_folder_with_a_sample_folder_imports_only_the_episode_and_counts_the_rest()
    {
        using var tree = new TempTree();
        var folder = Path.Combine(tree.Root, "Demon.Slayer.Kimetsu.no.Yaiba.S03E07.1080p.WEB.H264-SENPAI");
        var episode = tree.WriteFile("Demon.Slayer.Kimetsu.no.Yaiba.S03E07.1080p.WEB.H264-SENPAI/demon.slayer.kimetsu.no.yaiba.s03e07.1080p.web.h264-senpai.mkv", "episode");
        tree.WriteFile("Demon.Slayer.Kimetsu.no.Yaiba.S03E07.1080p.WEB.H264-SENPAI/demon.slayer.kimetsu.no.yaiba.s03e07.1080p.web.h264-senpai.nfo", "nfo");
        tree.WriteFile("Demon.Slayer.Kimetsu.no.Yaiba.S03E07.1080p.WEB.H264-SENPAI/demon.slayer.kimetsu.no.yaiba.s03e07.1080p.web.h264-senpai.srr", "srr");
        tree.WriteFile("Demon.Slayer.Kimetsu.no.Yaiba.S03E07.1080p.WEB.H264-SENPAI/Sample/demon.slayer.kimetsu.no.yaiba.s03e07.1080p.web.h264-senpai-sample.mkv", "clip");

        var listing = OneArr.Infrastructure.Services.DownloadService.DescribeContent(folder, tree.Root);

        Assert.Equal([Path.GetFullPath(episode)], listing.Importable);
        Assert.Equal(4, listing.Files);
        Assert.Equal(2, listing.VideoFiles);
        Assert.Equal(1, listing.Samples);
        Assert.Equal(0, listing.OutsideRoot);
    }

    [Fact]
    public void A_download_holding_only_a_sample_says_what_it_found_and_where()
    {
        using var tree = new TempTree();
        var folder = Path.Combine(tree.Root, "Show.S01E01");
        tree.WriteFile("Show.S01E01/Sample/show.s01e01-sample.mkv", "clip");
        tree.WriteFile("Show.S01E01/show.s01e01.nfo", "nfo");

        var text = OneArr.Infrastructure.Services.DownloadService.DescribeContent(folder, tree.Root).Explain();

        Assert.Contains(Path.GetFullPath(folder), text);
        Assert.Contains("2 file(s) found, 1 video file(s), 1 skipped as samples, 0 outside the Torrent download root", text);
    }

    [Fact]
    public void An_empty_folder_and_a_folder_without_video_are_named_as_such()
    {
        using var tree = new TempTree();
        var empty = Path.Combine(tree.Root, "empty");
        Directory.CreateDirectory(empty);
        var textOnly = Path.Combine(tree.Root, "text");
        tree.WriteFile("text/readme.nfo", "nfo");

        Assert.Contains("empty as OneArr sees it", OneArr.Infrastructure.Services.DownloadService.DescribeContent(empty, tree.Root).Explain());
        Assert.Contains("None of the files is a video file", OneArr.Infrastructure.Services.DownloadService.DescribeContent(textOnly, tree.Root).Explain());
    }
}
