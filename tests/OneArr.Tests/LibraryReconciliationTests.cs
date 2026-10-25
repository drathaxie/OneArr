using Microsoft.EntityFrameworkCore;
using OneArr.Domain;
using OneArr.Infrastructure.Library;
using Xunit;

namespace OneArr.Tests;

/// <summary>Pure matching and path rules. No file system or database, so these run on every OS.</summary>
public class LibraryMatcherTests
{
    private const string Root = "/media/movies";
    private static readonly IReadOnlyList<CatalogSeries> NoSeries = [];

    private static CandidateFile File(string relative, string root = Root) => new(root + "/" + relative, relative, 1000);

    private static FileMatch Movie(string relative, params CatalogMovie[] movies) =>
        LibraryMatcher.Match(File(relative), MediaType.Movie, movies, NoSeries, windows: false);

    private static FileMatch Episode(string relative, params CatalogSeries[] series) =>
        LibraryMatcher.Match(File(relative, "/media/tv"), MediaType.Series, [], series, windows: false);

    private static CatalogSeries Show(int id, string title, int? year, string? tmdb, string? legacy, params (int S, int E)[] episodes) =>
        new(id, title, year, tmdb, legacy, episodes.Select((e, i) => new CatalogEpisode(id * 100 + i, e.S, e.E)).ToList());

    [Fact]
    public void Movie_folder_with_year_links_by_title_and_year()
    {
        var m = Movie("Dune (2021)/Dune (2021) 1080p.mkv", new CatalogMovie(1, "Dune", 2021, null, null));
        Assert.Equal(MatchOutcome.Linked, m.Outcome);
        Assert.Equal(1, m.MediaItemId);
        Assert.Equal("title-year", m.Method);
        Assert.Equal(1080, m.Resolution);
    }

    [Fact]
    public void Dotted_file_name_without_folder_links_and_reads_4k()
    {
        var m = Movie("Dune.2021.2160p.BluRay.mkv", new CatalogMovie(1, "Dune", 2021, null, null));
        Assert.Equal(MatchOutcome.Linked, m.Outcome);
        Assert.Equal(2160, m.Resolution);
    }

    [Fact]
    public void Tmdb_id_in_folder_links_even_when_the_title_differs()
    {
        var m = Movie("Dune Part (2021) {tmdb-438631}/file.mkv", new CatalogMovie(7, "Dune", 2021, "438631", null));
        Assert.Equal(MatchOutcome.Linked, m.Outcome);
        Assert.Equal("tmdb-id", m.Method);
        Assert.Equal(7, m.MediaItemId);
    }

    [Fact]
    public void Tmdb_id_that_is_not_in_the_catalog_is_unmatched_and_does_not_fall_back_to_title()
    {
        var m = Movie("Dune (2021) {tmdb-1}/Dune.mkv", new CatalogMovie(1, "Dune", 2021, "438631", null));
        Assert.Equal(MatchOutcome.Unmatched, m.Outcome);
    }

    [Fact]
    public void Two_different_tmdb_ids_in_one_path_are_unmatched()
    {
        var m = Movie("Dune {tmdb-1}/Dune {tmdb-2}.mkv", new CatalogMovie(1, "Dune", 2021, "1", null));
        Assert.Equal(MatchOutcome.Unmatched, m.Outcome);
    }

    [Fact]
    public void Movie_without_a_year_is_never_guessed()
    {
        var m = Movie("Dune/Dune.mkv", new CatalogMovie(1, "Dune", 2021, null, null));
        Assert.Equal(MatchOutcome.Unmatched, m.Outcome);
    }

    [Fact]
    public void Same_title_and_year_twice_is_ambiguous_and_names_both_candidates()
    {
        var m = Movie("Dune (2021)/Dune.mkv", new CatalogMovie(1, "Dune", 2021, null, null), new CatalogMovie(2, "Dune", 2021, null, null));
        Assert.Equal(MatchOutcome.Ambiguous, m.Outcome);
        Assert.Equal([1, 2], m.CandidateIds);
        Assert.Null(m.MediaItemId);
    }

    [Fact]
    public void Movie_not_in_the_catalog_is_unmatched()
    {
        var m = Movie("Arrival (2016)/Arrival.mkv", new CatalogMovie(1, "Dune", 2021, null, null));
        Assert.Equal(MatchOutcome.Unmatched, m.Outcome);
    }

    [Fact]
    public void Sample_files_are_never_linked()
    {
        var m = Movie("Dune (2021)/sample.mkv", new CatalogMovie(1, "Dune", 2021, null, null));
        Assert.Equal(MatchOutcome.Unmatched, m.Outcome);
        Assert.Contains("sample", m.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Legacy_folder_wins_over_a_different_title_in_the_catalog()
    {
        var m = Movie("Dune (2021)/Dune.mkv", new CatalogMovie(5, "Something Else", 1999, null, "/media/movies/Dune (2021)"));
        Assert.Equal(MatchOutcome.Linked, m.Outcome);
        Assert.Equal("legacy-path", m.Method);
        Assert.Equal(5, m.MediaItemId);
    }

    [Fact]
    public void Two_versions_in_one_folder_both_match_the_same_movie()
    {
        var movie = new CatalogMovie(1, "Dune", 2021, null, null);
        var a = Movie("Dune (2021)/Dune.2160p.mkv", movie);
        var b = Movie("Dune (2021)/Dune.720p.mkv", movie);
        Assert.Equal(1, a.MediaItemId);
        Assert.Equal(1, b.MediaItemId);
        Assert.Equal(2160, a.Resolution);
        Assert.Equal(720, b.Resolution);
    }

    [Fact]
    public void Episode_links_by_series_folder_and_season_episode()
    {
        var show = Show(1, "Show", 2011, null, null, (1, 1), (1, 2));
        var m = Episode("Show/Season 01/Show - S01E02 - Title.mkv", show);
        Assert.Equal(MatchOutcome.Linked, m.Outcome);
        Assert.Equal([101], m.EpisodeIds);
    }

    [Theory]
    [InlineData("Show.S01E01E02.mkv", new[] { 1, 2 })]
    [InlineData("Show.S01E01-E02.mkv", new[] { 1, 2 })]
    [InlineData("Show.S01E01-03.1080p.mkv", new[] { 1, 2, 3 })]
    [InlineData("Show.1x03.mkv", new[] { 3 })]
    public void Multi_episode_and_alternate_forms_link_every_episode(string name, int[] numbers)
    {
        var show = Show(1, "Show", 2011, null, null, (1, 1), (1, 2), (1, 3));
        var m = Episode(name, show);
        Assert.Equal(MatchOutcome.Linked, m.Outcome);
        Assert.Equal(numbers.Select(n => 100 + n - 1), m.EpisodeIds);
    }

    [Fact]
    public void A_file_with_one_episode_missing_from_the_catalog_links_nothing()
    {
        var show = Show(1, "Show", 2011, null, null, (1, 1));
        var m = Episode("Show.S01E01E02.mkv", show);
        Assert.Equal(MatchOutcome.Unmatched, m.Outcome);
        Assert.Empty(m.EpisodeIds ?? []);
    }

    [Fact]
    public void Series_file_without_season_and_episode_is_unmatched()
    {
        var m = Episode("Show/extras.mkv", Show(1, "Show", 2011, null, null, (1, 1)));
        Assert.Equal(MatchOutcome.Unmatched, m.Outcome);
    }

    [Fact]
    public void Series_matched_by_title_only_links_when_unique_and_is_ambiguous_otherwise()
    {
        var one = LibraryMatcher.Match(File("Show/Show.S01E01.mkv", "/media/tv"), MediaType.Series, [], [Show(1, "Show", 2011, null, null, (1, 1))], windows: false);
        Assert.Equal(MatchOutcome.Linked, one.Outcome);
        Assert.Equal("title-only", one.Method);

        var two = LibraryMatcher.Match(File("Show/Show.S01E01.mkv", "/media/tv"), MediaType.Series, [],
            [Show(1, "Show", 2011, null, null, (1, 1)), Show(2, "Show", 2019, null, null, (1, 1))], windows: false);
        Assert.Equal(MatchOutcome.Ambiguous, two.Outcome);
    }

    [Fact]
    public void Series_legacy_folder_is_authoritative()
    {
        var a = Show(1, "Show", 2011, null, "/media/tv/Show (2011)", (1, 1));
        var b = Show(2, "Show", 2011, null, null, (1, 1));
        var m = LibraryMatcher.Match(File("Show (2011)/Season 01/Show.S01E01.mkv", "/media/tv"), MediaType.Series, [], [a, b], windows: false);
        Assert.Equal(MatchOutcome.Linked, m.Outcome);
        Assert.Equal(1, m.MediaItemId);
        Assert.Equal("legacy-path", m.Method);
    }

    [Theory]
    [InlineData("2001.A.Space.Odyssey.1968.1080p.mkv", "2001 A Space Odyssey", 1968)]
    [InlineData("Blade Runner 2049 (2017) {tmdb-335984}", "Blade Runner 2049", 2017)]
    [InlineData("Dune.2021.mkv", "Dune", 2021)]
    [InlineData("Dune.mkv", "Dune", null)]
    public void Title_and_year_are_read_from_common_name_shapes(string name, string title, int? year)
    {
        var parsed = LibraryMatcher.ParseTitle(name);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
    }

    [Fact]
    public void Episode_parser_expands_ranges_and_multi_episode_tags()
    {
        Assert.Equal([(1, 1), (1, 2)], LibraryMatcher.ParseEpisodes("Show.S01E01-E02.mkv"));
        Assert.Equal([(2, 5)], LibraryMatcher.ParseEpisodes("Show.S02E05.1080p.mkv"));
        Assert.Empty(LibraryMatcher.ParseEpisodes("Show.S01E01-1080p.mkv").Skip(1));
    }

    [Fact]
    public void Titles_compare_without_accents_case_or_punctuation()
    {
        Assert.Equal(LibraryMatcher.NormalizeTitle("Amélie: Édition"), LibraryMatcher.NormalizeTitle("amelie  edition"));
    }
}

public class LibraryPathRulesTests
{
    [Fact]
    public void On_windows_a_linux_path_is_rejected_with_guidance()
    {
        var problem = LibraryPaths.ValidateRoot("/data/media/movies", windows: true);
        Assert.NotNull(problem);
        Assert.Contains("Linux path", problem);
    }

    [Theory]
    [InlineData("D:\\Media\\Movies")]
    [InlineData("\\\\nas\\media\\tv")]
    public void On_windows_drive_and_share_paths_are_accepted(string path) =>
        Assert.Null(LibraryPaths.ValidateRoot(path, windows: true));

    [Theory]
    [InlineData("Media\\Movies")]
    [InlineData("D:Media")]
    [InlineData("D:\\Media\\..\\Secret")]
    [InlineData("\\\\nas")]
    public void On_windows_relative_traversal_and_incomplete_shares_are_rejected(string path) =>
        Assert.NotNull(LibraryPaths.ValidateRoot(path, windows: true));

    [Fact]
    public void On_linux_windows_paths_are_rejected()
    {
        Assert.NotNull(LibraryPaths.ValidateRoot("D:\\Media", windows: false));
        Assert.Null(LibraryPaths.ValidateRoot("/data/media", windows: false));
    }

    [Fact]
    public void Windows_keys_ignore_case_and_trailing_separators()
    {
        Assert.Equal(LibraryPaths.Key("D:\\MEDIA\\Movies\\", windows: true), LibraryPaths.Key("d:/media/movies", windows: true));
    }

    [Fact]
    public void Containment_respects_segment_boundaries()
    {
        Assert.True(LibraryPaths.IsWithin(LibraryPaths.Key("D:\\MOVIES\\Dune\\a.mkv", true), LibraryPaths.Key("D:\\Movies", true), true));
        Assert.False(LibraryPaths.IsWithin(LibraryPaths.Key("D:\\Movies2\\a.mkv", true), LibraryPaths.Key("D:\\Movies", true), true));
        Assert.False(LibraryPaths.IsWithin("/media/movies", "/media/movies", false));
    }

    [Fact]
    public void Legacy_paths_map_to_the_windows_folder_with_the_longest_prefix()
    {
        var mappings = new List<PathMapping>
        {
            new() { MediaType = MediaType.Series, SourcePrefix = "/data", TargetPrefix = "D:\\Data" },
            new() { MediaType = MediaType.Series, SourcePrefix = "/data/media", TargetPrefix = "E:\\Media" },
        };
        Assert.Equal("E:\\Media\\tv\\Show", LibraryPaths.MapLegacy("/data/media/tv/Show", mappings, windows: true));
        Assert.Equal("D:\\Data\\other", LibraryPaths.MapLegacy("/data/other", mappings, windows: true));
    }

    [Fact]
    public void A_path_that_only_shares_text_with_a_mapping_is_not_mapped()
    {
        var mappings = new List<PathMapping> { new() { MediaType = MediaType.Movie, SourcePrefix = "/data/media/movies", TargetPrefix = "D:\\Media\\Movies" } };
        Assert.Null(LibraryPaths.MapLegacy("/data/media/movies2/Dune", mappings, windows: true));
        Assert.Null(LibraryPaths.MapLegacy("/srv/other/Dune", mappings, windows: true));
    }

    [Fact]
    public void A_linux_path_is_never_returned_unmapped_on_windows()
    {
        Assert.Null(LibraryPaths.MapLegacy("/data/media/movies/Dune", [], windows: true));
        Assert.Equal("D:\\Old\\Movies\\Dune", LibraryPaths.MapLegacy("D:\\Old\\Movies\\Dune", [], windows: true));
    }
}

/// <summary>
/// An in-memory disk. Tests change it to simulate files appearing, vanishing, resizing, a share going offline,
/// a folder that cannot be read, or an I/O error on a single file.
/// </summary>
internal sealed class FakeLibraryFs : ILibraryFileSystem
{
    private readonly Dictionary<string, List<CandidateFile>> _files = new();
    private readonly HashSet<string> _existing = new();
    private readonly HashSet<string> _unreadable = new();
    private readonly HashSet<string> _undecidable = new();
    public Dictionary<string, LibraryRootState> RootStates { get; } = new();
    public bool Windows { get; set; }

    public Task<RootCheck> CheckRootAsync(string root, CancellationToken ct) =>
        Task.FromResult(RootStates.TryGetValue(root, out var s) && s == LibraryRootState.Accessible
            ? new RootCheck(LibraryRootState.Accessible, null)
            : new RootCheck(LibraryRootState.Unavailable, "offline"));

    public Task<LibraryEnumeration> EnumerateAsync(string root, CancellationToken ct)
    {
        var files = _files.TryGetValue(root, out var list) ? list.Where(f => !_unreadable.Any(u => f.FullPath.StartsWith(u + "/", StringComparison.Ordinal))).ToList() : [];
        var unreadable = _unreadable.Where(u => u.StartsWith(root + "/", StringComparison.Ordinal) || u == root).ToList();
        return Task.FromResult(new LibraryEnumeration(files, 0, unreadable, Complete: unreadable.Count == 0,
            IncompleteReason: unreadable.Count == 0 ? null : $"{unreadable.Count} folder(s) could not be read"));
    }

    public Task<FileProbe> ProbeFileAsync(string path, CancellationToken ct)
    {
        if (_undecidable.Contains(path)) return Task.FromResult(new FileProbe(FileProbeState.Unknown, "I/O error"));
        if (_unreadable.Any(u => path.StartsWith(u + "/", StringComparison.Ordinal))) return Task.FromResult(new FileProbe(FileProbeState.Unknown, "Access denied"));
        return Task.FromResult(new FileProbe(_existing.Contains(path) ? FileProbeState.Exists : FileProbeState.Missing));
    }

    public void Put(string root, string relative, long size = 1000)
    {
        var full = root + "/" + relative;
        if (!_files.TryGetValue(root, out var list)) _files[root] = list = [];
        list.RemoveAll(f => f.FullPath == full);
        list.Add(new CandidateFile(full, relative, size));
        _existing.Add(full);
    }

    /// <summary>Deletes the file. It is gone from the listing and from the disk.</summary>
    public void Delete(string root, string relative)
    {
        var full = root + "/" + relative;
        _files.GetValueOrDefault(root)?.RemoveAll(f => f.FullPath == full);
        _existing.Remove(full);
    }

    /// <summary>Hides the file from enumeration but leaves it on disk, like a file behind a junction.</summary>
    public void HideFromEnumeration(string root, string relative)
    {
        var full = root + "/" + relative;
        _files.GetValueOrDefault(root)?.RemoveAll(f => f.FullPath == full);
    }

    /// <summary>Makes a folder unreadable. Its files are not listed, and probes inside it are undecidable.</summary>
    public void MakeUnreadable(string root, string relativeFolder) => _unreadable.Add(root + "/" + relativeFolder);

    public void MakeUndecidable(string root, string relative) => _undecidable.Add(root + "/" + relative);
}

public class LibraryReconciliationTests
{
    private const string MovieRoot = "/media/movies";
    private const string TvRoot = "/media/tv";

    private static async Task<TestWorld> WorldAsync(params (MediaType Type, string Path)[] roots)
    {
        var world = new TestWorld();
        await world.ConfigureAsync(automaticGrab: false);
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

    private static async Task<ApplyResult> PreviewAndApplyAsync(LibraryReconciler reconciler, IReadOnlyList<string>? exclude = null)
    {
        var preview = await reconciler.PreviewAsync(default);
        return await reconciler.ApplyAsync(preview.PlanId, exclude, false, default);
    }

    [Fact]
    public async Task Preview_changes_no_media_rows_and_leaves_hasfile_alone()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var movie = await world.AddMovieAsync("Dune", 2021);
        var fs = OnlineFs(MovieRoot);
        fs.Put(MovieRoot, "Dune (2021)/Dune (2021).mkv");

        var preview = await Reconciler(world, fs).PreviewAsync(default);

        Assert.Single(preview.Plan.Links);
        await using var db = world.Db.CreateDbContext();
        Assert.Empty(db.MediaFiles);
        Assert.False((await db.MediaItems.FindAsync(movie))!.HasFile);
        Assert.Single(db.LibraryScanPlans);
    }

    [Fact]
    public async Task Apply_links_the_file_and_sets_hasfile()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var movie = await world.AddMovieAsync("Dune", 2021);
        var fs = OnlineFs(MovieRoot);
        fs.Put(MovieRoot, "Dune (2021)/Dune (2021).mkv", 4242);

        var result = await PreviewAndApplyAsync(Reconciler(world, fs));

        Assert.Equal(1, result.Linked);
        await using var db = world.Db.CreateDbContext();
        var row = await db.MediaFiles.SingleAsync();
        Assert.Equal(movie, row.MediaItemId);
        Assert.Equal(MediaFileStatus.Present, row.Status);
        Assert.Equal(4242, row.SizeBytes);
        Assert.True((await db.MediaItems.FindAsync(movie))!.HasFile);
        Assert.NotNull((await db.LibraryRoots.SingleAsync()).LastSuccessfulScanUtc);
    }

    [Fact]
    public async Task Rescanning_is_idempotent_and_never_duplicates_rows()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        await world.AddMovieAsync("Dune", 2021);
        var fs = OnlineFs(MovieRoot);
        fs.Put(MovieRoot, "Dune (2021)/Dune (2021).mkv");
        var reconciler = Reconciler(world, fs);
        await PreviewAndApplyAsync(reconciler);

        var second = await reconciler.PreviewAsync(default);
        Assert.Empty(second.Plan.Links);
        Assert.Equal(1, second.Plan.AlreadyLinked);
        var applied = await reconciler.ApplyAsync(second.PlanId, null, false, default);

        Assert.Equal(0, applied.Linked + applied.Relinked);
        await using var db = world.Db.CreateDbContext();
        Assert.Single(db.MediaFiles);
    }

    [Fact]
    public async Task Series_episodes_link_individually_and_series_hasfile_follows()
    {
        var world = await WorldAsync((MediaType.Series, TvRoot));
        var (seriesId, episodeIds) = await world.AddSeriesAsync("Show", 3);
        var fs = OnlineFs(TvRoot);
        fs.Put(TvRoot, "Show/Season 01/Show - S01E01.mkv");
        fs.Put(TvRoot, "Show/Season 01/Show - S01E02.mkv");

        await PreviewAndApplyAsync(Reconciler(world, fs));

        await using var db = world.Db.CreateDbContext();
        var eps = await db.Episodes.Where(e => e.MediaItemId == seriesId).OrderBy(e => e.EpisodeNumber).ToListAsync();
        Assert.True(eps[0].HasFile);
        Assert.True(eps[1].HasFile);
        Assert.False(eps[2].HasFile);
        Assert.True((await db.MediaItems.FindAsync(seriesId))!.HasFile);
        Assert.Equal(episodeIds[0], eps[0].Id);
    }

    [Fact]
    public async Task A_path_mapping_correction_moves_a_file_to_the_right_series_and_clears_the_old_one()
    {
        var world = await WorldAsync((MediaType.Series, TvRoot));
        var (wrong, wrongEpisodes) = await world.AddSeriesAsync("Show", 1);
        var (right, rightEpisodes) = await world.AddSeriesAsync("Show", 1);
        await using (var db = world.Db.CreateDbContext())
        {
            (await db.MediaItems.FindAsync(wrong))!.LegacyPath = "/media/tv/Show (2011)";
            db.PathMappings.Add(new PathMapping { MediaType = MediaType.Series, SourcePrefix = "/media/tv", TargetPrefix = TvRoot });
            await db.SaveChangesAsync();
        }
        var fs = OnlineFs(TvRoot);
        fs.Put(TvRoot, "Show (2011)/Show - S01E01.mkv");
        var reconciler = Reconciler(world, fs);
        await PreviewAndApplyAsync(reconciler);

        await using (var db = world.Db.CreateDbContext())
        {
            (await db.MediaItems.FindAsync(wrong))!.LegacyPath = null;
            (await db.MediaItems.FindAsync(right))!.LegacyPath = "/media/tv/Show (2011)";
            await db.SaveChangesAsync();
        }
        var result = await PreviewAndApplyAsync(reconciler);

        Assert.Equal(1, result.Relinked);
        await using var check = world.Db.CreateDbContext();
        var row = await check.MediaFiles.Include(f => f.EpisodeLinks).SingleAsync();
        Assert.Equal(right, row.MediaItemId);
        Assert.Equal([rightEpisodes[0]], row.EpisodeLinks.Select(l => l.EpisodeId).ToArray());
        Assert.True((await check.Episodes.FindAsync(rightEpisodes[0]))!.HasFile);
        Assert.False((await check.Episodes.FindAsync(wrongEpisodes[0]))!.HasFile);
        Assert.False((await check.MediaItems.FindAsync(wrong))!.HasFile);
        Assert.True((await check.MediaItems.FindAsync(right))!.HasFile);
    }

    [Fact]
    public async Task Offline_root_never_marks_media_missing()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var movie = await world.AddMovieAsync("Dune", 2021);
        var fs = OnlineFs(MovieRoot);
        fs.Put(MovieRoot, "Dune (2021)/Dune (2021).mkv");
        var reconciler = Reconciler(world, fs);
        await PreviewAndApplyAsync(reconciler);

        fs.RootStates[MovieRoot] = LibraryRootState.Unavailable;
        var preview = await reconciler.PreviewAsync(default);
        Assert.Empty(preview.Plan.Missing);
        Assert.Equal(LibraryRootState.Unavailable, preview.Plan.Roots.Single().State);
        Assert.Equal(1, preview.Plan.FilesUnderUnavailableRoots);

        await reconciler.ApplyAsync(preview.PlanId, null, false, default);
        await using var db = world.Db.CreateDbContext();
        Assert.True((await db.MediaItems.FindAsync(movie))!.HasFile);
        Assert.Equal(MediaFileStatus.Inaccessible, (await db.MediaFiles.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_file_confirmed_missing_on_an_online_root_clears_hasfile()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var movie = await world.AddMovieAsync("Dune", 2021);
        var fs = OnlineFs(MovieRoot);
        fs.Put(MovieRoot, "Dune (2021)/Dune (2021).mkv");
        var reconciler = Reconciler(world, fs);
        await PreviewAndApplyAsync(reconciler);

        fs.Delete(MovieRoot, "Dune (2021)/Dune (2021).mkv");
        var preview = await reconciler.PreviewAsync(default);
        Assert.Single(preview.Plan.Missing);
        await reconciler.ApplyAsync(preview.PlanId, null, false, default);

        await using var db = world.Db.CreateDbContext();
        Assert.Equal(MediaFileStatus.Missing, (await db.MediaFiles.SingleAsync()).Status);
        Assert.False((await db.MediaItems.FindAsync(movie))!.HasFile);
    }

    [Fact]
    public async Task A_file_that_still_exists_but_is_not_enumerated_is_not_missing()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        await world.AddMovieAsync("Dune", 2021);
        var fs = OnlineFs(MovieRoot);
        fs.Put(MovieRoot, "Dune (2021)/Dune (2021).mkv");
        var reconciler = Reconciler(world, fs);
        await PreviewAndApplyAsync(reconciler);

        fs.HideFromEnumeration(MovieRoot, "Dune (2021)/Dune (2021).mkv");
        var preview = await reconciler.PreviewAsync(default);
        Assert.Empty(preview.Plan.Missing);
    }

    [Fact]
    public async Task A_download_import_is_never_undone_by_a_missing_link()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var movie = await world.AddMovieAsync("Arrival", 2016, hasFile: true);
        var fs = OnlineFs(MovieRoot);
        fs.Put(MovieRoot, "Arrival (2016)/Arrival (2016).mkv");
        var reconciler = Reconciler(world, fs);
        await PreviewAndApplyAsync(reconciler);
        await using (var db = world.Db.CreateDbContext())
        {
            db.DownloadJobs.Add(new DownloadJob { MediaItemId = movie, MediaType = MediaType.Movie, Status = DownloadStatus.Imported, ReleaseTitle = "Arrival" });
            await db.SaveChangesAsync();
        }

        fs.Delete(MovieRoot, "Arrival (2016)/Arrival (2016).mkv");
        await PreviewAndApplyAsync(reconciler);

        await using var check = world.Db.CreateDbContext();
        Assert.True((await check.MediaItems.FindAsync(movie))!.HasFile);
    }

    [Fact]
    public async Task Ambiguous_and_unmatched_files_are_never_written()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var a = await world.AddMovieAsync("Dune", 2021);
        await world.AddMovieAsync("Dune", 2021);
        var fs = OnlineFs(MovieRoot);
        fs.Put(MovieRoot, "Dune (2021)/Dune.mkv");
        fs.Put(MovieRoot, "Mystery (2001)/Mystery.mkv");

        var preview = await Reconciler(world, fs).PreviewAsync(default);
        Assert.Single(preview.Plan.Ambiguous);
        Assert.Single(preview.Plan.Unmatched);
        await Reconciler(world, fs).ApplyAsync(preview.PlanId, null, false, default);

        await using var db = world.Db.CreateDbContext();
        Assert.Empty(db.MediaFiles);
        Assert.False((await db.MediaItems.FindAsync(a))!.HasFile);
    }

    [Fact]
    public async Task A_stale_preview_skips_a_file_that_changed_size()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        await world.AddMovieAsync("Dune", 2021);
        var fs = OnlineFs(MovieRoot);
        fs.Put(MovieRoot, "Dune (2021)/Dune (2021).mkv", 100);
        var reconciler = Reconciler(world, fs);
        var preview = await reconciler.PreviewAsync(default);

        fs.Put(MovieRoot, "Dune (2021)/Dune (2021).mkv", 999);
        var applied = await reconciler.ApplyAsync(preview.PlanId, null, false, default);

        Assert.Equal(0, applied.Linked);
        Assert.Single(applied.Skipped);
        await using var db = world.Db.CreateDbContext();
        Assert.Empty(db.MediaFiles);
    }

    [Fact]
    public async Task Excluded_files_are_not_linked()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        await world.AddMovieAsync("Dune", 2021);
        var fs = OnlineFs(MovieRoot);
        fs.Put(MovieRoot, "Dune (2021)/Dune (2021).mkv");

        var result = await PreviewAndApplyAsync(Reconciler(world, fs), ["/media/movies/Dune (2021)/Dune (2021).mkv"]);

        Assert.Equal(0, result.Linked);
        Assert.Contains(result.Skipped, s => s.EndsWith("excluded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_preview_cannot_be_applied_twice()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        await world.AddMovieAsync("Dune", 2021);
        var fs = OnlineFs(MovieRoot);
        fs.Put(MovieRoot, "Dune (2021)/Dune (2021).mkv");
        var reconciler = Reconciler(world, fs);
        var preview = await reconciler.PreviewAsync(default);
        await reconciler.ApplyAsync(preview.PlanId, null, false, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => reconciler.ApplyAsync(preview.PlanId, null, false, default));
    }

    [Fact]
    public async Task Legacy_paths_are_matched_through_the_path_mapping_and_unmapped_ones_are_reported()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        var mapped = await world.AddMovieAsync("Dune", 2021);
        var unmapped = await world.AddMovieAsync("Arrival", 2016);
        await using (var db = world.Db.CreateDbContext())
        {
            (await db.MediaItems.FindAsync(mapped))!.LegacyPath = "/data/media/movies/Dune (2021)";
            (await db.MediaItems.FindAsync(unmapped))!.LegacyPath = "/elsewhere/Arrival (2016)";
            db.PathMappings.Add(new PathMapping { MediaType = MediaType.Movie, SourcePrefix = "/data/media/movies", TargetPrefix = MovieRoot });
            await db.SaveChangesAsync();
        }
        var fs = OnlineFs(MovieRoot);
        fs.Put(MovieRoot, "Dune (2021)/Renamed.mkv");

        var preview = await Reconciler(world, fs).PreviewAsync(default);

        var link = Assert.Single(preview.Plan.Links);
        Assert.Equal("legacy-path", link.Method);
        Assert.Contains(preview.Plan.Warnings, w => w.Contains("no path mapping", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Overlapping_roots_do_not_create_duplicate_links()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot), (MediaType.Movie, MovieRoot + "/sub"));
        await world.AddMovieAsync("Dune", 2021);
        var fs = OnlineFs(MovieRoot, MovieRoot + "/sub");
        fs.Put(MovieRoot, "sub/Dune (2021)/Dune (2021).mkv");
        fs.Put(MovieRoot + "/sub", "Dune (2021)/Dune (2021).mkv");

        var preview = await Reconciler(world, fs).PreviewAsync(default);

        Assert.Single(preview.Plan.Links);
    }

    [Fact]
    public async Task Wanted_search_does_nothing_while_the_library_is_unscanned()
    {
        var world = await WorldAsync((MediaType.Movie, MovieRoot));
        await world.AddMovieAsync("Dune", 2021);
        await world.Settings.SetAsync(SettingKeys.AutomaticGrab, "true");

        var summary = await world.Search.RunOnceAsync(default);

        Assert.StartsWith("Paused:", summary.Message, StringComparison.Ordinal);
        Assert.Equal(0, summary.Searched);
    }
}
