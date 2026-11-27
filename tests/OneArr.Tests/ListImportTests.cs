using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Providers;
using OneArr.Infrastructure.Services;
using Xunit;

namespace OneArr.Tests;

/// <summary>List import: TMDb lists and IMDb ids are previewed, and only the chosen titles are added with the defaults.</summary>
public class ListImportTests
{
    [Theory]
    [InlineData("8312345", "8312345")]
    [InlineData("https://www.themoviedb.org/list/8312345-best-of-2024", "8312345")]
    [InlineData("https://www.themoviedb.org/list/8312345?language=en", "8312345")]
    [InlineData("https://example.com/list/8312345", null)]
    public void Tmdb_list_ids(string value, string? id) => Assert.Equal(id, ListImportService.TmdbListId(value));

    [Fact]
    public void Imdb_ids_are_read_from_an_export_or_a_paste() =>
        Assert.Equal(["tt0133093", "tt1375666"], ListImportService.ImdbIds("Const,Title\ntt0133093,The Matrix\ntt1375666,Inception\ntt0133093,dup"));

    [Fact]
    public async Task Tmdb_list_pages_are_read_through_the_provider()
    {
        var http = new RoutingHandler();
        http.Route("tmdb.test", req =>
        {
            var page = req.RequestUri!.Query.Contains("page=2") ? 2 : 1;
            var body = page == 1
                ? """{"total_pages":2,"items":[{"media_type":"movie","id":603,"title":"The Matrix","release_date":"1999-03-30"},{"media_type":"person","id":1}]}"""
                : """{"total_pages":2,"items":[{"media_type":"tv","id":2190,"name":"South Park","first_air_date":"1997-08-13"}]}""";
            return FakeHandler.Text(body);
        });
        var provider = new TmdbMetadataProvider(new FakeFactory(http), () => Task.FromResult<string?>("key"), new Uri("http://tmdb.test/3/"));

        var items = await provider.GetListAsync("42", default);

        Assert.Equal([new ListItem(MediaType.Movie, "603", "The Matrix", 1999), new ListItem(MediaType.Series, "2190", "South Park", 1997)], items);
    }

    private sealed class ListMeta : IMetadataProvider
    {
        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(MediaType type, string query, CancellationToken ct) => Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);
        public Task<MetadataDetails> GetDetailsAsync(MediaType type, string providerId, CancellationToken ct) =>
            Task.FromResult(new MetadataDetails(providerId, providerId == "603" ? "The Matrix" : "Inception", 1999, null, null, null, null, 0));
        public Task<IReadOnlyList<SeasonInfo>> GetSeasonsAsync(string providerId, CancellationToken ct) => Task.FromResult<IReadOnlyList<SeasonInfo>>([]);
        public Task<ListItem?> FindByImdbAsync(string imdbId, CancellationToken ct) => Task.FromResult<ListItem?>(imdbId switch
        {
            "tt0133093" => new ListItem(MediaType.Movie, "603", "The Matrix", 1999),
            "tt1375666" => new ListItem(MediaType.Movie, "27205", "Inception", 2010),
            _ => null,
        });
    }

    [Fact]
    public async Task Preview_marks_titles_already_in_the_catalog_and_add_uses_the_defaults()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        await w.AddMovieAsync("The Matrix", 1999);
        await using (var db = w.Db.CreateDbContext())
        {
            (await db.MediaItems.SingleAsync()).ProviderId = "603";
            await db.SaveChangesAsync();
        }
        await w.Settings.SetAsync(SettingKeys.DefaultMovieRoot, Path.Combine(w.Dirs.Media, "movies"));
        await w.Settings.SetAsync(SettingKeys.DefaultMovieProfile, w.ProfileId.ToString());
        var meta = new ListMeta();
        var lists = new ListImportService(w.Db, meta, new MediaService(w.Db, meta, w.Time), w.Settings);

        var preview = await lists.PreviewAsync("imdb", "tt0133093 tt1375666 tt9999999", default);
        Assert.Equal(["tt9999999"], preview.NotFound);
        Assert.True(preview.Items.Single(i => i.ProviderId == "603").InCatalog);
        Assert.False(preview.Items.Single(i => i.ProviderId == "27205").InCatalog);

        var result = await lists.AddAsync([new ListAddItem("movie", "27205"), new ListAddItem("movie", "603")], monitored: true, default);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.AlreadyThere);
        await using var check = w.Db.CreateDbContext();
        var inception = await check.MediaItems.SingleAsync(m => m.ProviderId == "27205");
        Assert.Equal(Path.Combine(w.Dirs.Media, "movies"), inception.RootFolder);
        Assert.Equal(w.ProfileId, inception.QualityProfileId);
    }

    [Fact]
    public async Task Without_default_folders_nothing_is_added_and_the_reason_is_given()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        var meta = new ListMeta();
        var lists = new ListImportService(w.Db, meta, new MediaService(w.Db, meta, w.Time), w.Settings);
        var result = await lists.AddAsync([new ListAddItem("movie", "27205")], true, default);
        Assert.Equal(0, result.Added);
        Assert.Contains(result.Errors, e => e.Contains("default movie root folder"));
    }
}
