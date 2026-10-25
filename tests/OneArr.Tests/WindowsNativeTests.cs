using System.Diagnostics;
using System.Runtime.Versioning;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Files;
using Xunit;

namespace OneArr.Tests;

/// <summary>
/// Windows filesystem regression tests: NTFS hardlinks, junctions, case-insensitive paths and shared reads.
/// Tagged Platform=Windows. Linux runs exclude them with --filter "Platform!=Windows"; the Windows CI job runs them with
/// --filter "Platform=Windows" and fails if none run. Each test fails rather than returning early when a precondition is missing.
/// </summary>
[Trait("Platform", "Windows")]
[SupportedOSPlatform("windows")]
public class WindowsNativeTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static ImportRequest Req(TempTree t, string source, string relative = "The Matrix (1999)") =>
        new(source, t.Media, relative, t.Torrents, false, null);

    /// <summary>Creates a directory junction, which does not need administrator rights. Fails the test if it cannot be made.</summary>
    private static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0 && Directory.Exists(link), $"Junction could not be created: {output}");
    }

    private static void RequireWindows()
    {
        // Guard against running this class on Linux by mistake: its assertions depend on NTFS behaviour.
        Assert.True(OperatingSystem.IsWindows(), "Platform=Windows tests must run on Windows");
    }

    [Fact]
    public async Task SameVolumeImportCreatesRealHardlink()
    {
        RequireWindows();
        using var t = new TempTree();
        var src = t.WriteFile("torrents/movie/The.Matrix.1999.mkv", "video");

        var outcome = await new SafeImporter(TimeSpan.Zero).ImportAsync(Req(t, src), Ct);

        Assert.Equal(ImportAction.Hardlink, outcome.Action);
        Assert.Equal("video", File.ReadAllText(outcome.DestinationPath));
        Assert.Equal(2u, WindowsNative.LinkCount(src));
        Assert.True(File.Exists(src), "torrent source must survive the import");
    }

    [Fact]
    public void HardlinkAcrossVolumesIsRefused()
    {
        RequireWindows();
        using var t = new TempTree();
        var src = t.WriteFile("torrents/a.mkv", "x");
        var otherVolume = @"D:\";
        Assert.True(Directory.Exists(otherVolume), "This test needs a second volume (D:) on the runner");
        var dest = Path.Combine(otherVolume, "onearr-test-" + Guid.NewGuid().ToString("N"), "a.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        try
        {
            var linked = SafeImporterHardlinkProbe(src, dest, out var reason);

            Assert.False(linked);
            Assert.Contains("different volumes", reason);
            Assert.False(File.Exists(dest));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dest)!, recursive: true);
        }
    }

    private static bool SafeImporterHardlinkProbe(string src, string dest, out string reason) =>
        WindowsNative.TryCreateHardLink(src, dest, out reason);

    [Fact]
    public async Task JunctionInTorrentTreeIsRefusedAndNothingIsWritten()
    {
        RequireWindows();
        using var t = new TempTree();
        var outside = Path.Combine(t.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.mkv"), "secret");
        Junction(Path.Combine(t.Torrents, "jump"), outside);

        var outcome = await new SafeImporter(TimeSpan.Zero).ImportAsync(Req(t, Path.Combine(t.Torrents, "jump", "secret.mkv")), Ct);

        Assert.Equal(ImportAction.Failed, outcome.Action);
        Assert.Empty(Directory.GetFiles(t.Media, "*", SearchOption.AllDirectories));
        Assert.Equal("secret", File.ReadAllText(Path.Combine(outside, "secret.mkv")));
    }

    [Fact]
    public async Task JunctionAsDestinationFolderIsRefused()
    {
        RequireWindows();
        using var t = new TempTree();
        var outside = Path.Combine(t.Root, "outside");
        Directory.CreateDirectory(outside);
        Junction(Path.Combine(t.Media, "The Matrix (1999)"), outside);
        var src = t.WriteFile("torrents/a.mkv", "x");

        var outcome = await new SafeImporter(TimeSpan.Zero).ImportAsync(Req(t, src), Ct);

        Assert.Equal(ImportAction.Failed, outcome.Action);
        Assert.Empty(Directory.GetFiles(outside));
    }

    [Fact]
    public void JunctionAsRootIsRefused()
    {
        RequireWindows();
        using var t = new TempTree();
        var real = Path.Combine(t.Root, "real-media");
        Directory.CreateDirectory(Path.Combine(real, "movies"));
        var rootJunction = Path.Combine(t.Root, "media-link");
        Junction(rootJunction, real);

        Assert.Throws<UnauthorizedAccessException>(() =>
            SafeImporter.RequireNoLinksBelow(rootJunction, Path.Combine(rootJunction, "movies")));
    }

    [Fact]
    public void FinalPathReturnsCanonicalCasing()
    {
        RequireWindows();
        using var t = new TempTree();
        var actual = Path.Combine(t.Root, "CasedFolder");
        Directory.CreateDirectory(actual);

        var resolved = WindowsNative.FinalPath(Path.Combine(t.Root, "casedfolder"));

        Assert.NotNull(resolved);
        Assert.Equal(actual, resolved, ignoreCase: false);
    }

    [Fact]
    public async Task CaseVariantOfExistingIdenticalFileIsRecognisedNotDuplicated()
    {
        RequireWindows();
        using var t = new TempTree();
        var src = t.WriteFile("torrents/movie/the.matrix.1999.mkv", "same bytes");
        var existingFolder = Path.Combine(t.Media, "THE MATRIX (1999)");
        Directory.CreateDirectory(existingFolder);
        File.WriteAllText(Path.Combine(existingFolder, "THE.MATRIX.1999.MKV"), "same bytes");

        var outcome = await new SafeImporter(TimeSpan.Zero).ImportAsync(Req(t, src, "The Matrix (1999)"), Ct);

        Assert.Equal(ImportAction.Skipped, outcome.Action);
        Assert.Single(Directory.GetFiles(existingFolder));
    }

    [Fact]
    public async Task CaseVariantOfExistingDifferentFileIsNeverOverwritten()
    {
        RequireWindows();
        using var t = new TempTree();
        var src = t.WriteFile("torrents/movie/the.matrix.1999.mkv", "new bytes");
        var existingFolder = Path.Combine(t.Media, "THE MATRIX (1999)");
        Directory.CreateDirectory(existingFolder);
        File.WriteAllText(Path.Combine(existingFolder, "THE.MATRIX.1999.MKV"), "user's own copy");

        var outcome = await new SafeImporter(TimeSpan.Zero).ImportAsync(Req(t, src, "The Matrix (1999)"), Ct);

        Assert.Equal(ImportAction.Failed, outcome.Action);
        Assert.Equal("user's own copy", File.ReadAllText(Path.Combine(existingFolder, "THE.MATRIX.1999.MKV")));
    }

    [Fact]
    public async Task SeedingClientHoldingTheFileOpenDoesNotBlockImport()
    {
        RequireWindows();
        using var t = new TempTree();
        var src = t.WriteFile("torrents/movie/a.mkv", "seeded bytes");
        // A seeding client keeps the file open for reading and shares it for read, write and delete.
        using var seeder = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var outcome = await new SafeImporter(TimeSpan.Zero).ImportAsync(Req(t, src), Ct);

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal("seeded bytes", File.ReadAllText(outcome.DestinationPath));
        Assert.True(File.Exists(src));
    }
}
