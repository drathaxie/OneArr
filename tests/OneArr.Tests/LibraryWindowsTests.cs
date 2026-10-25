using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using OneArr.Domain;
using OneArr.Infrastructure.Library;
using Xunit;

namespace OneArr.Tests;

/// <summary>
/// Real NTFS fixtures: disposable folders, junctions, drive letters. Run only on Windows (Platform=Windows).
/// Each test checks that the scan leaves every file exactly as it was.
/// </summary>
[Trait("Platform", "Windows")]
[SupportedOSPlatform("windows")]
public class LibraryWindowsTests
{
    private static void RequireWindows() =>
        Assert.True(OperatingSystem.IsWindows(), "Platform=Windows tests must run on Windows");

    [Fact]
    public async Task Scan_links_movies_and_episodes_and_leaves_every_file_unchanged()
    {
        RequireWindows();
        using var tree = new TempTree();
        tree.WriteFile(@"movies\Dune (2021)\Dune (2021) 1080p.mkv", "movie-bytes");
        tree.WriteFile(@"tv\Show\Season 01\Show - S01E01.mkv", "episode-one");
        tree.WriteFile(@"tv\Show\Season 01\Show - S01E02.mkv", "episode-two");
        tree.WriteFile(@"movies\Dune (2021)\notes.txt", "not media");
        var before = Snapshot(tree.Root);

        var world = new TestWorld();
        await world.ConfigureAsync(automaticGrab: false);
        var movie = await world.AddMovieAsync("Dune", 2021);
        var (show, _) = await world.AddSeriesAsync("Show", 2);
        await AddRootAsync(world, MediaType.Movie, Path.Combine(tree.Root, "movies"));
        await AddRootAsync(world, MediaType.Series, Path.Combine(tree.Root, "tv"));
        var reconciler = new LibraryReconciler(world.Db, new LocalLibraryFileSystem(), world.Time);

        var preview = await reconciler.PreviewAsync(default);
        Assert.Equal(3, preview.Plan.Links.Count);
        Assert.Equal(before, Snapshot(tree.Root));

        var applied = await reconciler.ApplyAsync(preview.PlanId, null, default);
        Assert.Equal(3, applied.Linked);
        Assert.Equal(before, Snapshot(tree.Root));

        await using var db = world.Db.CreateDbContext();
        Assert.True((await db.MediaItems.FindAsync(movie))!.HasFile);
        Assert.True((await db.MediaItems.FindAsync(show))!.HasFile);
        Assert.Equal(2, await db.Episodes.CountAsync(e => e.MediaItemId == show && e.HasFile));
    }

    [Fact]
    public async Task Repeated_scans_on_disk_are_idempotent()
    {
        RequireWindows();
        using var tree = new TempTree();
        tree.WriteFile(@"movies\Dune (2021)\Dune (2021).mkv", "movie");
        var world = new TestWorld();
        await world.ConfigureAsync(automaticGrab: false);
        await world.AddMovieAsync("Dune", 2021);
        await AddRootAsync(world, MediaType.Movie, Path.Combine(tree.Root, "movies"));
        var reconciler = new LibraryReconciler(world.Db, new LocalLibraryFileSystem(), world.Time);

        var first = await reconciler.PreviewAsync(default);
        await reconciler.ApplyAsync(first.PlanId, null, default);
        var second = await reconciler.PreviewAsync(default);

        Assert.Empty(second.Plan.Links);
        Assert.Equal(1, second.Plan.AlreadyLinked);
        await using var db = world.Db.CreateDbContext();
        Assert.Single(db.MediaFiles);
    }

    [Fact]
    public async Task Junctions_are_never_followed_so_files_outside_the_root_are_not_linked()
    {
        RequireWindows();
        using var tree = new TempTree();
        tree.WriteFile(@"outside\Outside Movie (2000)\Outside Movie (2000).mkv", "outside");
        var movies = Path.Combine(tree.Root, "movies");
        Directory.CreateDirectory(movies);
        Run("cmd.exe", $"/c mklink /J \"{Path.Combine(movies, "linked")}\" \"{Path.Combine(tree.Root, "outside")}\"");

        var enumeration = await new LocalLibraryFileSystem().EnumerateAsync(movies, default);
        Assert.Empty(enumeration.Files);
        Assert.True(enumeration.SkippedReparsePoints >= 1);

        var world = new TestWorld();
        await world.ConfigureAsync(automaticGrab: false);
        await world.AddMovieAsync("Outside Movie", 2000);
        await AddRootAsync(world, MediaType.Movie, movies);
        var preview = await new LibraryReconciler(world.Db, new LocalLibraryFileSystem(), world.Time).PreviewAsync(default);
        Assert.Empty(preview.Plan.Links);
    }

    [Fact]
    public async Task A_missing_folder_is_unavailable_and_nothing_under_it_is_marked_missing()
    {
        RequireWindows();
        using var tree = new TempTree();
        var root = Path.Combine(tree.Root, "movies");
        var world = new TestWorld();
        await world.ConfigureAsync(automaticGrab: false);
        var movie = await world.AddMovieAsync("Dune", 2021, hasFile: true);
        await AddRootAsync(world, MediaType.Movie, root);
        await using (var db = world.Db.CreateDbContext())
        {
            db.MediaFiles.Add(new MediaFile { Path = Path.Combine(root, "Dune (2021)", "Dune.mkv"), PathKey = LibraryPaths.Key(Path.Combine(root, "Dune (2021)", "Dune.mkv"), true), MediaItemId = movie, Status = MediaFileStatus.Present });
            await db.SaveChangesAsync();
        }

        var check = await new LocalLibraryFileSystem().CheckRootAsync(root, default);
        Assert.Equal(LibraryRootState.Unavailable, check.State);
        Assert.Contains("not found", check.Message, StringComparison.OrdinalIgnoreCase);

        var preview = await new LibraryReconciler(world.Db, new LocalLibraryFileSystem(), world.Time).PreviewAsync(default);
        Assert.Empty(preview.Plan.Missing);
        Assert.Equal(1, preview.Plan.FilesUnderUnavailableRoots);
    }

    [Fact]
    public async Task A_drive_letter_that_is_not_present_is_reported_as_unavailable()
    {
        RequireWindows();
        var drives = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var free = "ZYXWVUTSRQPONMLKJIHGFEDCBA".First(c => !drives.Contains(c));

        var check = await new LocalLibraryFileSystem().CheckRootAsync($"{free}:\\Media\\Movies", default);

        Assert.Equal(LibraryRootState.Unavailable, check.State);
        Assert.Contains("not ready", check.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Windows_paths_are_validated_for_drive_letters_and_shares_only()
    {
        RequireWindows();
        Assert.NotNull(LibraryPaths.ValidateRoot("/data/media/movies", windows: true));
        Assert.Null(LibraryPaths.ValidateRoot(@"D:\Media\Movies", windows: true));
        Assert.Null(LibraryPaths.ValidateRoot(@"\\nas\media\tv", windows: true));
        Assert.NotNull(LibraryPaths.ValidateRoot(@"\\nas", windows: true));
    }

    [Fact]
    public void Drive_letter_and_folder_case_do_not_create_a_second_root()
    {
        RequireWindows();
        Assert.Equal(LibraryPaths.Key(@"D:\Media\Movies", true), LibraryPaths.Key(@"d:\MEDIA\movies\", true));
    }

    [Fact]
    public async Task Paths_with_different_case_reach_the_same_folder()
    {
        RequireWindows();
        using var tree = new TempTree();
        Directory.CreateDirectory(Path.Combine(tree.Root, "Movies"));

        var check = await new LocalLibraryFileSystem().CheckRootAsync(Path.Combine(tree.Root, "MOVIES").ToUpperInvariant(), default);

        Assert.Equal(LibraryRootState.Accessible, check.State);
    }

    private static async Task AddRootAsync(TestWorld world, MediaType type, string path)
    {
        await using var db = world.Db.CreateDbContext();
        db.LibraryRoots.Add(new LibraryRoot { MediaType = type, Path = path, PathKey = LibraryPaths.Key(path, true) });
        await db.SaveChangesAsync();
    }

    /// <summary>Every path under the root with its size, write time and content hash. Equal snapshots mean nothing changed.</summary>
    private static Dictionary<string, string> Snapshot(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var key = Path.GetRelativePath(root, entry);
            if (Directory.Exists(entry))
            {
                map[key] = "dir";
                continue;
            }
            var info = new FileInfo(entry);
            map[key] = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry)))}";
        }
        return map;
    }

    private static void Run(string file, string args)
    {
        using var process = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
