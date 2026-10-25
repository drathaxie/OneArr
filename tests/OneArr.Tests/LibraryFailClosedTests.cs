using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using OneArr.Domain;
using OneArr.Infrastructure.Library;
using Xunit;

namespace OneArr.Tests;

/// <summary>
/// Automatic grabbing must fail closed: it starts only after a complete apply reviewed every monitored entry,
/// and it pauses when any part of the library is unknown. Partial scans never turn into "missing" files.
/// </summary>
public class LibraryFailClosedTests
{
    private const string MovieRoot = "/media/movies";
    private const string TvRoot = "/media/tv";

    private static async Task<TestWorld> WorldAsync(params (MediaType Type, string Path)[] roots)
    {
        var world = new TestWorld();
        await world.ConfigureAsync(automaticGrab: true);
        await using var db = world.Db.CreateDbContext();
        foreach (var (type, path) in roots)
            db.LibraryRoots.Add(new LibraryRoot { MediaType = type, Path = path, PathKey = LibraryPaths.Key(path, false) });
        await db.SaveChangesAsync();
        return world;
    }

    private static LibraryReconciler Reconciler(TestWorld world, FakeLibraryFs fs) => new(world.Db, fs, world.Time);

    private static FakeLibraryFs OnlineFs(params string[] roots)
    {
        var fs = new FakeLibraryFs();
        foreach (var root in roots) fs.RootStates[root] = LibraryRootState.Accessible;
        return fs;
    }

    private static async Task<ApplyResult> ScanAndApplyAsync(LibraryReconciler reconciler, bool acknowledgeUnmatched = false)
    {
        var preview = await reconciler.PreviewAsync(default);
        return await reconciler.ApplyAsync(preview.PlanId, null, acknowledgeUnmatched, default);
    }

    private static async Task AddMonitoredMovieAsync(TestWorld world, string title, int year)
    {
        await world.AddMovieAsync(title, year);
    }

    [Fact]
    public async Task A_catalog_imported_before_reconciliation_blocks_automation_even_with_folders_scanned()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var fs = OnlineFs(MovieRoot);
        await ScanAndApplyAsync(Reconciler(world, fs));
        await AddMonitoredMovieAsync(world, "Dune", 2021);

        var gate = await Reconciler(world, fs).CheckAutomationAsync(default);

        Assert.False(gate.Allowed);
        Assert.Contains("not been checked", gate.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_folders_monitored_entries_block_and_an_empty_catalog_is_allowed()
    {
        var empty = await WorldAsync();
        Assert.True((await Reconciler(empty, new FakeLibraryFs()).CheckAutomationAsync(default)).Allowed);

        var withItems = await WorldAsync();
        await AddMonitoredMovieAsync(withItems, "Dune", 2021);
        var gate = await Reconciler(withItems, new FakeLibraryFs()).CheckAutomationAsync(default);

        Assert.False(gate.Allowed);
        Assert.Contains("Add your library folders", gate.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_newly_configured_folder_that_was_never_scanned_blocks_automation()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var fs = OnlineFs(MovieRoot);
        await AddMonitoredMovieAsync(world, "Dune", 2021);
        await ScanAndApplyAsync(Reconciler(world, fs));
        await AddFolderAsync(world, MediaType.Series, TvRoot);
        fs.RootStates[TvRoot] = LibraryRootState.Accessible;

        var gate = await Reconciler(world, fs).CheckAutomationAsync(default);

        Assert.False(gate.Allowed);
        Assert.Contains("no complete scan", gate.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_a_folder_path_requires_a_new_complete_scan()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var fs = OnlineFs(MovieRoot, "/media/films");
        await AddMonitoredMovieAsync(world, "Dune", 2021);
        var reconciler = Reconciler(world, fs);
        await ScanAndApplyAsync(reconciler);

        await using (var db = world.Db.CreateDbContext())
        {
            db.LibraryRoots.RemoveRange(db.LibraryRoots);
            db.LibraryRoots.Add(new LibraryRoot { MediaType = MediaType.Movie, Path = "/media/films", PathKey = LibraryPaths.Key("/media/films", false) });
            await db.SaveChangesAsync();
        }

        var gate = await reconciler.CheckAutomationAsync(default);
        Assert.False(gate.Allowed);
    }

    [Fact]
    public async Task Changing_a_path_mapping_requires_a_new_complete_scan()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var fs = OnlineFs(MovieRoot);
        await AddMonitoredMovieAsync(world, "Dune", 2021);
        var reconciler = Reconciler(world, fs);
        await ScanAndApplyAsync(reconciler);
        Assert.True((await reconciler.CheckAutomationAsync(default)).Allowed);

        await using (var db = world.Db.CreateDbContext())
        {
            db.PathMappings.Add(new PathMapping { MediaType = MediaType.Movie, SourcePrefix = "/data/media/movies", TargetPrefix = MovieRoot });
            await db.SaveChangesAsync();
        }

        var gate = await reconciler.CheckAutomationAsync(default);
        Assert.False(gate.Allowed);
        Assert.Contains("path mappings changed", gate.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_previously_scanned_library_that_goes_offline_blocks_automation()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var fs = OnlineFs(MovieRoot);
        await AddMonitoredMovieAsync(world, "Dune", 2021);
        var reconciler = Reconciler(world, fs);
        await ScanAndApplyAsync(reconciler);

        fs.RootStates[MovieRoot] = LibraryRootState.Unavailable;
        var gate = await reconciler.CheckAutomationAsync(default);

        Assert.False(gate.Allowed);
        Assert.Contains("not reachable", gate.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Existing_files_not_yet_linked_block_until_the_operator_acknowledges_them()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var fs = OnlineFs(MovieRoot);
        await AddMonitoredMovieAsync(world, "Dune", 2021);
        fs.Put(MovieRoot, "Mystery (2001)/Mystery.mkv");
        var reconciler = Reconciler(world, fs);

        var unacknowledged = await ScanAndApplyAsync(reconciler);
        Assert.False(unacknowledged.Automation.Allowed);
        Assert.False((await reconciler.CheckAutomationAsync(default)).Allowed);

        var acknowledged = await ScanAndApplyAsync(reconciler, acknowledgeUnmatched: true);
        Assert.True(acknowledged.Automation.Allowed, acknowledged.Automation.Reason);
    }

    [Fact]
    public async Task Ambiguous_matches_block_automation_until_they_are_resolved()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var fs = OnlineFs(MovieRoot);
        await AddMonitoredMovieAsync(world, "Dune", 2021);
        await AddMonitoredMovieAsync(world, "Dune", 2021);
        fs.Put(MovieRoot, "Dune (2021)/Dune.mkv");

        var result = await ScanAndApplyAsync(Reconciler(world, fs), acknowledgeUnmatched: true);

        Assert.False(result.Automation.Allowed);
        Assert.Contains("more than one catalog entry", result.Automation.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_complete_apply_of_a_fully_reviewed_catalog_allows_automation()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var fs = OnlineFs(MovieRoot);
        await AddMonitoredMovieAsync(world, "Dune", 2021);
        fs.Put(MovieRoot, "Dune (2021)/Dune (2021).mkv");

        var result = await ScanAndApplyAsync(Reconciler(world, fs));

        Assert.True(result.Complete);
        Assert.True(result.Automation.Allowed, result.Automation.Reason);
        Assert.Equal(1, result.ReviewedItems);
    }

    [Fact]
    public async Task Applying_a_scan_never_turns_automatic_grabbing_on()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        await world.Settings.SetAsync(SettingKeys.AutomaticGrab, "false");
        var fs = OnlineFs(MovieRoot);
        await AddMonitoredMovieAsync(world, "Dune", 2021);
        fs.Put(MovieRoot, "Dune (2021)/Dune (2021).mkv");

        await ScanAndApplyAsync(Reconciler(world, fs));

        Assert.False(await world.Settings.GetBoolAsync(SettingKeys.AutomaticGrab, defaultValue: true));
    }

    [Fact]
    public async Task A_partial_scan_never_reviews_the_catalog_and_keeps_automation_paused()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var fs = OnlineFs(MovieRoot);
        var movie = await world.AddMovieAsync("Dune", 2021, hasFile: true);
        fs.Put(MovieRoot, "Dune (2021)/Dune (2021).mkv");
        fs.MakeUnreadable(MovieRoot, "Locked");

        var result = await ScanAndApplyAsync(Reconciler(world, fs));

        Assert.False(result.Complete);
        Assert.Equal(0, result.ReviewedItems);
        Assert.False(result.Automation.Allowed);
        await using var db = world.Db.CreateDbContext();
        Assert.Null((await db.MediaItems.FindAsync(movie))!.LibraryReviewedUtc);
    }

    [Fact]
    public async Task Files_in_an_unreadable_folder_become_unknown_not_missing_and_keep_hasfile()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var fs = OnlineFs(MovieRoot);
        var dune = await AddMovieAsync(world, "Dune", 2021);
        var arrival = await AddMovieAsync(world, "Arrival", 2016);
        fs.Put(MovieRoot, "Dune (2021)/Dune (2021).mkv");
        fs.Put(MovieRoot, "Arrival (2016)/Arrival (2016).mkv");
        var reconciler = Reconciler(world, fs);
        await ScanAndApplyAsync(reconciler);

        fs.MakeUnreadable(MovieRoot, "Arrival (2016)");
        var result = await ScanAndApplyAsync(reconciler);

        Assert.Equal(0, result.Linked + result.Relinked);
        Assert.Equal(0, result.MarkedMissing);
        Assert.Equal(1, result.MarkedUnknown);
        await using var db = world.Db.CreateDbContext();
        var rows = await db.MediaFiles.ToListAsync();
        Assert.Equal(MediaFileStatus.Unknown, rows.Single(r => r.MediaItemId == arrival).Status);
        Assert.Equal(MediaFileStatus.Present, rows.Single(r => r.MediaItemId == dune).Status);
        Assert.True((await db.MediaItems.FindAsync(arrival))!.HasFile);
        Assert.False(result.Complete);
    }

    [Fact]
    public async Task An_undecidable_file_is_unknown_and_recovers_when_the_disk_answers_again()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var fs = OnlineFs(MovieRoot);
        var movie = await AddMovieAsync(world, "Dune", 2021);
        fs.Put(MovieRoot, "Dune (2021)/Dune (2021).mkv");
        var reconciler = Reconciler(world, fs);
        await ScanAndApplyAsync(reconciler);

        fs.Delete(MovieRoot, "Dune (2021)/Dune (2021).mkv");
        fs.Put(MovieRoot, "Dune (2021)/Dune (2021).mkv");
        fs.HideFromEnumeration(MovieRoot, "Dune (2021)/Dune (2021).mkv");
        fs.MakeUndecidable(MovieRoot, "Dune (2021)/Dune (2021).mkv");
        var unknown = await ScanAndApplyAsync(reconciler);

        Assert.Equal(1, unknown.MarkedUnknown);
        Assert.Equal(0, unknown.MarkedMissing);
        await using (var db = world.Db.CreateDbContext())
        {
            Assert.Equal(MediaFileStatus.Unknown, (await db.MediaFiles.SingleAsync()).Status);
            Assert.True((await db.MediaItems.FindAsync(movie))!.HasFile);
        }

        fs.Put(MovieRoot, "Dune (2021)/Dune (2021).mkv");
        await ScanAndApplyAsync(reconciler);
        await using var check = world.Db.CreateDbContext();
        Assert.Equal(MediaFileStatus.Present, (await check.MediaFiles.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_cancelled_preview_writes_nothing()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var fs = OnlineFs(MovieRoot);
        await AddMonitoredMovieAsync(world, "Dune", 2021);
        fs.Put(MovieRoot, "Dune (2021)/Dune (2021).mkv");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reconciler(world, fs).PreviewAsync(cts.Token));

        await using var db = world.Db.CreateDbContext();
        Assert.Empty(db.LibraryScanPlans);
        Assert.Empty(db.MediaFiles);
    }

    [Fact]
    public async Task A_folder_whose_root_is_unreadable_is_never_read_as_missing()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var fs = OnlineFs(MovieRoot);
        await AddMovieAsync(world, "Dune", 2021);
        fs.Put(MovieRoot, "Dune (2021)/Dune (2021).mkv");
        var reconciler = Reconciler(world, fs);
        await ScanAndApplyAsync(reconciler);

        fs.RootStates[MovieRoot] = LibraryRootState.Unavailable;
        var result = await ScanAndApplyAsync(reconciler);

        Assert.Equal(0, result.MarkedMissing);
        Assert.Equal(0, result.MarkedUnknown);
        Assert.False(result.Complete);
    }

    private static async Task AddFolderAsync(TestWorld world, MediaType type, string path)
    {
        await using var db = world.Db.CreateDbContext();
        db.LibraryRoots.Add(new LibraryRoot { MediaType = type, Path = path, PathKey = LibraryPaths.Key(path, false) });
        await db.SaveChangesAsync();
    }

    private static async Task<int> AddMovieAsync(TestWorld world, string title, int year) => await world.AddMovieAsync(title, year);
}

/// <summary>The real walk and probe, with the disk replaced by a test listing where needed.</summary>
public class LibraryWalkTests
{
    private static LibraryEntry FileEntry(string path, long size = 10) => new(path, IsDirectory: false, IsReparsePoint: false, SizeBytes: size);
    private static LibraryEntry Dir(string path) => new(path, IsDirectory: true, IsReparsePoint: false, SizeBytes: 0);
    private static LibraryEntry Link(string path) => new(path, IsDirectory: true, IsReparsePoint: true, SizeBytes: 0);

    [Fact]
    public async Task An_unreadable_folder_is_reported_and_the_rest_of_the_library_is_still_read()
    {
        var listing = new Dictionary<string, LibraryEntry[]>
        {
            ["/m"] = [Dir("/m/Locked"), Dir("/m/Open")],
            ["/m/Open"] = [FileEntry("/m/Open/Open (2000).mkv")],
        };
        IEnumerable<LibraryEntry> List(string dir) => dir == "/m/Locked" ? throw new UnauthorizedAccessException() : listing[dir];

        var result = LocalLibraryFileSystem.Walk("/m", List, 100, default);

        Assert.False(result.Complete);
        Assert.Equal(["/m/Locked"], result.UnreadableFolders);
        Assert.Single(result.Files);
    }

    [Fact]
    public void Junctions_and_symlinks_are_counted_and_never_followed()
    {
        var listing = new Dictionary<string, LibraryEntry[]>
        {
            ["/m"] = [Link("/m/Outside"), FileEntry("/m/Real (2000).mkv")],
        };
        var visited = new List<string>();
        IEnumerable<LibraryEntry> List(string dir) { visited.Add(dir); return listing[dir]; }

        var result = LocalLibraryFileSystem.Walk("/m", List, 100, default);

        Assert.Equal(1, result.SkippedReparsePoints);
        Assert.Equal(["/m"], visited);
        Assert.True(result.Complete);
    }

    [Fact]
    public void Reaching_the_file_cap_makes_the_walk_incomplete_and_marks_the_root_unreadable()
    {
        var listing = new Dictionary<string, LibraryEntry[]> { ["/m"] = [FileEntry("/m/a.mkv"), FileEntry("/m/b.mkv"), FileEntry("/m/c.mkv")] };

        var result = LocalLibraryFileSystem.Walk("/m", d => listing[d], maxFiles: 2, default);

        Assert.False(result.Complete);
        Assert.Contains("/m", result.UnreadableFolders);
        Assert.Contains("stopped early", result.IncompleteReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_a_walk_stops_it_at_the_next_folder()
    {
        using var cts = new CancellationTokenSource();
        var listing = new Dictionary<string, LibraryEntry[]> { ["/m"] = [Dir("/m/a"), Dir("/m/b")], ["/m/a"] = [], ["/m/b"] = [] };
        var calls = 0;
        IEnumerable<LibraryEntry> List(string dir)
        {
            if (++calls == 2) cts.Cancel();
            return listing[dir];
        }

        Assert.Throws<OperationCanceledException>(() => LocalLibraryFileSystem.Walk("/m", List, 100, cts.Token));
        Assert.Equal(2, calls);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_listing_that_never_returns_is_abandoned_at_the_time_limit()
    {
        var release = new ManualResetEventSlim(false);
        var fs = new LocalLibraryFileSystem(TimeSpan.FromMilliseconds(200), 100, _ => { release.Wait(); return []; });
        var clock = Stopwatch.StartNew();

        var result = await fs.EnumerateAsync(Path.GetTempPath(), default);
        clock.Stop();
        release.Set();

        Assert.False(result.Complete);
        Assert.Contains("stopped", result.IncompleteReason, StringComparison.Ordinal);
        Assert.True(clock.ElapsedMilliseconds < 5_000, $"took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Cancelling_an_enumeration_returns_promptly_even_when_the_listing_is_stuck()
    {
        var release = new ManualResetEventSlim(false);
        var fs = new LocalLibraryFileSystem(TimeSpan.FromMinutes(5), 100, _ => { release.Wait(); return []; });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fs.EnumerateAsync(Path.GetTempPath(), cts.Token));
        clock.Stop();
        release.Set();

        Assert.True(clock.ElapsedMilliseconds < 5_000, $"took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_missing_file_in_a_readable_folder_is_missing_and_one_in_a_missing_folder_is_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "onearr-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "here.mkv"), "x");
            var fs = new LocalLibraryFileSystem();

            Assert.Equal(FileProbeState.Exists, (await fs.ProbeFileAsync(Path.Combine(dir, "here.mkv"), default)).State);
            Assert.Equal(FileProbeState.Missing, (await fs.ProbeFileAsync(Path.Combine(dir, "gone.mkv"), default)).State);
            Assert.Equal(FileProbeState.Missing, (await fs.ProbeFileAsync(Path.Combine(dir, "folder-gone", "x.mkv"), default)).State);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
