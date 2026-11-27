using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using Xunit;

namespace OneArr.Tests;

/// <summary>Anime: absolute episode numbers in release names, matched only for series marked as anime.</summary>
public class AnimeTests
{
    private const string Hash = "abcdef0123456789abcdef0123456789abcdef01";

    [Theory]
    [InlineData("[SubsPlease] One Piece - 1120 (1080p) [ABCD1234].mkv", "One Piece", new[] { 1120 })]
    [InlineData("[Erai-raws] Dandadan - 05v2 [1080p][Multiple Subtitle].mkv", "Dandadan", new[] { 5 })]
    [InlineData("[Group] Show Title - 05-06 [720p]", "Show Title", new[] { 5, 6 })]
    public void Absolute_numbers_are_parsed(string name, string title, int[] episodes)
    {
        var p = new ReleaseParser().Parse(name);
        Assert.Equal(title, p.Title);
        Assert.Equal(episodes, p.AbsoluteEpisodes);
        Assert.Null(p.Season);
        Assert.Equal(name.Contains("720p") ? 720 : 1080, p.Resolution);
    }

    [Theory]
    [InlineData("Show.S01E05.1080p.WEB-DL.x264-GRP")]
    [InlineData("Some Movie - 2019 (1080p)")]
    public void Normal_names_have_no_absolute_number(string name) => Assert.Null(new ReleaseParser().Parse(name).AbsoluteEpisodes);

    private static ReleaseTarget Target(int? absolute) =>
        new(MediaType.Series, "One Piece", 1999, 21, 228, new QualityProfile { MinResolution = 720, CutoffResolution = 1080 }, null, false,
            new HashSet<string>(), AbsoluteEpisode: absolute);

    [Fact]
    public void Ranker_matches_the_absolute_number_only_for_anime()
    {
        var ranker = new ReleaseRanker(new ReleaseParser());
        var c = new ReleaseCandidate("[SubsPlease] One Piece - 1120 (1080p) [ABCD1234].mkv", "magnet:?xt=urn:btih:" + Hash, 1_400_000_000, 50, Hash, "nyaa", []);
        Assert.True(ranker.Evaluate(c, Target(1120)).Accepted, string.Join("; ", ranker.Evaluate(c, Target(1120)).Reasons));
        Assert.Contains(ranker.Evaluate(c, Target(1121)).Reasons, r => r.StartsWith("Wrong episode"));
        Assert.Contains(ranker.Evaluate(c, Target(null)).Reasons, r => r.Contains("mark the series as anime"));
    }

    [Fact]
    public async Task Marking_a_series_as_anime_numbers_regular_episodes_and_searches_by_absolute_number()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        var (series, episodes) = await w.AddSeriesAsync("Dandadan", episodes: 3);
        await using (var db = w.Db.CreateDbContext())
        {
            db.Episodes.Add(new Episode { MediaItemId = series, SeasonNumber = 0, EpisodeNumber = 1, ProviderId = "sp", Title = "Special", AirDate = new DateOnly(2010, 1, 1), Monitored = false });
            await db.SaveChangesAsync();
        }
        var media = new OneArr.Infrastructure.Services.MediaService(w.Db, new NoMetadata(), w.Time);
        await media.UpdateAsync(series, null, null, null, default, isAnime: true);
        await using (var db = w.Db.CreateDbContext())
        {
            var numbers = await db.Episodes.Where(e => e.MediaItemId == series).OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).Select(e => e.AbsoluteNumber).ToListAsync();
            Assert.Equal([null, 1, 2, 3], numbers);
        }

        w.Indexer.Body = TestWorld.Torznab(("[SubsPlease] Dandadan - 02 (1080p) [ABCD1234].mkv", Hash, 40));
        var result = await w.Releases.SearchAsync(series, episodes[1], default);

        var query = Uri.UnescapeDataString(Assert.Single(w.Indexer.Queries)).Replace('+', ' ');
        Assert.Contains("t=search", query);
        Assert.Contains("q=Dandadan 02", query);
        Assert.True(result.Decisions.Single().Accepted, string.Join("; ", result.Decisions.Single().Reasons));
    }

    private sealed class NoMetadata : IMetadataProvider
    {
        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(MediaType type, string query, CancellationToken ct) => Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);
        public Task<MetadataDetails> GetDetailsAsync(MediaType type, string providerId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SeasonInfo>> GetSeasonsAsync(string providerId, CancellationToken ct) => throw new NotSupportedException();
    }
}
