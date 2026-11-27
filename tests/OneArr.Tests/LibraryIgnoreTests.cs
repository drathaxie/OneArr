using OneArr.Domain;
using OneArr.Infrastructure.Library;
using Xunit;

namespace OneArr.Tests;

/// <summary>Extras and the ignore list: skipped by scans, never matched, never counted as unmatched; disk untouched.</summary>
public class LibraryIgnoreTests
{
    [Theory]
    [InlineData("Dune (2021)/Featurettes/Making Of.mkv", true)]
    [InlineData("Show/Season 01/Extras/Bloopers.mkv", true)]
    [InlineData("Dune (2021)/Dune (2021)-trailer.mkv", true)]
    [InlineData("Dune (2021)/Dune (2021).mkv", false)]
    [InlineData("Show/Specials/Show S00E01.mkv", false)] // season 0, not an extra
    [InlineData("Other/Some Movie (2001).mkv", false)]   // a top-level folder named Other may be a real collection
    public void Recognises_plex_style_extras(string relative, bool extra) => Assert.Equal(extra, LibraryExtras.IsExtra(relative));

    [Fact]
    public async Task Extras_and_ignored_files_are_skipped_and_not_unmatched()
    {
        using var w = new TestWorld();
        await w.ConfigureAsync();
        await w.AddMovieAsync("Dune", 2021);
        await w.ReconcileLibraryAsync(); // creates the roots
        var movies = Path.Combine(w.Dirs.Media, "movies");
        var extra = w.Dirs.WriteFile("media/movies/Dune (2021)/Featurettes/Making Of.mkv", "x");
        var junk = w.Dirs.WriteFile("media/movies/Random Home Video.mkv", "y");

        var before = await w.Library.PreviewAsync(default);
        Assert.Equal(1, before.Plan.UnmatchedCount);
        Assert.Equal(1, before.Plan.IgnoredCount);

        await using (var db = w.Db.CreateDbContext())
        {
            db.LibraryIgnores.Add(new LibraryIgnore { Path = junk, PathKey = LibraryPaths.Key(junk, OperatingSystem.IsWindows()), CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var after = await w.Library.PreviewAsync(default);

        Assert.Equal(0, after.Plan.UnmatchedCount);
        Assert.Equal(2, after.Plan.IgnoredCount);
        Assert.True(File.Exists(extra) && File.Exists(junk));
        Assert.True(Directory.Exists(movies));
    }
}
