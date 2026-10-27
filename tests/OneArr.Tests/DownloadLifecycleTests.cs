using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Files;
using OneArr.Infrastructure.Providers;
using OneArr.Infrastructure.Services;
using Xunit;

namespace OneArr.Tests;

/// <summary>
/// PR #6 regression coverage for the download lifecycle, driven through the real DownloadService
/// over the fake qBittorrent upstreams and temp directories. No real client, indexer, or media.
/// </summary>
public class DownloadLifecycleTests
{
    private sealed class StubMetadata : IMetadataProvider
    {
        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(MediaType type, string query, CancellationToken ct) => Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);
        public Task<MetadataDetails> GetDetailsAsync(MediaType type, string providerId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<SeasonInfo>> GetSeasonsAsync(string providerId, CancellationToken ct) => Task.FromResult<IReadOnlyList<SeasonInfo>>([]);
    }

    private sealed class ThrowingImporter : IImporter
    {
        private readonly IImporter _inner;
        private readonly string _failFileName;
        public ThrowingImporter(IImporter inner, string failFileName) { _inner = inner; _failFileName = failFileName; }
        public Task<ImportOutcome> ImportAsync(ImportRequest request, CancellationToken ct) =>
            Path.GetFileName(request.SourcePath) == _failFileName
                ? throw new IOException("simulated copy failure")
                : _inner.ImportAsync(request, ct);
    }

    private static async Task<TestWorld> WorldAsync()
    {
        var world = new TestWorld();
        await world.ConfigureAsync(automaticGrab: false);
        return world;
    }

    private static async Task<int> AddClientAsync(TestWorld world, string name, string host, FakeQbit fake)
    {
        world.Http.Route(host, fake.Respond);
        await using var db = world.Db.CreateDbContext();
        var row = new DownloadClientConfig { Name = name, BaseUrl = $"http://{host}", Username = "u", Password = "p", MovieCategory = "onearr-movies", TvCategory = "onearr-tv", Enabled = true };
        db.DownloadClients.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static async Task<int> AddJobAsync(TestWorld world, int mediaId, int? episodeId, MediaType type, string title, DownloadStatus status, int? clientId, string? hash, string? contentPath, DateTimeOffset? lastSeen = null)
    {
        await using var db = world.Db.CreateDbContext();
        var job = new DownloadJob
        {
            MediaItemId = mediaId, EpisodeId = episodeId, MediaType = type, ReleaseTitle = title, Link = "magnet:?xt=urn:btih:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Hash = hash, Category = "onearr-tv", DownloadClientId = clientId, ClientName = clientId is null ? "" : "client",
            Status = status, Progress = status == DownloadStatus.Completed ? 1 : 0.5, ContentPath = contentPath,
            CreatedAt = world.Time.GetUtcNow(), UpdatedAt = world.Time.GetUtcNow(), LastSeenAt = lastSeen,
        };
        db.DownloadJobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private static async Task<DownloadJob> GetJobAsync(TestWorld world, int id)
    {
        await using var db = world.Db.CreateDbContext();
        return (await db.DownloadJobs.AsNoTracking().FirstAsync(j => j.Id == id));
    }

    private static DownloadService PreviewService(TestWorld world) =>
        new(world.Db, new QBittorrentClient(new FakeFactory(world.Http)), new SafeImporter(TimeSpan.FromSeconds(30)), world.Settings, world.Time);

    private static void AgeFile(string path) => File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));

    [Fact]
    public async Task TwoClientsWithSameTorrentName_DoNotConfuseJobs()
    {
        using var world = await WorldAsync();
        var qbit2 = new FakeQbit();
        _ = await AddClientAsync(world, "second", "qbit2.test", qbit2);
        // Same torrent name on both clients, different hashes. The job belongs to client 1.
        world.Qbit.Torrents.Add(new FakeQbit.Torrent("aa01", "Show", "onearr-tv", "downloading", 0.4, null));
        qbit2.Torrents.Add(new FakeQbit.Torrent("bb02", "Show", "onearr-tv", "downloading", 0.9, null));
        var (_, _) = await world.AddSeriesAsync("Show", 1);
        await using (var db = world.Db.CreateDbContext())
        {
            var series = await db.MediaItems.FirstAsync(m => m.Title == "Show");
            var job = new DownloadJob { MediaItemId = series.Id, MediaType = MediaType.Series, ReleaseTitle = "Show", Link = "magnet:?xt=urn:btih:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Category = "onearr-tv", DownloadClientId = 1, ClientName = "qbit", Status = DownloadStatus.Downloading, CreatedAt = world.Time.GetUtcNow(), UpdatedAt = world.Time.GetUtcNow(), LastSeenAt = world.Time.GetUtcNow() };
            db.DownloadJobs.Add(job);
            await db.SaveChangesAsync();
        }
        await world.Downloads.SyncQueueAsync(default);
        await using (var db = world.Db.CreateDbContext())
        {
            var job = await db.DownloadJobs.AsNoTracking().FirstAsync();
            Assert.Equal("aa01", job.Hash); // backfilled from client 1 only
            Assert.Equal(0.4, job.Progress);
            Assert.Equal(DownloadStatus.Downloading, job.Status);
        }
        // Client 1 drops the torrent; client 2 still reports the same name. The job must go
        // Missing anyway: only its owning client may vouch for it.
        world.Qbit.Torrents.Clear();
        world.Time.Advance(TimeSpan.FromMinutes(16));
        await world.Downloads.SyncQueueAsync(default);
        await using (var db = world.Db.CreateDbContext())
        {
            var after = await db.DownloadJobs.AsNoTracking().FirstAsync();
            Assert.Equal(DownloadStatus.Missing, after.Status);
        }
    }

    [Fact]
    public async Task HashKeyedJob_NeverFallsBackToNameMatching()
    {
        using var world = await WorldAsync();
        var (series, _) = await world.AddSeriesAsync("Show", 1);
        // Client reports the same NAME under a different hash: not a match for a hash-keyed job.
        world.Qbit.Torrents.Add(new FakeQbit.Torrent("zz99", "Show", "onearr-tv", "downloading", 0.5, null));
        var jobId = await AddJobAsync(world, series, null, MediaType.Series, "Show", DownloadStatus.Downloading, 1, "aa01", null, world.Time.GetUtcNow().AddMinutes(-16));
        await world.Downloads.SyncQueueAsync(default);
        Assert.Equal(DownloadStatus.Missing, (await GetJobAsync(world, jobId)).Status);
    }

    [Fact]
    public async Task ClientOffline_NeverMarksJobsMissing_AndHealthRecordsTheOutage()
    {
        using var world = await WorldAsync();
        var qbit2 = new FakeQbit();
        var client2 = await AddClientAsync(world, "second", "qbit2.test", qbit2);
        // Replace the second client's route with one that throws: a dead client, not a missing torrent.
        world.Http.Route("qbit2.test", _ => throw new HttpRequestException("connection refused"));
        var (series, _) = await world.AddSeriesAsync("Show", 1);
        var jobId = await AddJobAsync(world, series, null, MediaType.Series, "Show", DownloadStatus.Downloading, client2, null, null, world.Time.GetUtcNow().AddMinutes(-30));
        await world.Downloads.SyncQueueAsync(default);
        var job = await GetJobAsync(world, jobId);
        Assert.Equal(DownloadStatus.Downloading, job.Status);
        await using var db = world.Db.CreateDbContext();
        var health = await db.DownloadClientHealth.AsNoTracking().FirstOrDefaultAsync(h => h.DownloadClientId == client2);
        Assert.NotNull(health);
        Assert.Contains("Network error", health!.LastError);
    }

    [Fact]
    public async Task ClientStates_MapAndRecover()
    {
        using var world = await WorldAsync();
        var (series, episodes) = await world.AddSeriesAsync("Show", 1);
        world.Qbit.Torrents.Add(new FakeQbit.Torrent("aa01", "Show", "onearr-tv", "pausedDL", 0.5, null, 1234, 600));
        var jobId = await AddJobAsync(world, series, episodes[0], MediaType.Series, "Show", DownloadStatus.Downloading, 1, "aa01", null, world.Time.GetUtcNow());
        await world.Downloads.SyncQueueAsync(default);
        var paused = await GetJobAsync(world, jobId);
        Assert.Equal(DownloadStatus.Downloading, paused.Status);
        Assert.Equal("pausedDL", paused.ClientState);
        Assert.Equal(1234, paused.DownloadSpeedBps);
        Assert.Equal(600, paused.EtaSeconds);
        // Client reports an error: download-side failure, still polled afterwards.
        world.Qbit.SetTorrent("aa01", "error", 0.5, null);
        await world.Downloads.SyncQueueAsync(default);
        var failed = await GetJobAsync(world, jobId);
        Assert.Equal(DownloadStatus.Failed, failed.Status);
        Assert.False(failed.ImportFailed);
        // Torrent recovers: the job recovers too instead of staying Failed forever.
        world.Qbit.SetTorrent("aa01", "uploading", 1, null);
        await world.Downloads.SyncQueueAsync(default);
        var done = await GetJobAsync(world, jobId);
        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.NotNull(done.CompletedAt);
    }

    [Fact]
    public async Task RepeatedPolling_CreatesNoDuplicateJobs()
    {
        using var world = await WorldAsync();
        var (series, _) = await world.AddSeriesAsync("Show", 1);
        world.Qbit.Torrents.Add(new FakeQbit.Torrent("aa01", "Show", "onearr-tv", "downloading", 0.5, null));
        var jobId = await AddJobAsync(world, series, null, MediaType.Series, "Show", DownloadStatus.Downloading, 1, "aa01", null, world.Time.GetUtcNow());
        await world.Downloads.SyncQueueAsync(default);
        await world.Downloads.SyncQueueAsync(default);
        await using var db = world.Db.CreateDbContext();
        Assert.Equal(1, await db.DownloadJobs.CountAsync());
        Assert.Equal(DownloadStatus.Downloading, (await db.DownloadJobs.AsNoTracking().FirstAsync(j => j.Id == jobId)).Status);
    }

    [Fact]
    public async Task ImportMovie_MarksAvailable_AndPreservesSource()
    {
        using var world = await WorldAsync();
        var movieId = await world.AddMovieAsync("Dune", 2021);
        var file = world.Dirs.WriteFile("torrents/Dune/Dune (2021) 1080p.mkv", "movie-bytes");
        AgeFile(file);
        var jobId = await AddJobAsync(world, movieId, null, MediaType.Movie, "Dune", DownloadStatus.Completed, 1, "aa01", Path.GetDirectoryName(file));
        var summary = await world.Downloads.ImportCompletedAsync(dryRun: false, default);
        Assert.Equal(1, summary.Imported);
        Assert.Equal(DownloadStatus.Imported, (await GetJobAsync(world, jobId)).Status);
        await using var db = world.Db.CreateDbContext();
        Assert.True((await db.MediaItems.AsNoTracking().FirstAsync(m => m.Id == movieId)).HasFile);
        Assert.True(File.Exists(file), "Source must never be deleted by import");
        Assert.True(File.Exists(Path.Combine(world.Dirs.Media, "movies", "Dune (2021)", "Dune (2021) 1080p.mkv")));
    }

    [Fact]
    public async Task ImportSingleEpisode_MarksOnlyThatEpisode()
    {
        using var world = await WorldAsync();
        var (series, episodes) = await world.AddSeriesAsync("Show", 2);
        var file = world.Dirs.WriteFile("torrents/Show/Show - S01E01.mkv", "ep-one");
        AgeFile(file);
        var jobId = await AddJobAsync(world, series, episodes[0], MediaType.Series, "Show", DownloadStatus.Completed, 1, "aa01", Path.GetDirectoryName(file));
        await world.Downloads.ImportCompletedAsync(dryRun: false, default);
        Assert.Equal(DownloadStatus.Imported, (await GetJobAsync(world, jobId)).Status);
        await using var db = world.Db.CreateDbContext();
        var eps = await db.Episodes.AsNoTracking().Where(e => e.MediaItemId == series).OrderBy(e => e.EpisodeNumber).ToListAsync();
        Assert.True(eps[0].HasFile);
        Assert.False(eps[1].HasFile);
    }

    [Fact]
    public async Task ImportSeasonPack_MarksEveryCoveredEpisode_AndNothingElse()
    {
        using var world = await WorldAsync();
        var (series, _) = await world.AddSeriesAsync("Show", 4);
        foreach (var name in new[] { "Show - S01E01.mkv", "Show - S01E02.mkv", "Show - S01E03.mkv", "extras.mkv" })
            AgeFile(world.Dirs.WriteFile($"torrents/Show/{name}", "bytes-" + name));
        var jobId = await AddJobAsync(world, series, null, MediaType.Series, "Show", DownloadStatus.Completed, 1, "aa01", Path.Combine(world.Dirs.Torrents, "Show"));
        await world.Downloads.ImportCompletedAsync(dryRun: false, default);
        // Per the PR #6 review, a pack with an unmatched file stays visibly unresolved: the job
        // does not read fully Imported while extras.mkv matches no episode, even though the
        // covered episodes are all marked and E04 stays unmarked.
        Assert.Equal(DownloadStatus.Completed, (await GetJobAsync(world, jobId)).Status);
        await using var db = world.Db.CreateDbContext();
        var eps = await db.Episodes.AsNoTracking().Where(e => e.MediaItemId == series).OrderBy(e => e.EpisodeNumber).ToListAsync();
        Assert.True(eps[0].HasFile);
        Assert.True(eps[1].HasFile);
        Assert.True(eps[2].HasFile);
        Assert.False(eps[3].HasFile); // extras.mkv covers no episode and must not mark E04
        Assert.Contains(await db.ImportRecords.AsNoTracking().ToListAsync(), r => r.DownloadJobId == jobId && r.Action == ImportAction.Unmatched && r.SourcePath.EndsWith("extras.mkv"));
    }

    [Fact]
    public async Task ImportMultiEpisodeFile_MarksBothEpisodes()
    {
        using var world = await WorldAsync();
        var (series, _) = await world.AddSeriesAsync("Show", 3);
        var file = world.Dirs.WriteFile("torrents/Show/Show - S01E01E02.mkv", "both");
        AgeFile(file);
        await AddJobAsync(world, series, null, MediaType.Series, "Show", DownloadStatus.Completed, 1, "aa01", Path.GetDirectoryName(file));
        await world.Downloads.ImportCompletedAsync(dryRun: false, default);
        await using var db = world.Db.CreateDbContext();
        var eps = await db.Episodes.AsNoTracking().Where(e => e.MediaItemId == series).OrderBy(e => e.EpisodeNumber).ToListAsync();
        Assert.True(eps[0].HasFile);
        Assert.True(eps[1].HasFile);
        Assert.False(eps[2].HasFile);
    }

    [Fact]
    public async Task PartialFailure_StaysVisible_AndResumesWithoutDuplicates()
    {
        using var world = await WorldAsync();
        var (series, _) = await world.AddSeriesAsync("Show", 2);
        var f1 = world.Dirs.WriteFile("torrents/Show/Show - S01E01.mkv", "one");
        var f2 = world.Dirs.WriteFile("torrents/Show/Show - S01E02.mkv", "two");
        AgeFile(f1); AgeFile(f2);
        var jobId = await AddJobAsync(world, series, null, MediaType.Series, "Show", DownloadStatus.Completed, 1, "aa01", Path.Combine(world.Dirs.Torrents, "Show"));
        var failing = new DownloadService(world.Db, new QBittorrentClient(new FakeFactory(world.Http)), new ThrowingImporter(new SafeImporter(TimeSpan.Zero), "Show - S01E02.mkv"), world.Settings, world.Time);
        var summary = await failing.ImportCompletedAsync(dryRun: false, default);
        Assert.Equal(1, summary.Failed);
        var failed = await GetJobAsync(world, jobId);
        Assert.Equal(DownloadStatus.Failed, failed.Status);
        Assert.True(failed.ImportFailed);
        await using (var db = world.Db.CreateDbContext())
        {
            Assert.Contains(await db.ImportRecords.AsNoTracking().ToListAsync(), r => r.DownloadJobId == jobId && !r.Success);
        }
        // A later bulk pass must NOT silently retry a failed import.
        await world.Downloads.ImportCompletedAsync(dryRun: false, default);
        Assert.Equal(DownloadStatus.Failed, (await GetJobAsync(world, jobId)).Status);
        // Retry requires a preview: without one it is refused, so a failed import can never be
        // retried around the preview-and-confirmation model.
        await Assert.ThrowsAsync<ArgumentException>(() => world.Downloads.RetryImportAsync(jobId, default));
        await world.Downloads.PreviewImportAsync(jobId, default);
        // "Restart": a fresh service with a working importer applies the preview, skips the
        // already-imported file, imports the second, and the job completes with no duplicates.
        var retry = await world.Downloads.RetryImportAsync(jobId, default);
        Assert.Equal(0, retry.Failed);
        Assert.Equal(DownloadStatus.Imported, (await GetJobAsync(world, jobId)).Status);
        await using var db2 = world.Db.CreateDbContext();
        var eps = await db2.Episodes.AsNoTracking().Where(e => e.MediaItemId == series).OrderBy(e => e.EpisodeNumber).ToListAsync();
        Assert.True(eps[0].HasFile);
        Assert.True(eps[1].HasFile);
    }

    [Fact]
    public async Task Reimport_SkipsIdenticalContent_WithoutOverwriting()
    {
        using var world = await WorldAsync();
        var movieId = await world.AddMovieAsync("Dune", 2021);
        var file = world.Dirs.WriteFile("torrents/Dune/Dune (2021) 1080p.mkv", "movie-bytes");
        AgeFile(file);
        var jobId = await AddJobAsync(world, movieId, null, MediaType.Movie, "Dune", DownloadStatus.Completed, 1, "aa01", Path.GetDirectoryName(file));
        await world.Downloads.ImportCompletedAsync(dryRun: false, default);
        var dest = Path.Combine(world.Dirs.Media, "movies", "Dune (2021)", "Dune (2021) 1080p.mkv");
        var before = await File.ReadAllTextAsync(dest);
        await using (var db = world.Db.CreateDbContext())
        {
            var job = await db.DownloadJobs.FirstAsync(j => j.Id == jobId);
            job.Status = DownloadStatus.Completed;
            await db.SaveChangesAsync();
        }
        var again = await world.Downloads.ImportCompletedAsync(dryRun: false, default);
        Assert.Equal(1, again.Imported); // Skipped counts as imported: nothing new, nothing lost
        Assert.Equal(before, await File.ReadAllTextAsync(dest));
        Assert.Equal(DownloadStatus.Imported, (await GetJobAsync(world, jobId)).Status);
    }

    [Fact]
    public async Task SiblingPrefixDirectory_IsNotAcceptedAsTorrentRoot()
    {
        using var world = await WorldAsync();
        var movieId = await world.AddMovieAsync("Dune", 2021);
        // torrents2 is a sibling of torrents, not a child: a raw StartsWith check would accept it.
        var file = world.Dirs.WriteFile("torrents2/Dune/Dune (2021) 1080p.mkv", "movie-bytes");
        AgeFile(file);
        var jobId = await AddJobAsync(world, movieId, null, MediaType.Movie, "Dune", DownloadStatus.Completed, 1, "aa01", Path.GetDirectoryName(file));
        await world.Downloads.ImportCompletedAsync(dryRun: false, default);
        var job = await GetJobAsync(world, jobId);
        Assert.Equal(DownloadStatus.Failed, job.Status);
        Assert.True(job.ImportFailed);
        Assert.False(File.Exists(Path.Combine(world.Dirs.Media, "movies", "Dune (2021)", "Dune (2021) 1080p.mkv")));
    }

    [Fact]
    public async Task MissingContentPath_BecomesVisibleFailure_NotSilentSkip()
    {
        using var world = await WorldAsync();
        var movieId = await world.AddMovieAsync("Dune", 2021);
        var jobId = await AddJobAsync(world, movieId, null, MediaType.Movie, "Dune", DownloadStatus.Completed, 1, "aa01", Path.Combine(world.Dirs.Root, "gone"));
        await world.Downloads.ImportCompletedAsync(dryRun: false, default);
        var job = await GetJobAsync(world, jobId);
        Assert.Equal(DownloadStatus.Failed, job.Status);
        Assert.True(job.ImportFailed);
        Assert.Contains("No media files", job.Error);
    }

    [Fact]
    public async Task Preview_PlansWithoutWriting_AndApplyImportsExactlyThePlan()
    {
        using var world = await WorldAsync();
        var (series, episodes) = await world.AddSeriesAsync("Show", 2);
        var file = world.Dirs.WriteFile("torrents/Show/Show - S01E01.mkv", "ep-one");
        AgeFile(file);
        var jobId = await AddJobAsync(world, series, episodes[0], MediaType.Series, "Show", DownloadStatus.Completed, 1, "aa01", Path.GetDirectoryName(file));
        var svc = PreviewService(world);
        var preview = await svc.PreviewImportAsync(jobId, default);
        Assert.Single(preview.Plans);
        var plan = preview.Plans[0];
        Assert.Equal("hardlink", plan.PlannedAction);
        Assert.Contains("S01E01", plan.MatchedEpisodeLabels);
        Assert.True(plan.SameVolume);
        var dest = plan.DestinationPath!;
        Assert.False(File.Exists(dest), "A preview must not create the destination");
        await svc.ApplyPreviewAsync(jobId, preview.PreviewId, default);
        Assert.True(File.Exists(dest));
        Assert.True(File.Exists(file), "Apply must preserve the source");
        Assert.Equal(DownloadStatus.Imported, (await GetJobAsync(world, jobId)).Status);
        await using var db = world.Db.CreateDbContext();
        Assert.True((await db.Episodes.AsNoTracking().FirstAsync(e => e.Id == episodes[0])).HasFile);
    }

    [Fact]
    public async Task Preview_DefersFreshFiles_AndStalePreviewIsRefused()
    {
        using var world = await WorldAsync();
        var movieId = await world.AddMovieAsync("Dune", 2021);
        var file = world.Dirs.WriteFile("torrents/Dune/Dune (2021) 1080p.mkv", "fresh-bytes");
        // Fresh mtime + 30 s quiet period: the preview must defer, not plan an import.
        var jobId = await AddJobAsync(world, movieId, null, MediaType.Movie, "Dune", DownloadStatus.Completed, 1, "aa01", Path.GetDirectoryName(file));
        var svc = PreviewService(world);
        var preview = await svc.PreviewImportAsync(jobId, default);
        Assert.Equal("defer", preview.Plans[0].PlannedAction);
        // Now change the file (different size) and age it: the old preview is stale.
        await File.WriteAllTextAsync(file, "fresh-bytes-and-more");
        AgeFile(file);
        var ex = await Assert.ThrowsAsync<StalePreviewException>(() => svc.ApplyPreviewAsync(jobId, preview.PreviewId, default));
        Assert.Single(ex.Fresh.Plans);
        Assert.Equal("hardlink", ex.Fresh.Plans[0].PlannedAction);
        Assert.Equal(DownloadStatus.Completed, (await GetJobAsync(world, jobId)).Status);
        Assert.False(File.Exists(Path.Combine(world.Dirs.Media, "movies", "Dune (2021)", "Dune (2021) 1080p.mkv")));
    }

    [Fact]
    public async Task WantedAndLibraryAgree_AfterVerifiedImport()
    {
        using var world = await WorldAsync();
        var (series, episodes) = await world.AddSeriesAsync("Show", 2);
        var file = world.Dirs.WriteFile("torrents/Show/Show - S01E01.mkv", "ep-one");
        AgeFile(file);
        await AddJobAsync(world, series, episodes[0], MediaType.Series, "Show", DownloadStatus.Completed, 1, "aa01", Path.GetDirectoryName(file));
        await world.Downloads.ImportCompletedAsync(dryRun: false, default);
        var media = new MediaService(world.Db, new StubMetadata(), world.Time);
        var wanted = await media.WantedAsync(default);
        Assert.DoesNotContain(wanted.Episodes, e => e.EpisodeId == episodes[0]);
        Assert.Contains(wanted.Episodes, e => e.EpisodeId == episodes[1]);
        var availability = await media.SeriesAvailabilityAsync(default);
        Assert.Equal("Partial", availability[series].Status);
        Assert.Equal(1, availability[series].AvailableEpisodes);
        Assert.Equal(2, availability[series].ApplicableEpisodes);
    }

    [Fact]
    public async Task UnmonitoredEpisodes_StayUnmonitored_AfterImport()
    {
        using var world = await WorldAsync();
        var (series, episodes) = await world.AddSeriesAsync("Show", 2);
        await using (var db = world.Db.CreateDbContext())
        {
            var ep = await db.Episodes.FirstAsync(e => e.Id == episodes[1]);
            ep.Monitored = false;
            await db.SaveChangesAsync();
        }
        foreach (var name in new[] { "Show - S01E01.mkv", "Show - S01E02.mkv" })
            AgeFile(world.Dirs.WriteFile($"torrents/Show/{name}", "bytes"));
        await AddJobAsync(world, series, null, MediaType.Series, "Show", DownloadStatus.Completed, 1, "aa01", Path.Combine(world.Dirs.Torrents, "Show"));
        await world.Downloads.ImportCompletedAsync(dryRun: false, default);
        await using var db2 = world.Db.CreateDbContext();
        var eps = await db2.Episodes.AsNoTracking().Where(e => e.MediaItemId == series).OrderBy(e => e.EpisodeNumber).ToListAsync();
        Assert.True(eps[1].HasFile, "The file was imported, so the episode has a file");
        Assert.False(eps[1].Monitored, "But importing must never flip a monitoring exception");
        var media = new MediaService(world.Db, new StubMetadata(), world.Time);
        var wanted = await media.WantedAsync(default);
        Assert.DoesNotContain(wanted.Episodes, e => e.EpisodeId == episodes[1]);
    }

    [Fact]
    public async Task NullContentPath_BecomesVisibleFailure_AndDryRunChangesNothing()
    {
        using var world = await WorldAsync();
        var movieId = await world.AddMovieAsync("Dune", 2021);
        var jobId = await AddJobAsync(world, movieId, null, MediaType.Movie, "Dune", DownloadStatus.Completed, 1, "aa01", null);
        var summary = await world.Downloads.ImportCompletedAsync(dryRun: false, default);
        Assert.Equal(1, summary.Failed);
        var job = await GetJobAsync(world, jobId);
        Assert.Equal(DownloadStatus.Failed, job.Status);
        Assert.True(job.ImportFailed);
        Assert.Contains("content path", job.Error);

        // Same shape as a dry run: reported, but the job and the records are untouched.
        using var dry = await WorldAsync();
        var dryMovie = await dry.AddMovieAsync("Dune", 2021);
        var dryJob = await AddJobAsync(dry, dryMovie, null, MediaType.Movie, "Dune", DownloadStatus.Completed, 1, "aa01", null);
        var drySummary = await dry.Downloads.ImportCompletedAsync(dryRun: true, default);
        Assert.Equal(1, drySummary.Failed);
        Assert.Equal(DownloadStatus.Completed, (await GetJobAsync(dry, dryJob)).Status);
        await using var db = dry.Db.CreateDbContext();
        Assert.Empty(await db.ImportRecords.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task DryRun_PlansWithoutWritingRecordsOrChangingStatus()
    {
        using var world = await WorldAsync();
        var movieId = await world.AddMovieAsync("Dune", 2021);
        var file = world.Dirs.WriteFile("torrents/Dune/Dune (2021) 1080p.mkv", "movie-bytes");
        AgeFile(file);
        var jobId = await AddJobAsync(world, movieId, null, MediaType.Movie, "Dune", DownloadStatus.Completed, 1, "aa01", Path.GetDirectoryName(file));
        var summary = await world.Downloads.ImportCompletedAsync(dryRun: true, default);
        Assert.Equal(1, summary.DryRun);
        Assert.Equal(DownloadStatus.Completed, (await GetJobAsync(world, jobId)).Status);
        Assert.Empty(Directory.GetFiles(world.Dirs.Media, "*", SearchOption.AllDirectories));
        await using var db = world.Db.CreateDbContext();
        Assert.Empty(await db.ImportRecords.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task DisabledOwner_NeverFallsBackToOtherClients()
    {
        using var world = await WorldAsync();
        var qbit2 = new FakeQbit();
        _ = await AddClientAsync(world, "second", "qbit2.test", qbit2);
        qbit2.Torrents.Add(new FakeQbit.Torrent("bb02", "Show", "onearr-tv", "downloading", 0.9, null));
        var (series, _) = await world.AddSeriesAsync("Show", 1);
        var jobId = await AddJobAsync(world, series, null, MediaType.Series, "Show", DownloadStatus.Downloading, 1, "aa01", null, world.Time.GetUtcNow().AddMinutes(-16));
        await using (var db = world.Db.CreateDbContext())
        {
            var owner = await db.DownloadClients.FirstAsync(c => c.Id == 1);
            owner.Enabled = false;
            await db.SaveChangesAsync();
        }
        world.Time.Advance(TimeSpan.FromMinutes(16));
        await world.Downloads.SyncQueueAsync(default);
        var job = await GetJobAsync(world, jobId);
        Assert.Equal(DownloadStatus.Downloading, job.Status);
        Assert.Equal("aa01", job.Hash);
        Assert.Equal(0.5, job.Progress);
    }

    [Fact]
    public async Task Preview_RequiresCompletedJob_AndRejectsEmptyDownloads()
    {
        using var world = await WorldAsync();
        var (series, _) = await world.AddSeriesAsync("Show", 1);
        var queued = await AddJobAsync(world, series, null, MediaType.Series, "Show", DownloadStatus.Downloading, 1, "aa01", null);
        await Assert.ThrowsAsync<ArgumentException>(() => world.Downloads.PreviewImportAsync(queued, default));
        var emptyDir = Path.Combine(world.Dirs.Torrents, "Empty");
        Directory.CreateDirectory(emptyDir);
        var empty = await AddJobAsync(world, series, null, MediaType.Series, "Show", DownloadStatus.Completed, 1, "bb02", emptyDir);
        await Assert.ThrowsAsync<ArgumentException>(() => world.Downloads.PreviewImportAsync(empty, default));
    }

    [Fact]
    public async Task Apply_EmptyPreview_IsRefused()
    {
        using var world = await WorldAsync();
        var movieId = await world.AddMovieAsync("Dune", 2021);
        var jobId = await AddJobAsync(world, movieId, null, MediaType.Movie, "Dune", DownloadStatus.Completed, 1, "aa01", Path.Combine(world.Dirs.Torrents, "Dune"));
        int previewId;
        await using (var db = world.Db.CreateDbContext())
        {
            var row = new ImportPreview { DownloadJobId = jobId, Fingerprint = "empty", PlansJson = "[]", CreatedAt = world.Time.GetUtcNow() };
            db.ImportPreviews.Add(row);
            await db.SaveChangesAsync();
            previewId = row.Id;
        }
        await Assert.ThrowsAsync<ArgumentException>(() => world.Downloads.ApplyPreviewAsync(jobId, previewId, default));
    }

    [Fact]
    public async Task Retry_RequiresPreview_ThenAppliesLatestPreview()
    {
        using var world = await WorldAsync();
        var (series, _) = await world.AddSeriesAsync("Show", 2);
        var f1 = world.Dirs.WriteFile("torrents/Show/Show - S01E01.mkv", "one");
        var f2 = world.Dirs.WriteFile("torrents/Show/Show - S01E02.mkv", "two");
        AgeFile(f1); AgeFile(f2);
        var jobId = await AddJobAsync(world, series, null, MediaType.Series, "Show", DownloadStatus.Completed, 1, "aa01", Path.Combine(world.Dirs.Torrents, "Show"));
        var failing = new DownloadService(world.Db, new QBittorrentClient(new FakeFactory(world.Http)), new ThrowingImporter(new SafeImporter(TimeSpan.Zero), "Show - S01E02.mkv"), world.Settings, world.Time);
        await failing.ImportCompletedAsync(dryRun: false, default);
        var failed = await GetJobAsync(world, jobId);
        Assert.Equal(DownloadStatus.Failed, failed.Status);
        Assert.True(failed.ImportFailed);
        await Assert.ThrowsAsync<ArgumentException>(() => world.Downloads.RetryImportAsync(jobId, default));
        await world.Downloads.PreviewImportAsync(jobId, default);
        var retry = await world.Downloads.RetryImportAsync(jobId, default);
        Assert.Equal(0, retry.Failed);
        Assert.Equal(DownloadStatus.Imported, (await GetJobAsync(world, jobId)).Status);
        await using var db = world.Db.CreateDbContext();
        var eps = await db.Episodes.AsNoTracking().Where(e => e.MediaItemId == series).OrderBy(e => e.EpisodeNumber).ToListAsync();
        Assert.True(eps[0].HasFile);
        Assert.True(eps[1].HasFile);
    }

    [Fact]
    public async Task Import_CreatesMediaFileLinks_AndUnmatchedPackFilesStayUnresolved()
    {
        using var world = await WorldAsync();
        var (series, episodes) = await world.AddSeriesAsync("Show", 2);
        var f1 = world.Dirs.WriteFile("torrents/Show/Show - S01E01.mkv", "ep-one");
        var f2 = world.Dirs.WriteFile("torrents/Show/Show - Random.mkv", "random-bytes");
        AgeFile(f1); AgeFile(f2);
        var jobId = await AddJobAsync(world, series, null, MediaType.Series, "Show", DownloadStatus.Completed, 1, "aa01", Path.Combine(world.Dirs.Torrents, "Show"));
        await world.Downloads.ImportCompletedAsync(dryRun: false, default);
        var job = await GetJobAsync(world, jobId);
        Assert.Equal(DownloadStatus.Completed, job.Status);
        Assert.Contains("unmatched", job.Error);
        await using var db = world.Db.CreateDbContext();
        var eps = await db.Episodes.AsNoTracking().Where(e => e.MediaItemId == series).OrderBy(e => e.EpisodeNumber).ToListAsync();
        Assert.True(eps[0].HasFile);
        Assert.False(eps[1].HasFile);
        var files = await db.MediaFiles.AsNoTracking().Where(f => f.MediaItemId == series).ToListAsync();
        Assert.Equal(2, files.Count);
        var links = await db.MediaFileEpisodes.AsNoTracking().ToListAsync();
        var ep1File = files.First(f => f.Path.EndsWith("Show - S01E01.mkv"));
        Assert.Contains(links, l => l.MediaFileId == ep1File.Id && l.EpisodeId == episodes[0]);
        var randomFile = files.First(f => f.Path.EndsWith("Show - Random.mkv"));
        Assert.DoesNotContain(links, l => l.MediaFileId == randomFile.Id);
        Assert.Contains(await db.ImportRecords.AsNoTracking().ToListAsync(), r => r.DownloadJobId == jobId && r.Action == ImportAction.Unmatched && r.SourcePath.EndsWith("Show - Random.mkv"));
    }

    [Fact]
    public async Task ImportMovie_CreatesMediaFileLink()
    {
        using var world = await WorldAsync();
        var movieId = await world.AddMovieAsync("Dune", 2021);
        var file = world.Dirs.WriteFile("torrents/Dune/Dune (2021) 1080p.mkv", "movie-bytes");
        AgeFile(file);
        var jobId = await AddJobAsync(world, movieId, null, MediaType.Movie, "Dune", DownloadStatus.Completed, 1, "aa01", Path.GetDirectoryName(file));
        await world.Downloads.ImportCompletedAsync(dryRun: false, default);
        Assert.Equal(DownloadStatus.Imported, (await GetJobAsync(world, jobId)).Status);
        await using var db = world.Db.CreateDbContext();
        var row = await db.MediaFiles.AsNoTracking().SingleAsync(f => f.MediaItemId == movieId);
        Assert.EndsWith("Dune (2021) 1080p.mkv", row.Path);
        Assert.True(File.Exists(row.Path));
        Assert.Empty(await db.MediaFileEpisodes.AsNoTracking().Where(l => l.MediaFileId == row.Id).ToListAsync());
    }

    [Fact]
    public async Task StalePreview_RefusedWhenEpisodeMappingChanges()
    {
        using var world = await WorldAsync();
        var (series, episodes) = await world.AddSeriesAsync("Show", 2);
        var file = world.Dirs.WriteFile("torrents/Show/Show - S01E01.mkv", "ep-one");
        AgeFile(file);
        var jobId = await AddJobAsync(world, series, episodes[0], MediaType.Series, "Show", DownloadStatus.Completed, 1, "aa01", Path.GetDirectoryName(file));
        var svc = PreviewService(world);
        var preview = await svc.PreviewImportAsync(jobId, default);
        Assert.Contains(episodes[0], preview.Plans[0].MatchedEpisodeIds);
        await using (var db = world.Db.CreateDbContext())
        {
            var job = await db.DownloadJobs.FirstAsync(j => j.Id == jobId);
            job.EpisodeId = episodes[1];
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<StalePreviewException>(() => svc.ApplyPreviewAsync(jobId, preview.PreviewId, default));
        Assert.Equal(DownloadStatus.Completed, (await GetJobAsync(world, jobId)).Status);
        Assert.False(File.Exists(preview.Plans[0].DestinationPath!));
    }

    [Fact]
    public async Task SafetyDefaults_GrabbingOff_AndDryRunOn()
    {
        using var world = new TestWorld(); // no ConfigureAsync: defaults, as a fresh install sees them
        Assert.False(await world.Settings.GetBoolAsync(SettingKeys.AutomaticGrab, false));
        Assert.True(await world.Settings.GetBoolAsync(SettingKeys.DryRunImport, true));
    }
}
