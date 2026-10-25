using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using OneArr.Domain;
using OneArr.Infrastructure.Library;
using Xunit;
using Xunit.Abstractions;

namespace OneArr.Tests;

/// <summary>
/// Reconciliation on a synthetic library with real folders and files on disk. It runs only when ONEARR_BENCH=1, so the normal suite is unaffected.
/// Size comes from ONEARR_BENCH_MOVIES (default 20000). Results are printed; nothing here is a claimed figure until it has been run.
/// </summary>
[Trait("Category", "Benchmark")]
public class LibraryBenchmarkTests
{
    private readonly ITestOutputHelper _out;

    public LibraryBenchmarkTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Benchmark_reconciliation_on_a_synthetic_library()
    {
        if (Environment.GetEnvironmentVariable("ONEARR_BENCH") != "1") return;

        var count = int.TryParse(Environment.GetEnvironmentVariable("ONEARR_BENCH_MOVIES"), out var n) ? n : 20_000;
        var root = Path.Combine(Path.GetTempPath(), "onearr-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var (db, keepAlive) = TestDb.Create();
        try
        {
            var timings = new Dictionary<string, double>();

            var generate = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                var year = 1950 + i % 70;
                var folder = Path.Combine(root, $"Movie {i} ({year})");
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, $"Movie {i} ({year}) 1080p.mkv"), new byte[1024]);
            }
            timings["generate files (not measured in the reconciler)"] = generate.Elapsed.TotalMilliseconds;

            await using (var ctx = db.CreateDbContext())
            {
                var profile = new QualityProfile { Name = "HD" };
                ctx.QualityProfiles.Add(profile);
                await ctx.SaveChangesAsync();
                for (var i = 0; i < count; i++)
                {
                    var year = 1950 + i % 70;
                    ctx.MediaItems.Add(new MediaItem
                    {
                        MediaType = MediaType.Movie, Provider = "tmdb", ProviderId = (1000 + i).ToString(), Title = $"Movie {i}", Year = year,
                        QualityProfileId = profile.Id, RootFolder = root, Monitored = true,
                    });
                }
                ctx.LibraryRoots.Add(new LibraryRoot { MediaType = MediaType.Movie, Path = root, PathKey = LibraryPaths.Key(root, OperatingSystem.IsWindows()) });
                await ctx.SaveChangesAsync();
            }

            var fs = new LocalLibraryFileSystem();
            var reconciler = new LibraryReconciler(db, fs, TimeProvider.System);

            var enumerate = Stopwatch.StartNew();
            var listing = await fs.EnumerateAsync(root, default);
            timings["enumerate folders and files"] = enumerate.Elapsed.TotalMilliseconds;
            Assert.Equal(count, listing.Files.Count);

            var matchCatalog = Enumerable.Range(0, count)
                .Select(i => new CatalogMovie(i + 1, $"Movie {i}", 1950 + i % 70, null, null)).ToList();
            var index = new CatalogIndex(matchCatalog, [], OperatingSystem.IsWindows());
            var match = Stopwatch.StartNew();
            var matched = 0;
            foreach (var file in listing.Files)
                if (LibraryMatcher.Match(file, MediaType.Movie, index).Outcome == MatchOutcome.Linked) matched++;
            timings["match files to catalog (indexed)"] = match.Elapsed.TotalMilliseconds;
            Assert.Equal(count, matched);

            var preview = Stopwatch.StartNew();
            var plan = await reconciler.PreviewAsync(default);
            timings["preview: enumerate, match, store plan"] = preview.Elapsed.TotalMilliseconds;
            Assert.Equal(count, plan.Plan.Links.Count);

            var apply = Stopwatch.StartNew();
            var result = await reconciler.ApplyAsync(plan.PlanId, null, false, default);
            timings["apply: database writes and re-check"] = apply.Elapsed.TotalMilliseconds;
            Assert.Equal(count, result.Linked);

            var rescan = Stopwatch.StartNew();
            var second = await reconciler.PreviewAsync(default);
            timings["second preview (no changes)"] = rescan.Elapsed.TotalMilliseconds;
            Assert.Equal(count, second.Plan.AlreadyLinked);

            // Cancellation responsiveness: cancel a preview while it runs, and measure how long the cancel takes to take effect.
            using var cts = new CancellationTokenSource();
            var cancelled = Stopwatch.StartNew();
            var cancelling = reconciler.PreviewAsync(cts.Token);
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));
            var observed = 0.0;
            try { await cancelling; }
            catch (OperationCanceledException) { observed = cancelled.Elapsed.TotalMilliseconds; }
            timings["cancel a running preview: time until it stops"] = observed;

            var peakMb = Process.GetCurrentProcess().PeakWorkingSet64 / 1024.0 / 1024.0;
            _out.WriteLine($"Synthetic library: {count:N0} movies, {listing.Files.Count:N0} media files, real disk, database: in-memory SQLite (TestDb).");
            foreach (var (label, ms) in timings) _out.WriteLine($"{label}: {ms:N0} ms");
            _out.WriteLine($"Peak working set of the test process: {peakMb:N0} MB");
        }
        finally
        {
            keepAlive.Dispose();
            SqliteCleanup(root);
        }
    }

    private static void SqliteCleanup(string root)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
