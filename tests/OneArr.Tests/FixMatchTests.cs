using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Infrastructure.Library;
using OneArr.Domain;
using OneArr.Infrastructure.Persistence;
using OneArr.Infrastructure.Services;
using Xunit;

namespace OneArr.Tests;

/// <summary>Fix match: pointing an existing catalog entry at the correct TMDb title without losing history or touching files.</summary>
public class FixMatchTests
{
    /// <summary>Returns a fixed catalog per provider id, so each test controls exactly what the new match lists.</summary>
    private sealed class CatalogStub : IMetadataProvider
    {
        public Dictionary<string, (string Title, int Year, List<SeasonInfo> Seasons)> Catalog { get; } = new();

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(MediaType type, string query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public Task<MetadataDetails> GetDetailsAsync(MediaType type, string providerId, CancellationToken ct)
        {
            var (title, year, seasons) = Catalog[providerId];
            return Task.FromResult(new MetadataDetails(providerId, title, year, "overview", null, null, null, seasons.Count));
        }

        public Task<IReadOnlyList<SeasonInfo>> GetSeasonsAsync(string providerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeasonInfo>>(Catalog[providerId].Seasons);
    }

    private static readonly string Root = OperatingSystem.IsWindows() ? @"D:\Media\TV" : "/media/tv";

    private static async Task<(IDbContextFactory<OneArrDbContext> Db, IDisposable Keep, CatalogStub Meta, MediaService Media)> SetupAsync()
    {
        var (factory, keep) = TestDb.Create();
        var meta = new CatalogStub();
        var media = new MediaService(factory, meta, TimeProvider.System);
        await using var db = factory.CreateDbContext();
        db.QualityProfiles.Add(new QualityProfile { Name = "HD" });
        await db.SaveChangesAsync();
        return (factory, keep, meta, media);
    }

    /// <summary>A series that was matched to the wrong show: three episodes, one of them with a download and a library link.</summary>
    private static async Task<(int SeriesId, int Ep1, int Ep2, int Ep3, int FileId)> SeedWrongMatchAsync(IDbContextFactory<OneArrDbContext> factory)
    {
        await using var db = factory.CreateDbContext();
        var profile = await db.QualityProfiles.FirstAsync();
        var series = new MediaItem
        {
            MediaType = MediaType.Series, Provider = "tmdb", ProviderId = "111", Title = "Wrong Show", Year = 2001, Monitored = true,
            QualityProfileId = profile.Id, RootFolder = Root, CreatedAt = DateTimeOffset.UtcNow, LibraryReviewedUtc = DateTimeOffset.UtcNow, HasFile = true,
        };
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        var episodes = new List<Episode>();
        foreach (var n in new[] { 1, 2, 3 })
        {
            var ep = new Episode { MediaItemId = series.Id, SeasonNumber = 1, EpisodeNumber = n, ProviderId = $"w{n}", Title = $"Old {n}", Monitored = true, HasFile = true };
            db.Episodes.Add(ep);
            episodes.Add(ep);
        }
        db.Seasons.Add(new Season { MediaItemId = series.Id, SeasonNumber = 1, Monitored = true });
        await db.SaveChangesAsync();
        db.DownloadJobs.Add(new DownloadJob { MediaItemId = series.Id, EpisodeId = episodes[0].Id, MediaType = MediaType.Series, Status = DownloadStatus.Imported, ReleaseTitle = "Wrong.Show.S01E01" });
        var file = new MediaFile { Path = Root + "/Wrong.Show.S01E01.mkv", PathKey = Root + "/Wrong.Show.S01E01.mkv", MediaItemId = series.Id, Status = MediaFileStatus.Present, SizeBytes = 10 };
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync();
        db.MediaFileEpisodes.Add(new MediaFileEpisode { MediaFileId = file.Id, EpisodeId = episodes[0].Id });
        await db.SaveChangesAsync();
        return (series.Id, episodes[0].Id, episodes[1].Id, episodes[2].Id, file.Id);
    }

    [Fact]
    public async Task Fixing_a_series_keeps_episode_ids_and_download_history_and_unmonitors_dropped_episodes()
    {
        var (factory, keep, meta, media) = await SetupAsync();
        using var _ = keep;
        var (seriesId, ep1, ep2, ep3, _) = await SeedWrongMatchAsync(factory);
        meta.Catalog["222"] = ("Right Show", 2010,
        [
            new SeasonInfo(1, [new EpisodeInfo(1, "r1", "Pilot", new DateOnly(2010, 1, 1)), new EpisodeInfo(2, "r2", "Second", null), new EpisodeInfo(4, "r4", "Fourth", null)]),
        ]);

        var result = await media.RematchAsync(seriesId, "222", default);

        Assert.Equal("Right Show", result.Title);
        await using var db = factory.CreateDbContext();
        var item = await db.MediaItems.SingleAsync(m => m.Id == seriesId);
        Assert.Equal("222", item.ProviderId);
        Assert.Equal("Right Show", item.Title);
        Assert.Null(item.LibraryReviewedUtc);
        Assert.False(item.HasFile);
        var episodes = await db.Episodes.Where(e => e.MediaItemId == seriesId).ToDictionaryAsync(e => e.EpisodeNumber);
        Assert.All(episodes.Values, episode => Assert.False(episode.HasFile));
        Assert.Equal(ep1, episodes[1].Id);
        Assert.Equal("Pilot", episodes[1].Title);
        Assert.Equal(ep2, episodes[2].Id);
        Assert.Equal(ep3, episodes[3].Id);
        Assert.False(episodes[3].Monitored);
        Assert.True(episodes.ContainsKey(4) && episodes[4].Monitored);
        Assert.Equal(1, result.EpisodesAdded);
        Assert.Equal(2, result.EpisodesUpdated);
        Assert.Equal(1, result.EpisodesUnmonitored);
        Assert.True(await db.DownloadJobs.AnyAsync(j => j.EpisodeId == ep1), "download history must still point at the kept episode");
    }

    [Fact]
    public async Task Fixing_a_series_unlinks_its_library_files_without_changing_them()
    {
        var (factory, keep, meta, media) = await SetupAsync();
        using var _ = keep;
        var (seriesId, _, _, _, fileId) = await SeedWrongMatchAsync(factory);
        meta.Catalog["222"] = ("Right Show", 2010, [new SeasonInfo(1, [new EpisodeInfo(1, "r1", "Pilot", null)])]);

        var result = await media.RematchAsync(seriesId, "222", default);

        Assert.Equal(1, result.FilesUnlinked);
        await using var db = factory.CreateDbContext();
        var file = await db.MediaFiles.Include(f => f.EpisodeLinks).SingleAsync(f => f.Id == fileId);
        Assert.Null(file.MediaItemId);
        Assert.Empty(file.EpisodeLinks);
        Assert.Equal(MediaFileStatus.Present, file.Status);
        Assert.Equal(Root + "/Wrong.Show.S01E01.mkv", file.Path);
    }

    [Fact]
    public async Task Fixing_a_movie_changes_its_identity_and_unlinks_its_file()
    {
        var (factory, keep, meta, media) = await SetupAsync();
        using var _ = keep;
        await using (var db = factory.CreateDbContext())
        {
            var profile = await db.QualityProfiles.FirstAsync();
            var movie = new MediaItem { MediaType = MediaType.Movie, Provider = "tmdb", ProviderId = "333", Title = "Wrong Film", Year = 1999, Monitored = true, QualityProfileId = profile.Id, RootFolder = Root, CreatedAt = DateTimeOffset.UtcNow };
            movie.HasFile = true;
            db.MediaItems.Add(movie);
            await db.SaveChangesAsync();
            db.MediaFiles.Add(new MediaFile { Path = Root + "/film.mkv", PathKey = Root + "/film.mkv", MediaItemId = movie.Id, Status = MediaFileStatus.Present });
            await db.SaveChangesAsync();
        }
        meta.Catalog["444"] = ("Right Film", 2021, []);
        int movieId;
        await using (var db = factory.CreateDbContext()) movieId = (await db.MediaItems.SingleAsync(m => m.ProviderId == "333")).Id;

        await media.RematchAsync(movieId, "444", default);

        await using var check = factory.CreateDbContext();
        var item = await check.MediaItems.SingleAsync(m => m.Id == movieId);
        Assert.Equal("Right Film", item.Title);
        Assert.Equal("444", item.ProviderId);
        Assert.False(item.HasFile);
        Assert.Null((await check.MediaFiles.SingleAsync()).MediaItemId);
    }

    [Fact]
    public async Task A_match_that_is_already_in_the_catalog_is_refused_and_nothing_changes()
    {
        var (factory, keep, meta, media) = await SetupAsync();
        using var _ = keep;
        var (seriesId, _, _, _, _) = await SeedWrongMatchAsync(factory);
        await using (var db = factory.CreateDbContext())
        {
            var profile = await db.QualityProfiles.FirstAsync();
            db.MediaItems.Add(new MediaItem { MediaType = MediaType.Series, Provider = "tmdb", ProviderId = "222", Title = "Already Here", Monitored = true, QualityProfileId = profile.Id, RootFolder = Root, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        meta.Catalog["222"] = ("Right Show", 2010, []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => media.RematchAsync(seriesId, "222", default));

        await using var check = factory.CreateDbContext();
        Assert.Equal("111", (await check.MediaItems.SingleAsync(m => m.Id == seriesId)).ProviderId);
    }

    [Fact]
    public async Task A_non_numeric_provider_id_is_rejected()
    {
        var (factory, keep, _, media) = await SetupAsync();
        using var _ = keep;
        var (seriesId, _, _, _, _) = await SeedWrongMatchAsync(factory);

        await Assert.ThrowsAsync<ArgumentException>(() => media.RematchAsync(seriesId, "not-a-number", default));
    }

    [Fact]
    public async Task Refreshing_adds_new_episodes_keeps_links_and_reopens_review_only_when_episodes_are_added()
    {
        var (factory, keep, meta, media) = await SetupAsync();
        using var _ = keep;
        var (seriesId, ep1, ep2, ep3, fileId) = await SeedWrongMatchAsync(factory);
        await using (var db = factory.CreateDbContext())
        {
            var series = await db.MediaItems.SingleAsync(m => m.Id == seriesId);
            series.ProviderId = "555";
            series.LibraryReviewedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        // The show now has season 1 episode 4 and a season 0 special.
        meta.Catalog["555"] = ("Wrong Show", 2001,
        [
            new SeasonInfo(0, [new EpisodeInfo(1, "s1", "Special", null)]),
            new SeasonInfo(1, [new EpisodeInfo(1, "e1", "Old 1", null), new EpisodeInfo(2, "e2", "Old 2", null), new EpisodeInfo(3, "e3", "Old 3", null), new EpisodeInfo(4, "e4", "New", null)]),
        ]);

        var result = await media.RefreshMetadataAsync(seriesId, default);

        // The new season 1 episode and the season 0 special.
        Assert.Equal(2, result.EpisodesAdded);
        Assert.True(result.NeedsLibraryScan);
        await using var db2 = factory.CreateDbContext();
        var item = await db2.MediaItems.SingleAsync(m => m.Id == seriesId);
        Assert.Null(item.LibraryReviewedUtc);
        var episodes = await db2.Episodes.Where(e => e.MediaItemId == seriesId).ToListAsync();
        Assert.Equal(ep1, episodes.Single(e => e.SeasonNumber == 1 && e.EpisodeNumber == 1).Id);
        Assert.Equal(ep2, episodes.Single(e => e.SeasonNumber == 1 && e.EpisodeNumber == 2).Id);
        Assert.Equal(ep3, episodes.Single(e => e.SeasonNumber == 1 && e.EpisodeNumber == 3).Id);
        Assert.False(episodes.Single(e => e.SeasonNumber == 0).Monitored, "specials are not monitored by default");
        Assert.Equal(1, await db2.MediaFileEpisodes.CountAsync(l => l.MediaFileId == fileId));
        Assert.True(await db2.DownloadJobs.AnyAsync(j => j.EpisodeId == ep1));
    }

    [Fact]
    public async Task A_refresh_that_adds_nothing_keeps_the_review_flag_and_the_links()
    {
        var (factory, keep, meta, media) = await SetupAsync();
        using var _ = keep;
        var (seriesId, _, _, _, _) = await SeedWrongMatchAsync(factory);
        await using (var db = factory.CreateDbContext())
        {
            var series = await db.MediaItems.SingleAsync(m => m.Id == seriesId);
            series.ProviderId = "555";
            await db.SaveChangesAsync();
        }
        var reviewed = DateTimeOffset.UtcNow;
        await using (var db = factory.CreateDbContext())
        {
            (await db.MediaItems.SingleAsync(m => m.Id == seriesId)).LibraryReviewedUtc = reviewed;
            await db.SaveChangesAsync();
        }
        meta.Catalog["555"] = ("Wrong Show", 2001, [new SeasonInfo(1, [new EpisodeInfo(1, "e1", "Renamed", null), new EpisodeInfo(2, "e2", "Renamed", null), new EpisodeInfo(3, "e3", "Renamed", null)])]);

        var result = await media.RefreshMetadataAsync(seriesId, default);

        Assert.False(result.NeedsLibraryScan);
        await using var check = factory.CreateDbContext();
        Assert.NotNull((await check.MediaItems.SingleAsync(m => m.Id == seriesId)).LibraryReviewedUtc);
        Assert.Equal(1, await check.MediaFileEpisodes.CountAsync());
        Assert.Equal("Renamed", (await check.Episodes.FirstAsync(e => e.SeasonNumber == 1 && e.EpisodeNumber == 1)).Title);
    }

    [Fact]
    public async Task Adding_a_series_with_all_seasons_does_not_monitor_specials()
    {
        var (factory, keep, meta, media) = await SetupAsync();
        using var _ = keep;
        meta.Catalog["777"] = ("New Show", 2020,
        [
            new SeasonInfo(0, [new EpisodeInfo(1, "sp1", "Special", null)]),
            new SeasonInfo(1, [new EpisodeInfo(1, "a1", "Pilot", null)]),
        ]);
        int profileId;
        await using (var db = factory.CreateDbContext()) profileId = (await db.QualityProfiles.FirstAsync()).Id;

        var added = await media.AddAsync(new AddMediaRequest(MediaType.Series, "777", profileId, Root, true, null, MonitorAllSeasons: true), default);

        await using var check = factory.CreateDbContext();
        var episodes = await check.Episodes.Where(e => e.MediaItemId == added.Item.Id).ToListAsync();
        Assert.False(episodes.Single(e => e.SeasonNumber == 0).Monitored);
        Assert.True(episodes.Single(e => e.SeasonNumber == 1).Monitored);
    }

    [Fact]
    public void An_unmatched_series_carries_its_parsed_title_so_it_can_be_added()
    {
        var file = new CandidateFile(Root + "/Rey.Mysterio.vs.The.Darkness.2022.S01E01.Enygma.1080p.HMAX.WEB-DL.mkv",
            "Rey.Mysterio.vs.The.Darkness.2022.S01.1080p.HMAX/Rey.Mysterio.vs.The.Darkness.2022.S01E01.Enygma.1080p.HMAX.WEB-DL.mkv", 10);

        var match = LibraryMatcher.Match(file, MediaType.Series, [], [], windows: false);

        Assert.Equal(MatchOutcome.Unmatched, match.Outcome);
        Assert.Equal("Rey Mysterio vs The Darkness", match.SuggestedTitle);
        Assert.Equal(2022, match.SuggestedYear);
    }

    /// <summary>
    /// A weekly show gains an episode on every refresh. That episode cannot already be on disk from before reconciliation,
    /// so it must not pause automatic grabbing; otherwise the scheduled refresh would stop every new episode being fetched.
    /// </summary>
    [Fact]
    public async Task Refreshing_in_newly_aired_or_upcoming_episodes_keeps_the_review_and_automation()
    {
        var (factory, keep, meta, media) = await SetupAsync();
        using var _ = keep;
        var (seriesId, _, _, _, _) = await SeedWrongMatchAsync(factory);
        var reviewed = DateTimeOffset.UtcNow;
        await using (var db = factory.CreateDbContext())
        {
            var series = await db.MediaItems.SingleAsync(m => m.Id == seriesId);
            series.ProviderId = "555";
            series.LibraryReviewedUtc = reviewed;
            await db.SaveChangesAsync();
        }
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        meta.Catalog["555"] = ("Wrong Show", 2001,
        [
            new SeasonInfo(1, [new EpisodeInfo(1, "e1", "One", null), new EpisodeInfo(2, "e2", "Two", null), new EpisodeInfo(3, "e3", "Three", null),
                new EpisodeInfo(4, "e4", "Aired today", today), new EpisodeInfo(5, "e5", "Next week", today.AddDays(7))]),
        ]);

        var result = await media.RefreshMetadataAsync(seriesId, default);

        Assert.Equal(2, result.EpisodesAdded);
        Assert.False(result.NeedsLibraryScan);
        await using var check = factory.CreateDbContext();
        var item = await check.MediaItems.SingleAsync(m => m.Id == seriesId);
        Assert.NotNull(item.LibraryReviewedUtc);
        Assert.NotNull(item.MetadataRefreshedUtc);
    }

    [Fact]
    public async Task Scheduled_refresh_takes_the_stalest_monitored_series_up_to_the_limit()
    {
        var (factory, keep, meta, media) = await SetupAsync();
        using var _ = keep;
        int never, stale, fresh, unmonitored;
        await using (var db = factory.CreateDbContext())
        {
            var profile = await db.QualityProfiles.FirstAsync();
            MediaItem Series(string tmdb, bool monitored, DateTimeOffset? refreshed) => new()
            {
                MediaType = MediaType.Series, Provider = "tmdb", ProviderId = tmdb, Title = "Show " + tmdb, Year = 2020, Monitored = monitored,
                QualityProfileId = profile.Id, RootFolder = Root, CreatedAt = DateTimeOffset.UtcNow, MetadataRefreshedUtc = refreshed,
            };
            var a = Series("901", true, null);
            var b = Series("902", true, DateTimeOffset.UtcNow.AddHours(-20));
            var c = Series("903", true, DateTimeOffset.UtcNow.AddHours(-1));
            var d = Series("904", false, null);
            db.MediaItems.AddRange(a, b, c, d);
            await db.SaveChangesAsync();
            (never, stale, fresh, unmonitored) = (a.Id, b.Id, c.Id, d.Id);
        }
        foreach (var id in new[] { "901", "902", "903", "904" })
            meta.Catalog[id] = ("Show " + id, 2020, [new SeasonInfo(1, [new EpisodeInfo(1, id + "e1", "Pilot", new DateOnly(2020, 1, 1))])]);

        var first = await media.RefreshDueSeriesAsync(1, TimeSpan.FromHours(12), default);
        var second = await media.RefreshDueSeriesAsync(5, TimeSpan.FromHours(12), default);

        Assert.StartsWith("Refreshed 1 series", first);
        Assert.StartsWith("Refreshed 1 series", second);
        await using var check = factory.CreateDbContext();
        var items = await check.MediaItems.ToDictionaryAsync(m => m.Id);
        Assert.NotNull(items[never].MetadataRefreshedUtc);
        Assert.True(items[stale].MetadataRefreshedUtc > DateTimeOffset.UtcNow.AddMinutes(-5), "the stale series is refreshed on the second pass");
        Assert.True(items[fresh].MetadataRefreshedUtc < DateTimeOffset.UtcNow.AddMinutes(-30), "a series refreshed an hour ago is left alone");
        Assert.Null(items[unmonitored].MetadataRefreshedUtc);
        Assert.Equal(1, await check.Episodes.CountAsync(e => e.MediaItemId == never));
    }
}
