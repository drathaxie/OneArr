using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OneArr.Domain;
using OneArr.Infrastructure.Library;
using OneArr.Infrastructure.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace OneArr.Tests;

/// <summary>
/// Reconciliation on a synthetic library with real folders and files, and a real migrated SQLite file (not in memory),
/// so database cost is included as it is in production. Runs only when ONEARR_BENCH=1.
/// ONEARR_BENCH_MOVIES sets the size (default 20000). Each phase reports wall time, CPU time and bytes allocated.
/// </summary>
[Trait("Category", "Benchmark")]
public class LibraryBenchmarkTests
{
    private readonly ITestOutputHelper _out;

    public LibraryBenchmarkTests(ITestOutputHelper output) => _out = output;

    private sealed class Phase
    {
        private readonly Stopwatch _wall = Stopwatch.StartNew();
        private readonly TimeSpan _cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        private readonly long _alloc0 = GC.GetTotalAllocatedBytes(precise: false);
        public string Name { get; }
        public Phase(string name) => Name = name;
        public (double WallMs, double CpuMs, double AllocMb) Stop()
        {
            var cpu = Process.GetCurrentProcess().TotalProcessorTime - _cpu0;
            return (_wall.Elapsed.TotalMilliseconds, cpu.TotalMilliseconds, (GC.GetTotalAllocatedBytes(precise: false) - _alloc0) / 1024.0 / 1024.0);
        }
    }

    [Fact]
    public async Task Benchmark_reconciliation_on_a_synthetic_library()
    {
        if (Environment.GetEnvironmentVariable("ONEARR_BENCH") != "1") return;

        var count = int.TryParse(Environment.GetEnvironmentVariable("ONEARR_BENCH_MOVIES"), out var n) ? n : 20_000;
        var root = Path.Combine(Path.GetTempPath(), "onearr-bench-" + Guid.NewGuid().ToString("N"));
        var mediaRoot = Path.Combine(root, "media");
        var dbFile = Path.Combine(root, "config", "onearr.db");
        Directory.CreateDirectory(mediaRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(dbFile)!);
        var services = new ServiceCollection();
        services.AddDbContextFactory<OneArrDbContext>(o => o.UseSqlite($"Data Source={dbFile}"));
        var db = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<OneArrDbContext>>();
        var results = new List<string>();
        try
        {
            await using (var migrate = await db.CreateDbContextAsync())
                await migrate.Database.MigrateAsync();

            var generate = new Phase("generate");
            for (var i = 0; i < count; i++)
            {
                var year = 1950 + i % 70;
                var folder = Path.Combine(mediaRoot, $"Movie {i} ({year})");
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, $"Movie {i} ({year}) 1080p.mkv"), new byte[1024]);
            }
            results.Add(Line("generate files (fixture, not reconciler)", generate.Stop()));

            await using (var ctx = await db.CreateDbContextAsync())
            {
                var profile = new QualityProfile { Name = "HD" };
                ctx.QualityProfiles.Add(profile);
                await ctx.SaveChangesAsync();
                for (var i = 0; i < count; i++)
                {
                    ctx.MediaItems.Add(new MediaItem
                    {
                        MediaType = MediaType.Movie, Provider = "tmdb", ProviderId = (1000 + i).ToString(), Title = $"Movie {i}", Year = 1950 + i % 70,
                        QualityProfileId = profile.Id, RootFolder = mediaRoot, Monitored = true,
                    });
                }
                ctx.LibraryRoots.Add(new LibraryRoot { MediaType = MediaType.Movie, Path = mediaRoot, PathKey = LibraryPaths.Key(mediaRoot, OperatingSystem.IsWindows()) });
                await ctx.SaveChangesAsync();
            }

            var fs = new LocalLibraryFileSystem();
            var reconciler = new LibraryReconciler(db, fs, TimeProvider.System);

            var enumerate = new Phase("enumerate");
            var listing = await fs.EnumerateAsync(mediaRoot, default);
            results.Add(Line("enumerate folders and files", enumerate.Stop()));
            Assert.Equal(count, listing.Files.Count);

            var catalog = Enumerable.Range(0, count).Select(i => new CatalogMovie(i + 1, $"Movie {i}", 1950 + i % 70, null, null)).ToList();
            var index = new CatalogIndex(catalog, [], OperatingSystem.IsWindows());
            var match = new Phase("match");
            var matched = listing.Files.Count(f => LibraryMatcher.Match(f, MediaType.Movie, index).Outcome == MatchOutcome.Linked);
            results.Add(Line("match files to catalog (indexed)", match.Stop()));
            Assert.Equal(count, matched);

            var preview = new Phase("preview");
            var plan = await reconciler.PreviewAsync(default);
            results.Add(Line("preview: enumerate, match, probe, store plan", preview.Stop()));
            Assert.Equal(count, plan.Plan.Links.Count);

            var apply = new Phase("apply");
            var applied = await reconciler.ApplyAsync(plan.PlanId, null, false, default);
            results.Add(Line("apply: re-scan, database writes, recalculation", apply.Stop()));
            Assert.Equal(count, applied.Linked);
            // Read the database back. A count from the plan alone would not show whether rows were written.
            await using (var check = await db.CreateDbContextAsync())
            {
                Assert.Equal(count, await check.MediaFiles.CountAsync(f => f.Status == MediaFileStatus.Present));
                Assert.Equal(count, await check.MediaItems.CountAsync(m => m.HasFile));
            }

            var second = new Phase("second");
            var again = await reconciler.PreviewAsync(default);
            results.Add(Line("second preview (no changes)", second.Stop()));
            Assert.Equal(count, again.Plan.AlreadyLinked);

            using var cts = new CancellationTokenSource();
            var cancelled = Stopwatch.StartNew();
            var cancelling = reconciler.PreviewAsync(cts.Token);
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));
            var observed = 0.0;
            try { await cancelling; }
            catch (OperationCanceledException) { observed = cancelled.Elapsed.TotalMilliseconds; }
            results.Add($"cancel a running preview 50 ms in: stopped after {observed:N0} ms");

            var platform = OperatingSystem.IsWindows() ? "Windows" : "non-Windows";
            _out.WriteLine($"Synthetic library: {count:N0} movies, {listing.Files.Count:N0} media files, real folders, SQLite file database ({platform}).");
            foreach (var line in results) _out.WriteLine(line);
            _out.WriteLine($"Peak working set of the test process: {Process.GetCurrentProcess().PeakWorkingSet64 / 1024.0 / 1024.0:N0} MB");
            _out.WriteLine($"Logical processors: {Environment.ProcessorCount}");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string Line(string label, (double WallMs, double CpuMs, double AllocMb) m) =>
        $"{label}: wall {m.WallMs:N0} ms, CPU {m.CpuMs:N0} ms, allocated {m.AllocMb:N0} MB";
}
