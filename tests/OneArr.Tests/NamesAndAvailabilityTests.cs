using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Providers;
using Xunit;

namespace OneArr.Tests;

/// <summary>Alternate titles for matching release names, and movie availability from release dates.</summary>
public class NamesAndAvailabilityTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string HashA = "abcdef0123456789abcdef0123456789abcdef01";

    [Theory]
    [InlineData("Shōgun", "Shogun")]
    [InlineData("Law & Order", "Law.and.Order")]
    [InlineData("Amélie", "Amelie")]
    public void Accents_and_ampersands_do_not_block_a_title_match(string title, string release) =>
        Assert.Equal(ReleaseParser.NormalizeTitle(title), ReleaseParser.NormalizeTitle(release));

    // Shape of TMDb /movie/{id}?append_to_response=alternative_titles,release_dates (per TMDb's API reference).
    private const string MovieJson = """
        {"id":1,"title":"Dune: Part Two","original_title":"Dune: Part Two",
         "alternative_titles":{"titles":[{"iso_3166_1":"US","title":"Dune 2","type":""},{"iso_3166_1":"JP","title":"デューン 砂の惑星PART2","type":""},{"iso_3166_1":"FR","title":"Dune : Deuxième partie","type":""}]},
         "release_dates":{"results":[
           {"iso_3166_1":"US","release_dates":[{"type":3,"release_date":"2024-03-01T00:00:00.000Z"},{"type":4,"release_date":"2024-04-16T00:00:00.000Z"},{"type":5,"release_date":"2024-05-14T00:00:00.000Z"}]},
           {"iso_3166_1":"FR","release_dates":[{"type":3,"release_date":"2024-02-28T00:00:00.000Z"},{"type":4,"release_date":"2024-06-20T00:00:00.000Z"}]}]}}
        """;

    [Fact]
    public void Movie_names_keep_comparable_alternatives_only()
    {
        using var doc = JsonDocument.Parse(MovieJson);
        var names = TmdbMetadataProvider.AlternateTitlesOf(doc.RootElement, MediaType.Movie, "Dune: Part Two");
        Assert.Equal(["Dune 2", "Dune : Deuxième partie"], names);
    }

    [Fact]
    public void Series_alternative_titles_come_from_results()
    {
        using var doc = JsonDocument.Parse("""{"name":"Daredevil","original_name":"Marvel's Daredevil","alternative_titles":{"results":[{"title":"Daredevil (2015)"}]}}""");
        Assert.Equal(["Marvel's Daredevil", "Daredevil (2015)"], TmdbMetadataProvider.AlternateTitlesOf(doc.RootElement, MediaType.Series, "Daredevil"));
    }

    [Fact]
    public void Release_dates_are_the_earliest_of_each_kind()
    {
        using var doc = JsonDocument.Parse(MovieJson);
        var dates = TmdbMetadataProvider.ReleaseDatesOf(doc.RootElement);
        Assert.Equal(new MovieReleaseDates(new DateOnly(2024, 2, 28), new DateOnly(2024, 4, 16), new DateOnly(2024, 5, 14)), dates);
    }

    [Theory]
    [InlineData("2024-03-01", null, null, "2024-04-01", false)] // in cinemas
    [InlineData("2024-03-01", null, null, "2024-06-01", true)]  // 90 days after cinemas
    [InlineData("2024-03-01", "2024-04-16", null, "2024-04-15", false)]
    [InlineData("2024-03-01", "2024-04-16", null, "2024-04-16", true)]
    [InlineData(null, null, "2024-05-14", "2024-05-14", true)]
    [InlineData(null, null, null, "2024-01-01", true)]          // nothing known: as before
    public void Movie_availability(string? theatrical, string? digital, string? physical, string today, bool expected) =>
        Assert.Equal(expected, MediaItem.MovieAvailable(D(theatrical), D(digital), D(physical), DateOnly.Parse(today)));

    private static DateOnly? D(string? s) => s is null ? null : DateOnly.Parse(s);

    [Fact]
    public void Ranker_accepts_a_release_under_an_alternate_title()
    {
        var ranker = new ReleaseRanker(new ReleaseParser());
        var profile = new QualityProfile { MinResolution = 720, CutoffResolution = 1080, AllowWebSources = true };
        var c = new ReleaseCandidate("Marvels.Daredevil.S01E01.1080p.WEB-DL.x264-GRP", "magnet:?xt=urn:btih:" + HashA, 2_000_000_000, 10, HashA, "t", []);
        var without = ranker.Evaluate(c, new ReleaseTarget(MediaType.Series, "Daredevil", 2015, 1, 1, profile, null, false, new HashSet<string>()));
        var with = ranker.Evaluate(c, new ReleaseTarget(MediaType.Series, "Daredevil", 2015, 1, 1, profile, null, false, new HashSet<string>(), ["Marvel's Daredevil"]));
        Assert.False(without.Accepted);
        Assert.True(with.Accepted, string.Join("; ", with.Reasons));
    }

    [Fact]
    public async Task Wanted_search_skips_a_movie_still_in_cinemas()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        var id = await w.AddMovieAsync("New Movie", 2026);
        var today = DateOnly.FromDateTime(w.Time.GetUtcNow().UtcDateTime);
        await using (var db = w.Db.CreateDbContext())
        {
            (await db.MediaItems.SingleAsync(m => m.Id == id)).TheatricalReleaseDate = today.AddDays(-10);
            await db.SaveChangesAsync();
        }
        await w.ReconcileLibraryAsync();

        Assert.Equal(0, (await w.Search.RunOnceAsync(Ct)).Considered);
        Assert.Empty(w.Indexer.Queries);

        await using (var db = w.Db.CreateDbContext())
        {
            (await db.MediaItems.SingleAsync(m => m.Id == id)).DigitalReleaseDate = today;
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, (await w.Search.RunOnceAsync(Ct)).Searched);
    }
}
