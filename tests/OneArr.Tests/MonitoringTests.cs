using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Legacy;
using OneArr.Infrastructure.Providers;
using OneArr.Infrastructure.Services;
using Xunit;

namespace OneArr.Tests;

/// <summary>Monitoring fidelity: Sonarr season flags survive migration, sync only touches Monitored flags on
/// already-imported items, and Wanted drops unmonitored episodes after the sync.</summary>
public class MonitoringTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class StubMetadata : IMetadataProvider
    {
        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(MediaType type, string query, CancellationToken ct) => throw new NotImplementedException();
        public Task<MetadataDetails> GetDetailsAsync(MediaType type, string providerId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<SeasonInfo>> GetSeasonsAsync(string providerId, CancellationToken ct) => throw new NotImplementedException();
    }

    [Fact]
    public async Task LegacyReaderParsesSonarrSeasons()
    {
        var json = "[{\"title\":\"WWE Raw\",\"year\":1993,\"tmdbId\":123,\"tvdbId\":456,\"path\":\"/data/tv/WWE Raw\",\"monitored\":true,"
            + "\"seasons\":[{\"seasonNumber\":0,\"monitored\":false},{\"seasonNumber\":1,\"monitored\":false},{\"seasonNumber\":2,\"monitored\":true}]}]";
        var reader = new LegacyArrReader(new FakeFactory(new FakeHandler(_ => FakeHandler.Text(json))));

        var items = await reader.ReadAsync(new LegacyArrSource("sonarr", "http://sonarr.test", "key"), Ct);

        var item = Assert.Single(items);
        Assert.True(item.Monitored);
        Assert.Equal(3, item.Seasons!.Count);
        Assert.Equal(1, item.Seasons[1].SeasonNumber);
        Assert.False(item.Seasons[1].Monitored);
        Assert.True(item.Seasons[2].Monitored);
    }

    [Fact]
    public async Task MonitoringSyncPreviewChangesNothingButApplyUpdatesSeasonsAndEpisodesOnly()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        var (seriesId, episodeIds) = await w.AddSeriesAsync("WWE Raw", 3);
        await using (var db = w.Db.CreateDbContext())
        {
            db.Seasons.Add(new Season { MediaItemId = seriesId, SeasonNumber = 1, Monitored = true });
            await db.SaveChangesAsync();
        }
        var media = new MediaService(w.Db, new StubMetadata(), w.Time);
        var legacy = new LegacyItem("sonarr", MediaType.Series, "WWE Raw", 1993, 123, null, null, "/data/tv/WWE Raw", true, null,
            [new LegacySeason(1, false)]);

        // The test series was added with a generated provider id, so match it by re-keying: sync matches on
        // TMDb id, so set the item's provider id to the legacy TMDb id first (test setup only).
        await using (var db = w.Db.CreateDbContext())
        {
            var item = await db.MediaItems.SingleAsync(m => m.Id == seriesId);
            item.ProviderId = "123";
            await db.SaveChangesAsync();
        }

        var preview = await media.SyncLegacyMonitoringAsync([legacy], apply: false, Ct);
        Assert.Single(preview.Changes);
        Assert.Equal(1, preview.Changes[0].SeasonsChanged);
        Assert.Equal(3, preview.Changes[0].EpisodesChanged);
        await using (var db = w.Db.CreateDbContext())
        {
            Assert.True((await db.Seasons.SingleAsync()).Monitored);
            Assert.All(await db.Episodes.ToListAsync(), e => Assert.True(e.Monitored));
        }

        var applied = await media.SyncLegacyMonitoringAsync([legacy], apply: true, Ct);
        Assert.Single(applied.Changes);
        await using (var db = w.Db.CreateDbContext())
        {
            Assert.False((await db.Seasons.SingleAsync()).Monitored);
            Assert.All(await db.Episodes.ToListAsync(), e => Assert.False(e.Monitored));
            // Series-level flag, title, and file state are untouched by a season-only change.
            var item = await db.MediaItems.SingleAsync(m => m.Id == seriesId);
            Assert.True(item.Monitored);
            Assert.False(item.HasFile);
        }

        // Wanted now drops the unmonitored episodes: they were the only episodes, so wanted is empty.
        var wanted = await media.WantedAsync(Ct);
        Assert.Empty(wanted.Episodes);
        Assert.Equal(3, wanted.UnmonitoredEpisodesWithoutFile);
    }

    [Fact]
    public async Task MonitoringSyncWithoutSeasonsLeavesEpisodesAlone()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        var (seriesId, _) = await w.AddSeriesAsync("Show", 2);
        await using (var db = w.Db.CreateDbContext())
        {
            var item = await db.MediaItems.SingleAsync(m => m.Id == seriesId);
            item.ProviderId = "999";
            await db.SaveChangesAsync();
        }
        var media = new MediaService(w.Db, new StubMetadata(), w.Time);
        var legacy = new LegacyItem("sonarr", MediaType.Series, "Show", 2020, 999, null, null, "/data/tv/Show", false, null, []);

        var result = await media.SyncLegacyMonitoringAsync([legacy], apply: true, Ct);

        Assert.Single(result.Changes);
        Assert.Equal(0, result.Changes[0].SeasonsChanged);
        Assert.Equal(0, result.Changes[0].EpisodesChanged);
        await using (var db = w.Db.CreateDbContext())
        {
            Assert.False((await db.MediaItems.SingleAsync(m => m.Id == seriesId)).Monitored);
            Assert.All(await db.Episodes.ToListAsync(), e => Assert.True(e.Monitored));
        }
    }

    [Fact]
    public async Task SeasonToggleSetsEpisodesAndEpisodeToggleLeavesSeasonAlone()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        var (seriesId, episodeIds) = await w.AddSeriesAsync("Show", 2);
        await using (var db = w.Db.CreateDbContext())
        {
            db.Seasons.Add(new Season { MediaItemId = seriesId, SeasonNumber = 1, Monitored = true });
            await db.SaveChangesAsync();
        }
        var media = new MediaService(w.Db, new StubMetadata(), w.Time);

        Assert.True(await media.UpdateSeasonMonitoredAsync(seriesId, 1, false, Ct));
        await using (var db = w.Db.CreateDbContext())
        {
            Assert.False((await db.Seasons.SingleAsync()).Monitored);
            Assert.All(await db.Episodes.ToListAsync(), e => Assert.False(e.Monitored));
        }

        Assert.True(await media.UpdateEpisodeMonitoredAsync(episodeIds[0], true, Ct));
        await using (var db = w.Db.CreateDbContext())
        {
            Assert.False((await db.Seasons.SingleAsync()).Monitored);
            Assert.True((await db.Episodes.SingleAsync(e => e.Id == episodeIds[0])).Monitored);
            Assert.False((await db.Episodes.SingleAsync(e => e.Id == episodeIds[1])).Monitored);
        }
        Assert.False(await media.UpdateSeasonMonitoredAsync(seriesId, 99, true, Ct));
    }

    [Fact]
    public async Task PreviewReportsSeriesOnlyMonitoredChangeWithoutWriting()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        var (seriesId, _) = await w.AddSeriesAsync("Show", 2);
        await using (var db = w.Db.CreateDbContext())
        {
            var item = await db.MediaItems.SingleAsync(m => m.Id == seriesId);
            item.ProviderId = "777";
            await db.SaveChangesAsync();
        }
        var media = new MediaService(w.Db, new StubMetadata(), w.Time);
        var legacy = new LegacyItem("sonarr", MediaType.Series, "Show", 2020, 777, null, null, "/data/tv/Show", false, null, [], null, false);

        var preview = await media.SyncLegacyMonitoringAsync([legacy], apply: false, Ct);

        var change = Assert.Single(preview.Changes);
        Assert.True(change.MonitoredChanged);
        Assert.True(change.SeriesMonitoredBefore);
        Assert.False(change.SeriesMonitoredAfter);
        Assert.Equal(0, change.SeasonsChanged);
        Assert.Equal(0, change.EpisodesChanged);
        await using (var db = w.Db.CreateDbContext())
        {
            Assert.True((await db.MediaItems.SingleAsync(m => m.Id == seriesId)).Monitored);
        }

        var applied = await media.SyncLegacyMonitoringAsync([legacy], apply: true, Ct);
        Assert.Single(applied.Changes);
        await using (var db = w.Db.CreateDbContext())
        {
            Assert.False((await db.MediaItems.SingleAsync(m => m.Id == seriesId)).Monitored);
        }
    }

    [Fact]
    public async Task PerEpisodeExceptionsInsideMonitoredSeasonArePreserved()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        var (seriesId, episodeIds) = await w.AddSeriesAsync("Show", 3);
        await using (var db = w.Db.CreateDbContext())
        {
            var item = await db.MediaItems.SingleAsync(m => m.Id == seriesId);
            item.ProviderId = "888";
            // One extra episode Sonarr does not report: it must be left alone, not forced by any flag.
            db.Episodes.Add(new Episode { MediaItemId = seriesId, SeasonNumber = 1, EpisodeNumber = 99, ProviderId = "e99", Title = "Extra", AirDate = new DateOnly(2010, 1, 1), Monitored = true });
            db.Seasons.Add(new Season { MediaItemId = seriesId, SeasonNumber = 1, Monitored = true });
            await db.SaveChangesAsync();
        }
        var media = new MediaService(w.Db, new StubMetadata(), w.Time);
        var legacy = new LegacyItem("sonarr", MediaType.Series, "Show", 2020, 888, null, null, "/data/tv/Show", true, null,
            [new LegacySeason(1, true)],
            [new LegacyEpisode(1, 1, true), new LegacyEpisode(1, 2, false), new LegacyEpisode(1, 3, true)], false);

        var result = await media.SyncLegacyMonitoringAsync([legacy], apply: true, Ct);

        var change = Assert.Single(result.Changes);
        Assert.Equal(1, change.EpisodesChanged);
        var episodeChange = Assert.Single(change.EpisodeChanges);
        Assert.Equal(1, episodeChange.SeasonNumber);
        Assert.Equal(2, episodeChange.EpisodeNumber);
        Assert.True(episodeChange.Before);
        Assert.False(episodeChange.After);
        await using (var db = w.Db.CreateDbContext())
        {
            var eps = await db.Episodes.OrderBy(e => e.EpisodeNumber).ToListAsync();
            Assert.True(eps[0].Monitored);
            Assert.False(eps[1].Monitored);
            Assert.True(eps[2].Monitored);
            Assert.True(eps[3].Monitored);
        }
    }

    [Fact]
    public async Task UnknownEpisodeDataLeavesEpisodesAloneButStillSyncsSeasons()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        var (seriesId, _) = await w.AddSeriesAsync("Show", 2);
        await using (var db = w.Db.CreateDbContext())
        {
            var item = await db.MediaItems.SingleAsync(m => m.Id == seriesId);
            item.ProviderId = "555";
            db.Seasons.Add(new Season { MediaItemId = seriesId, SeasonNumber = 1, Monitored = true });
            await db.SaveChangesAsync();
        }
        var media = new MediaService(w.Db, new StubMetadata(), w.Time);
        var legacy = new LegacyItem("sonarr", MediaType.Series, "Show", 2020, 555, null, null, "/data/tv/Show", true, null,
            [new LegacySeason(1, false)], null, true);

        var result = await media.SyncLegacyMonitoringAsync([legacy], apply: true, Ct);

        Assert.Equal(1, Assert.Single(result.Changes).SeasonsChanged);
        Assert.Equal(0, Assert.Single(result.Changes).EpisodesChanged);
        Assert.Contains("Show", result.EpisodesUnknownTitles);
        await using (var db = w.Db.CreateDbContext())
        {
            Assert.False((await db.Seasons.SingleAsync()).Monitored);
            Assert.All(await db.Episodes.ToListAsync(), e => Assert.True(e.Monitored));
        }
    }

    [Fact]
    public async Task ReaderParsesEpisodesAndMarksUnknownWhenEpisodeReadFails()
    {
        var series = "[{\"id\":42,\"title\":\"Show\",\"year\":2020,\"tmdbId\":123,\"path\":\"/data/tv/Show\",\"monitored\":true,"
            + "\"seasons\":[{\"seasonNumber\":1,\"monitored\":true}]}]";
        var episodes = "[{\"seasonNumber\":1,\"episodeNumber\":1,\"monitored\":true},{\"seasonNumber\":1,\"episodeNumber\":2,\"monitored\":false}]";
        var reader = new LegacyArrReader(new FakeFactory(new FakeHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("/episode") ? FakeHandler.Text(episodes) : FakeHandler.Text(series))));

        var items = await reader.ReadAsync(new LegacyArrSource("sonarr", "http://sonarr.test", "key"), Ct);

        var item = Assert.Single(items);
        Assert.Equal(2, item.Episodes!.Count);
        Assert.False(item.Episodes[1].Monitored);
        Assert.False(item.EpisodesUnknown);

        var failing = new LegacyArrReader(new FakeFactory(new FakeHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("/episode") ? FakeHandler.Text("nope", System.Net.HttpStatusCode.InternalServerError) : FakeHandler.Text(series))));
        var failed = await failing.ReadAsync(new LegacyArrSource("sonarr", "http://sonarr.test", "key"), Ct);
        Assert.Null(Assert.Single(failed).Episodes);
        Assert.True(Assert.Single(failed).EpisodesUnknown);
    }

    [Fact]
    public async Task ReaderMarksEpisodesUnknownWhenSonarrIdIsAbsent()
    {
        // No "id" on the series record: no episode request can be made, so the series must be marked
        // unknown rather than left in the ambiguous Episodes-null / EpisodesUnknown-false state that
        // sync would treat as "fall back to season flags".
        var seriesWithoutId = "[{\"title\":\"Show\",\"year\":2020,\"tmdbId\":123,\"path\":\"/data/tv/Show\",\"monitored\":true,"
            + "\"seasons\":[{\"seasonNumber\":1,\"monitored\":true}]}]";
        var reader = new LegacyArrReader(new FakeFactory(new FakeHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("/episode")
                ? throw new InvalidOperationException("no episode request should be made without a series id")
                : FakeHandler.Text(seriesWithoutId))));

        var items = await reader.ReadAsync(new LegacyArrSource("sonarr", "http://sonarr.test", "key"), Ct);

        var item = Assert.Single(items);
        Assert.Null(item.Episodes);
        Assert.True(item.EpisodesUnknown);
    }

    [Fact]
    public void PreviewGateAndSeriesImportSkipRules()
    {
        var fresh = new MonitoringSyncResult(
            [new MonitoringChange("Show", "series", true, false, true, 0, 0, false, 123, [], [])], [], 0, 1, []);
        var matching = new OneArr.Api.ApiEndpoints.MonitoringSyncPreview(
            [new MonitoringChange("Show", "series", true, false, true, 0, 0, false, 123, [], [])], [], 0, 1, []);
        var drifted = new OneArr.Api.ApiEndpoints.MonitoringSyncPreview(
            [new MonitoringChange("Show", "series", true, true, false, 0, 0, false, 123, [], [])], [], 0, 1, []);

        Assert.True(OneArr.Api.ApiEndpoints.PreviewMatches(fresh, matching));
        Assert.False(OneArr.Api.ApiEndpoints.PreviewMatches(fresh, drifted));
        Assert.False(OneArr.Api.ApiEndpoints.PreviewMatches(fresh, matching with { ItemsChecked = 2 }));

        // Same counts, different identities: a preview for episodes 1 and 2 must NOT pass after the
        // source moves to episodes 3 and 4, and a preview for season 1 must not pass for season 2.
        var freshIdentities = new MonitoringSyncResult(
            [new MonitoringChange("Show", "series", false, false, false, 1, 2, false, 123,
                [new SeasonChange(1, true, false)],
                [new EpisodeChange(1, 1, true, false), new EpisodeChange(1, 2, true, false)])], [], 0, 1, []);
        var sameCountDifferentEpisodes = new OneArr.Api.ApiEndpoints.MonitoringSyncPreview(
            [new MonitoringChange("Show", "series", false, false, false, 1, 2, false, 123,
                [new SeasonChange(1, true, false)],
                [new EpisodeChange(1, 3, true, false), new EpisodeChange(1, 4, true, false)])], [], 0, 1, []);
        var sameCountDifferentSeason = new OneArr.Api.ApiEndpoints.MonitoringSyncPreview(
            [new MonitoringChange("Show", "series", false, false, false, 1, 2, false, 123,
                [new SeasonChange(2, true, false)],
                [new EpisodeChange(1, 1, true, false), new EpisodeChange(1, 2, true, false)])], [], 0, 1, []);
        var sameCountDifferentValue = new OneArr.Api.ApiEndpoints.MonitoringSyncPreview(
            [new MonitoringChange("Show", "series", false, false, false, 1, 2, false, 123,
                [new SeasonChange(1, true, false)],
                [new EpisodeChange(1, 1, true, false), new EpisodeChange(1, 2, false, true)])], [], 0, 1, []);
        var identical = new OneArr.Api.ApiEndpoints.MonitoringSyncPreview(
            [new MonitoringChange("Show", "series", false, false, false, 1, 2, false, 123,
                [new SeasonChange(1, true, false)],
                [new EpisodeChange(1, 1, true, false), new EpisodeChange(1, 2, true, false)])], [], 0, 1, []);

        Assert.False(OneArr.Api.ApiEndpoints.PreviewMatches(freshIdentities, sameCountDifferentEpisodes));
        Assert.False(OneArr.Api.ApiEndpoints.PreviewMatches(freshIdentities, sameCountDifferentSeason));
        Assert.False(OneArr.Api.ApiEndpoints.PreviewMatches(freshIdentities, sameCountDifferentValue));
        Assert.True(OneArr.Api.ApiEndpoints.PreviewMatches(freshIdentities, identical));

        Assert.True(OneArr.Api.ApiEndpoints.ShouldSkipNewSeriesImport(MediaType.Series, null, exists: false));
        Assert.True(OneArr.Api.ApiEndpoints.ShouldSkipNewSeriesImport(MediaType.Series, [], exists: false));
        Assert.False(OneArr.Api.ApiEndpoints.ShouldSkipNewSeriesImport(MediaType.Series, null, exists: true));
        Assert.False(OneArr.Api.ApiEndpoints.ShouldSkipNewSeriesImport(MediaType.Series, [new OneArr.Api.ApiEndpoints.SeasonSelection(1, true)], exists: false));
        Assert.False(OneArr.Api.ApiEndpoints.ShouldSkipNewSeriesImport(MediaType.Movie, null, exists: false));
    }
}
