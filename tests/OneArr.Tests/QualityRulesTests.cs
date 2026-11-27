using OneArr.Application;
using OneArr.Domain;
using Xunit;

namespace OneArr.Tests;

/// <summary>Propers and repacks, source rules, size limits, required/banned/preferred words, and what counts as an upgrade.</summary>
public class QualityRulesTests
{
    private const string Hash = "abcdef0123456789abcdef0123456789abcdef01";
    private readonly ReleaseRanker _ranker = new(new ReleaseParser());

    private static ReleaseCandidate C(string title, long sizeMb = 2000) =>
        new(title, "magnet:?xt=urn:btih:" + Hash, sizeMb * 1024 * 1024, 10, Hash, "t", []);

    private static ReleaseTarget T(QualityProfile p, int? currentRes = null, string? currentSource = null, int currentRevision = 1) =>
        new(MediaType.Series, "Show", 2020, 1, 1, p, currentRes, currentRes is not null, new HashSet<string>(), null, null, currentSource, currentRevision);

    private static QualityProfile P() => new() { MinResolution = 720, CutoffResolution = 1080, AllowWebSources = true };

    [Theory]
    [InlineData("Show.S01E01.1080p.WEB-DL.x264-GRP", 1)]
    [InlineData("Show.S01E01.PROPER.1080p.WEB-DL.x264-GRP", 2)]
    [InlineData("Show.S01E01.REPACK.1080p.WEB-DL.x264-GRP", 2)]
    [InlineData("Show.S01E01.REAL.PROPER.REPACK.1080p.WEB-DL.x264-GRP", 3)]
    [InlineData("[Group] Show - 05v2 [1080p].mkv", 2)]
    public void Revision_is_parsed(string title, int revision) => Assert.Equal(revision, new ReleaseParser().Parse(title).Revision);

    [Fact]
    public void A_proper_of_the_same_quality_replaces_the_original_even_at_the_cutoff()
    {
        var d = _ranker.Evaluate(C("Show.S01E01.PROPER.1080p.WEB-DL.x264-GRP"), T(P(), 1080, ReleaseParser.SourceWebDl));
        Assert.True(d.Accepted, string.Join("; ", d.Reasons));
    }

    [Fact]
    public void The_same_quality_again_is_not_an_upgrade()
    {
        var d = _ranker.Evaluate(C("Show.S01E01.1080p.WEB-DL.x264-OTHER"), T(P(), 1080, ReleaseParser.SourceWebDl));
        Assert.False(d.Accepted);
    }

    [Fact]
    public void A_better_source_at_the_same_resolution_is_an_upgrade_below_the_cutoff()
    {
        var p = P(); p.CutoffResolution = 2160;
        Assert.True(_ranker.Evaluate(C("Show.S01E01.1080p.BluRay.x264-GRP"), T(p, 1080, ReleaseParser.SourceHdtv)).Accepted);
        Assert.False(_ranker.Evaluate(C("Show.S01E01.1080p.HDTV.x264-GRP"), T(p, 1080, ReleaseParser.SourceBluRay)).Accepted);
    }

    [Fact]
    public void Allowed_sources_reject_others()
    {
        var p = P(); p.AllowedSources = "bluray, webdl";
        Assert.True(_ranker.Evaluate(C("Show.S01E01.1080p.WEB-DL.x264-GRP"), T(p)).Accepted);
        Assert.Contains(_ranker.Evaluate(C("Show.S01E01.1080p.HDTV.x264-GRP"), T(p)).Reasons, r => r.Contains("not allowed"));
    }

    [Fact]
    public void Size_limits_apply_to_single_episodes_but_not_packs()
    {
        var p = P(); p.MinSizeMb = 300; p.MaxSizeMb = 4000;
        Assert.Contains(_ranker.Evaluate(C("Show.S01E01.1080p.WEB-DL.x264-GRP", 100), T(p)).Reasons, r => r.Contains("below the profile minimum"));
        Assert.Contains(_ranker.Evaluate(C("Show.S01E01.1080p.WEB-DL.x264-GRP", 9000), T(p)).Reasons, r => r.Contains("above the profile maximum"));
        var pack = new ReleaseTarget(MediaType.Series, "Show", 2020, 1, null, p, null, false, new HashSet<string>());
        Assert.True(_ranker.Evaluate(C("Show.S01.1080p.WEB-DL.x264-GRP", 40000), pack).Accepted);
    }

    [Fact]
    public void Required_banned_and_preferred_words()
    {
        var p = P(); p.MustNotContain = "HDR"; p.PreferredWords = "x265";
        Assert.Contains(_ranker.Evaluate(C("Show.S01E01.1080p.WEB-DL.HDR.x265-GRP"), T(p)).Reasons, r => r.Contains("banned word"));
        // Whole words only: "HDRip" is not "HDR".
        Assert.DoesNotContain(_ranker.Evaluate(C("Show.S01E01.1080p.HDRip.x264-GRP"), T(p)).Reasons, r => r.Contains("banned word"));
        var plain = _ranker.Evaluate(C("Show.S01E01.1080p.WEB-DL.x264-GRP"), T(p));
        var preferred = _ranker.Evaluate(C("Show.S01E01.1080p.WEB-DL.x265-GRP"), T(p));
        Assert.True(preferred.Score > plain.Score);
        p.MustContain = "x265";
        Assert.False(_ranker.Evaluate(C("Show.S01E01.1080p.WEB-DL.x264-GRP"), T(p)).Accepted);
    }
}

public class UpgradeSearchTests
{
    private static async Task<TestWorld> WorldWithFileAsync(int resolution)
    {
        var w = new TestWorld();
        await w.ConfigureAsync();
        var id = await w.AddMovieAsync("The Matrix", 1999, hasFile: true);
        await using (var db = w.Db.CreateDbContext())
        {
            db.MediaFiles.Add(new MediaFile { Path = "/m/The Matrix.mkv", PathKey = "/m/the matrix.mkv", MediaItemId = id, Resolution = resolution, Source = "webdl", Status = MediaFileStatus.Present });
            await db.SaveChangesAsync();
        }
        w.Indexer.Body = TestWorld.Torznab(("The.Matrix.1999.1080p.BluRay.x264-GRP", "abcdef0123456789abcdef0123456789abcdef01", 50));
        return w;
    }

    private static async Task UpgradesOnAsync(TestWorld w)
    {
        await using var db = w.Db.CreateDbContext();
        foreach (var p in db.QualityProfiles) p.UpgradesAllowed = true;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Below_cutoff_file_is_upgraded_when_upgrades_and_a_recycle_bin_are_set()
    {
        using var w = await WorldWithFileAsync(720);
        await UpgradesOnAsync(w);
        await w.Settings.SetAsync(SettingKeys.RecycleBin, Path.Combine(w.Dirs.Root, "recycle"));
        await w.ReconcileLibraryAsync();
        var summary = await w.Search.RunOnceAsync(default);
        Assert.Equal(1, summary.Grabbed);
    }

    [Theory]
    [InlineData(false, true, 720)]  // upgrades off
    [InlineData(true, false, 720)]  // no recycle bin
    [InlineData(true, true, 1080)]  // cutoff met
    public async Task Upgrade_is_not_searched(bool upgrades, bool recycle, int resolution)
    {
        using var w = await WorldWithFileAsync(resolution);
        if (upgrades) await UpgradesOnAsync(w);
        if (recycle) await w.Settings.SetAsync(SettingKeys.RecycleBin, Path.Combine(w.Dirs.Root, "recycle"));
        await w.ReconcileLibraryAsync();
        await w.Search.RunOnceAsync(default);
        Assert.Empty(w.Indexer.Queries);
    }
}
