using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Files;
using Xunit;

namespace OneArr.Tests;

/// <summary>
/// Import containment: traversal, sibling prefixes and symlinks in source or destination must fail closed, and nothing
/// may be written outside the media root. Symlink tests return early on hosts that cannot create symlinks; such a case is
/// reported as unverified, not as passing.
/// </summary>
public class FilesystemContainmentTests
{
    private readonly SafeImporter _importer = new(TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static ImportRequest Req(TempTree t, string source, string relative = "The Matrix (1999)", bool dryRun = false) =>
        new(source, t.Media, relative, t.Torrents, dryRun, null);

    /// <summary>Creates a symlink or fails the test. A containment test that silently passes without its precondition proves nothing.</summary>
    private static void Link(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Fail($"Symbolic link could not be created on this host ({ex.GetType().Name}); the containment test cannot run");
        }
    }

    private static string Outside(TempTree t)
    {
        var dir = Path.Combine(t.Root, "outside");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task SourceInSiblingDirectoryWithSharedPrefixIsRefused()
    {
        using var t = new TempTree();
        var sibling = Path.Combine(t.Root, "torrents-evil");
        Directory.CreateDirectory(sibling);
        var src = Path.Combine(sibling, "a.mkv");
        File.WriteAllText(src, "x");

        var outcome = await _importer.ImportAsync(Req(t, src), Ct);

        Assert.False(outcome.Success);
        Assert.Contains("outside", outcome.Message);
        Assert.Empty(Directory.GetFiles(t.Media, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../media-evil")]
    [InlineData("../../escape")]
    [InlineData("x/../../media-evil")]
    public async Task DestinationFolderThatEscapesOrNamesASiblingIsRefused(string relative)
    {
        using var t = new TempTree();
        var src = t.WriteFile("torrents/movie/a.mkv", "x");

        var outcome = await _importer.ImportAsync(Req(t, src, relative), Ct);

        Assert.False(outcome.Success);
        Assert.DoesNotContain(Directory.GetFiles(t.Root, "a.mkv", SearchOption.AllDirectories), p => !p.StartsWith(t.Torrents, StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(t.Root, "media-evil")));
    }

    [Fact]
    public async Task SourceTraversalOutOfTorrentRootIsRefused()
    {
        using var t = new TempTree();
        t.WriteFile("media/secret.mkv", "s");
        var src = Path.Combine(t.Torrents, "..", "media", "secret.mkv");

        var outcome = await _importer.ImportAsync(Req(t, src), Ct);

        Assert.False(outcome.Success);
        Assert.Empty(Directory.GetDirectories(t.Media));
    }

    [Fact]
    public async Task SourceThroughSymlinkedDirectoryIsRefused()
    {
        using var t = new TempTree();
        var outside = Outside(t);
        File.WriteAllText(Path.Combine(outside, "secret.mkv"), "secret");
        Link(Path.Combine(t.Torrents, "link"), outside);

        var outcome = await _importer.ImportAsync(Req(t, Path.Combine(t.Torrents, "link", "secret.mkv")), Ct);

        Assert.False(outcome.Success);
        Assert.Contains("symbolic link", outcome.Message);
        Assert.Empty(Directory.GetFiles(t.Media, "*", SearchOption.AllDirectories));
        Assert.Equal("secret", File.ReadAllText(Path.Combine(outside, "secret.mkv")));
    }

    [Fact]
    public async Task SymlinkedFileAsSourceIsRefused()
    {
        using var t = new TempTree();
        var real = t.WriteFile("torrents/movie/real.mkv", "x");
        Link(Path.Combine(t.Torrents, "movie", "link.mkv"), real);

        var outcome = await _importer.ImportAsync(Req(t, Path.Combine(t.Torrents, "movie", "link.mkv")), Ct);

        Assert.False(outcome.Success);
        Assert.Empty(Directory.GetFiles(t.Media, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DestinationDirectoryThatIsASymlinkIsRefused()
    {
        using var t = new TempTree();
        var outside = Outside(t);
        var src = t.WriteFile("torrents/movie/a.mkv", "x");
        Link(Path.Combine(t.Media, "The Matrix (1999)"), outside);

        var outcome = await _importer.ImportAsync(Req(t, src), Ct);

        Assert.False(outcome.Success);
        Assert.Contains("symbolic link", outcome.Message);
        Assert.Empty(Directory.GetFiles(outside));
    }

    [Fact]
    public async Task PreexistingSymlinkAtDestinationFileIsNeverFollowed()
    {
        using var t = new TempTree();
        var outside = Outside(t);
        var target = Path.Combine(outside, "victim.mkv");
        File.WriteAllText(target, "keep me");
        var src = t.WriteFile("torrents/movie/a.mkv", "new");
        Directory.CreateDirectory(Path.Combine(t.Media, "The Matrix (1999)"));
        Link(Path.Combine(t.Media, "The Matrix (1999)", "a.mkv"), target);

        var outcome = await _importer.ImportAsync(Req(t, src), Ct);

        Assert.False(outcome.Success);
        Assert.Equal("keep me", File.ReadAllText(target));
    }

    [Fact]
    public async Task DanglingSymlinkAtDestinationCannotCreateFileOutsideRoot()
    {
        using var t = new TempTree();
        var outside = Outside(t);
        var missing = Path.Combine(outside, "created-by-import.mkv");
        var src = t.WriteFile("torrents/movie/a.mkv", "new");
        Directory.CreateDirectory(Path.Combine(t.Media, "The Matrix (1999)"));
        Link(Path.Combine(t.Media, "The Matrix (1999)", "a.mkv"), missing);

        var outcome = await _importer.ImportAsync(Req(t, src), Ct);

        Assert.False(outcome.Success);
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task DryRunStillRefusesSymlinkedDestination()
    {
        using var t = new TempTree();
        var outside = Outside(t);
        var src = t.WriteFile("torrents/movie/a.mkv", "x");
        Link(Path.Combine(t.Media, "The Matrix (1999)"), outside);

        var outcome = await _importer.ImportAsync(Req(t, src, dryRun: true), Ct);

        Assert.Equal(ImportAction.Failed, outcome.Action);
    }

    [Fact]
    public async Task FileStillBeingWrittenIsDeferredNotFailed()
    {
        using var t = new TempTree();
        var src = t.WriteFile("torrents/movie/a.mkv", "partial");

        // Default quiet period: a file written just now may still be downloading.
        var outcome = await new SafeImporter().ImportAsync(Req(t, src), Ct);

        Assert.Equal(ImportAction.Deferred, outcome.Action);
        Assert.False(outcome.Success);
        Assert.Empty(Directory.GetFiles(t.Media, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InterruptedImportLeavesNoTemporaryCopyBehind()
    {
        // A killed import leaves <name>.onearr-<32 hex>.tmp. The next import removes it once it is stale.
        using var t = new TempTree();
        var src = t.WriteFile("torrents/movie/a.mkv", "complete bytes");
        var folder = Path.Combine(t.Media, "The Matrix (1999)");
        Directory.CreateDirectory(folder);
        var stale = Path.Combine(folder, "a.mkv.onearr-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(stale, "half written");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromHours(1));
        var userFile = Path.Combine(folder, "notes.txt");
        File.WriteAllText(userFile, "mine");
        File.SetLastWriteTimeUtc(userFile, DateTime.UtcNow - TimeSpan.FromHours(1));
        var similarButNotOurs = Path.Combine(folder, "a.mkv.onearr-keep.tmp");
        File.WriteAllText(similarButNotOurs, "not ours");
        File.SetLastWriteTimeUtc(similarButNotOurs, DateTime.UtcNow - TimeSpan.FromHours(1));

        var outcome = await new SafeImporter(TimeSpan.Zero).ImportAsync(Req(t, src), Ct);

        Assert.True(outcome.Success, outcome.Message);
        Assert.False(File.Exists(stale), "a stale temp copy from an interrupted import must be removed");
        Assert.Equal("mine", File.ReadAllText(userFile));
        Assert.Equal("not ours", File.ReadAllText(similarButNotOurs));
    }

    [Fact]
    public async Task ExistingMediaInTheSameFolderIsNeverTouched()
    {
        using var t = new TempTree();
        var src = t.WriteFile("torrents/movie/a.mkv", "new release");
        var folder = Path.Combine(t.Media, "The Matrix (1999)");
        Directory.CreateDirectory(folder);
        var existing = Path.Combine(folder, "The.Matrix.1999.720p.mkv");
        File.WriteAllText(existing, "old release, keep");
        var before = File.GetLastWriteTimeUtc(existing);

        var outcome = await new SafeImporter(TimeSpan.Zero).ImportAsync(Req(t, src), Ct);

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal("old release, keep", File.ReadAllText(existing));
        Assert.Equal(before, File.GetLastWriteTimeUtc(existing));
        Assert.Equal(2, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public void RootItselfIsNotAContainedChild()
    {
        using var t = new TempTree();
        Assert.Throws<UnauthorizedAccessException>(() => SafeImporter.RequireNoLinksBelow(t.Torrents, t.Torrents));
    }

    [Fact]
    public void OrdinaryNestedDirectoriesPassTheLinkCheck()
    {
        // Positive control: the check must not reject normal trees, or every import would fail.
        using var t = new TempTree();
        var file = t.WriteFile("torrents/a/b/c/file.mkv", "x");

        SafeImporter.RequireNoLinksBelow(t.Torrents, file);
    }

    [Fact]
    public void PathCheckRejectsEmptyAndSiblingRoots()
    {
        using var t = new TempTree();
        var sibling = Path.Combine(t.Root, "torrents2", "x.mkv");

        Assert.Throws<UnauthorizedAccessException>(() => SafeImporter.CanonicalUnder(t.Torrents, sibling));
    }
}
