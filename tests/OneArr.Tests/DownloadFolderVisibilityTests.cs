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

        var ex = Assert.Throws<ArgumentException>(() => OneArr.Infrastructure.Services.DownloadService.RequireVisibleContent(missing));

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

        OneArr.Infrastructure.Services.DownloadService.RequireVisibleContent(folder);
    }
}
