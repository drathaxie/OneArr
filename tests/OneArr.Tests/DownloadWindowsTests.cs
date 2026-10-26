using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using OneArr.Domain;
using OneArr.Infrastructure.Files;
using OneArr.Infrastructure.Services;
using Xunit;

namespace OneArr.Tests;

/// <summary>
/// Real NTFS behaviour for the download lifecycle: directory-boundary comparisons, drive-letter
/// case, UNC paths, and junctions. Run only on Windows (Platform=Windows). Cross-platform
/// siblings of these rules live in DownloadLifecycleTests and FilesystemContainmentTests.
/// </summary>
[Trait("Platform", "Windows")]
[SupportedOSPlatform("windows")]
public class DownloadWindowsTests
{
    private static void RequireWindows() =>
        Assert.True(OperatingSystem.IsWindows(), "Platform=Windows tests must run on Windows");

    private static void Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public void SiblingDirectory_WithSharedPrefix_IsRejected()
    {
        RequireWindows();
        // D:\Downloads2 must never pass a D:\Downloads root, and drive-letter case must not matter.
        Assert.Throws<UnauthorizedAccessException>(() => SafeImporter.CanonicalUnder(@"D:\Downloads", @"D:\Downloads2\Show\file.mkv"));
        Assert.Equal(@"D:\Downloads\Show\file.mkv", SafeImporter.CanonicalUnder(@"D:\Downloads", @"D:\Downloads\Show\file.mkv"));
        Assert.Equal(@"D:\Downloads\Show\file.mkv", SafeImporter.CanonicalUnder(@"D:\Downloads", @"d:\downloads\Show\file.mkv"));
    }

    [Fact]
    public void UncPaths_CompareServerAndShare_NotJustPrefix()
    {
        RequireWindows();
        Assert.Equal(@"\\server\share\media\file.mkv", SafeImporter.CanonicalUnder(@"\\server\share\media", @"\\server\share\media\file.mkv"));
        Assert.Throws<UnauthorizedAccessException>(() => SafeImporter.CanonicalUnder(@"\\server\share\media", @"\\server\share\media2\file.mkv"));
        Assert.Throws<UnauthorizedAccessException>(() => SafeImporter.CanonicalUnder(@"\\server\share\media", @"\\other\share\media\file.mkv"));
        Assert.True(SafeImporter.IsSameVolume(@"\\server\share\a.mkv", @"\\server\share\b.mkv"));
        Assert.False(SafeImporter.IsSameVolume(@"\\server\share\a.mkv", @"\\server\other\b.mkv"));
        Assert.False(SafeImporter.IsSameVolume(@"C:\a.mkv", @"D:\b.mkv"));
        Assert.True(SafeImporter.IsSameVolume(@"C:\a.mkv", @"c:\b.mkv"));
    }

    [Fact]
    public async Task JunctionThroughTorrentRoot_IsRefused_AndTargetUntouched()
    {
        RequireWindows();
        using var tree = new TempTree();
        var outside = Path.Combine(tree.Root, "outside");
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "Show - S01E01.mkv");
        await File.WriteAllTextAsync(target, "outside-bytes");
        var linked = Path.Combine(tree.Torrents, "linked");
        Run("cmd.exe", $"/c mklink /J \"{linked}\" \"{outside}\"");
        var importer = new SafeImporter(TimeSpan.Zero);
        var outcome = await importer.ImportAsync(new ImportRequest(
            Path.Combine(linked, "Show - S01E01.mkv"), Path.Combine(tree.Media, "tv"), "Show\\Season 01", tree.Torrents, dryRun: false, null), default);
        Assert.Equal(ImportAction.Failed, outcome.Action);
        Assert.False(outcome.Success);
        Assert.Contains("link", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outside-bytes", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(Path.Combine(tree.Media, "tv", "Show", "Season 01", "Show - S01E01.mkv")));
    }

    [Fact]
    public async Task MovieImport_OnRealWindowsPaths_LinksFileAndMarksAvailable()
    {
        RequireWindows();
        using var world = new TestWorld();
        await world.ConfigureAsync(automaticGrab: false);
        var movieId = await world.AddMovieAsync("Dune", 2021);
        var file = world.Dirs.WriteFile("torrents\\Dune\\Dune (2021) 1080p.mkv", "movie-bytes");
        await using (var db = world.Db.CreateDbContext())
        {
            var media = await db.MediaItems.FirstAsync(m => m.Id == movieId);
            db.DownloadJobs.Add(new DownloadJob
            {
                MediaItemId = movieId, MediaType = MediaType.Movie, ReleaseTitle = "Dune", Link = "magnet:?xt=urn:btih:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Category = "onearr-movies", DownloadClientId = 1, ClientName = "qbit", Status = DownloadStatus.Completed,
                ContentPath = Path.GetDirectoryName(file), CreatedAt = world.Time.GetUtcNow(), UpdatedAt = world.Time.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }
        var summary = await world.Downloads.ImportCompletedAsync(dryRun: false, default);
        Assert.Equal(1, summary.Imported);
        await using var db = world.Db.CreateDbContext();
        Assert.True((await db.MediaItems.AsNoTracking().FirstAsync(m => m.Id == movieId)).HasFile);
        Assert.True(File.Exists(file), "Source must survive import");
    }
}
