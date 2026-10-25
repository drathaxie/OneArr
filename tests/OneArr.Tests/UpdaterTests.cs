using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using OneArr.Updates;
using Xunit;

namespace OneArr.Tests;

/// <summary>Release discovery, artifact verification and the apply/rollback state machine. No network, no processes.</summary>
public class UpdaterTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static string Latest(string tag, params (string Name, string Url)[] assets) =>
        "{\"tag_name\":\"" + tag + "\",\"assets\":[" + string.Join(",", assets.Select(a => $"{{\"name\":\"{a.Name}\",\"browser_download_url\":\"{a.Url}\"}}")) + "]}";

    [Fact]
    public async Task LatestReleasePicksWindowsArtifactAndChecksum()
    {
        var zip = "OneArr-0.2.0-win-x64.zip";
        var body = Latest("v0.2.0",
            (zip, "https://github.com/drathaxie/OneArr/releases/download/v0.2.0/" + zip),
            (zip + ".sha256", "https://github.com/drathaxie/OneArr/releases/download/v0.2.0/" + zip + ".sha256"),
            ("source.tar.gz", "https://github.com/drathaxie/OneArr/archive/v0.2.0.tar.gz"));
        var client = new ReleaseClient(new HttpClient(new FakeResponses(body)));

        var info = await client.GetLatestAsync(Ct);

        Assert.NotNull(info);
        Assert.Equal(new Version(0, 2, 0), info!.Version);
        Assert.Equal(zip, info.ZipName);
    }

    [Fact]
    public async Task ReleaseWithoutArtifactIsNotOffered()
    {
        var client = new ReleaseClient(new HttpClient(new FakeResponses(Latest("v0.2.0", ("notes.md", "https://github.com/x")))));
        Assert.Null(await client.GetLatestAsync(Ct));
    }

    [Fact]
    public async Task NoReleasesYetIsNotAnError()
    {
        var client = new ReleaseClient(new HttpClient(new FakeResponses("{}", HttpStatusCode.NotFound)));
        Assert.Null(await client.GetLatestAsync(Ct));
    }

    [Theory]
    [InlineData("https://evil.example.com/OneArr.zip")]
    [InlineData("http://github.com/drathaxie/OneArr/releases/download/v0.2.0/OneArr.zip")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    public void UpdateUrlsMustBeHttpsGitHubHosts(string url)
    {
        Assert.Throws<InvalidDataException>(() => ReleaseClient.EnsureAllowedUrl(url));
    }

    [Fact]
    public void GitHubDownloadHostsAreAccepted()
    {
        ReleaseClient.EnsureAllowedUrl("https://objects.githubusercontent.com/github-production-release-asset/x");
        ReleaseClient.EnsureAllowedUrl("https://github.com/drathaxie/OneArr/releases/download/v0.2.0/a.zip");
    }

    [Theory]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("v1.10.3", "1.10.3")]
    public void ValidVersionsParse(string text, string expected) =>
        Assert.Equal(new Version(expected), ReleaseVersions.Parse(text));

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3-beta")]
    [InlineData("latest")]
    [InlineData("1.2.3.4")]
    [InlineData("")]
    public void InvalidVersionsAreRejected(string text) => Assert.Null(ReleaseVersions.Parse(text));

    [Fact]
    public async Task ChecksumFileMustListTheArtifact()
    {
        var sha = new string('a', 64);
        var client = new ReleaseClient(new HttpClient(new FakeResponses($"{sha}  OneArr-0.2.0-win-x64.zip\n")));
        Assert.Equal(sha, await client.ReadExpectedHashAsync("https://github.com/x.sha256", "OneArr-0.2.0-win-x64.zip", Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadExpectedHashAsync("https://github.com/x.sha256", "other.zip", Ct));
    }

    [Fact]
    public void ArtifactWithWrongChecksumIsRejected()
    {
        using var t = new Scratch();
        var file = t.Write("artifact.zip", "payload");

        Assert.Throws<InvalidDataException>(() => ArtifactVerifier.RequireHash(file, new string('0', 64)));
        ArtifactVerifier.RequireHash(file, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant());
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("sub/../../evil.txt")]
    [InlineData("/abs/evil.txt")]
    [InlineData("C:/evil.txt")]
    public void ZipSlipEntriesAreRefused(string entry)
    {
        using var t = new Scratch();
        var zip = t.MakeZip(("ok.txt", "ok"), (entry, "pwned"));

        Assert.Throws<InvalidDataException>(() => ArtifactVerifier.SafeExtract(zip, Path.Combine(t.Root, "out")));
        Assert.False(File.Exists(Path.Combine(t.Root, "evil.txt")));
    }

    [Fact]
    public void SymlinkEntriesAreRefused()
    {
        using var t = new Scratch();
        var zip = t.MakeZip(("app/OneArr.Api.exe", "x"));
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            var link = archive.CreateEntry("app/link");
            link.ExternalAttributes = 0xA1FF << 16; // Unix mode 0120777: symbolic link
            using var w = new StreamWriter(link.Open());
            w.Write("/etc/passwd");
        }

        Assert.Throws<InvalidDataException>(() => ArtifactVerifier.SafeExtract(zip, Path.Combine(t.Root, "out")));
    }

    [Fact]
    public void WellFormedArtifactExtracts()
    {
        using var t = new Scratch();
        var zip = t.MakeZip(("OneArr.Api.exe", "x"), ("wwwroot/index.html", "<html/>"), ("version.txt", "0.2.0"));

        ArtifactVerifier.SafeExtract(zip, Path.Combine(t.Root, "out"));

        Assert.True(File.Exists(Path.Combine(t.Root, "out", "wwwroot", "index.html")));
        Assert.Equal("0.2.0", File.ReadAllText(Path.Combine(t.Root, "out", "version.txt")));
    }

    [Fact]
    public async Task OversizedDownloadIsCutOff()
    {
        using var t = new Scratch();
        var client = new ReleaseClient(new HttpClient(new FakeResponses(new string('x', 5000))));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.DownloadAsync("https://github.com/a.zip", Path.Combine(t.Root, "a.zip"), maxBytes: 1000, Ct));
        Assert.False(File.Exists(Path.Combine(t.Root, "a.zip")) && new FileInfo(Path.Combine(t.Root, "a.zip")).Length > 1000);
    }

    [Fact]
    public async Task SuccessfulUpdateReplacesAppAndKeepsConfigUntouched()
    {
        using var t = new Scratch();
        var layout = t.Layout();
        t.InstallOld(layout);
        var configBefore = File.ReadAllText(layout.Database);
        var staged = t.Staged(layout, "0.2.0", "new build");
        var api = new FakeApi(healthy: true);

        var outcome = await new UpdateApplier(layout, api, TimeProvider.System, _ => { }).ApplyAsync(staged, "0.2.0", Ct);

        Assert.Equal(UpdateOutcome.Updated, outcome);
        Assert.Equal("0.2.0", File.ReadAllText(layout.VersionFile).Trim());
        Assert.Equal("1.0.0", File.ReadAllText(Path.Combine(layout.AppPrevious, "version.txt")).Trim());
        Assert.Equal(configBefore, File.ReadAllText(layout.Database));
        Assert.Single(Directory.GetFiles(layout.Backups, "onearr-*.db"));
        Assert.Equal(1, api.Starts);
    }

    [Fact]
    public async Task UnhealthyNewVersionRollsBackAppAndDatabase()
    {
        using var t = new Scratch();
        var layout = t.Layout();
        t.InstallOld(layout);
        var before = File.ReadAllText(layout.Database);
        var staged = t.Staged(layout, "0.2.0", "new build");
        // Simulate the new version writing to the database before it fails its health check.
        // Only the first start (the new version) migrates the database; the rollback's restart runs the old version.
        var api = new FakeApi(healthy: false, onStart: starts => { if (starts == 1) File.WriteAllText(layout.Database, "migrated by new version"); });

        var outcome = await new UpdateApplier(layout, api, TimeProvider.System, _ => { }).ApplyAsync(staged, "0.2.0", Ct);

        Assert.Equal(UpdateOutcome.RolledBack, outcome);
        Assert.Equal("1.0.0", File.ReadAllText(layout.VersionFile).Trim());
        Assert.Equal(before, File.ReadAllText(layout.Database));
        Assert.True(Directory.Exists(layout.App + ".failed"));
    }

    [Fact]
    public async Task ApiThatWillNotStopBlocksTheUpdateWithoutTouchingFiles()
    {
        using var t = new Scratch();
        var layout = t.Layout();
        t.InstallOld(layout);
        var staged = t.Staged(layout, "0.2.0", "new build");
        var api = new FakeApi(healthy: true, stops: false);

        await Assert.ThrowsAsync<IOException>(() => new UpdateApplier(layout, api, TimeProvider.System, _ => { }).ApplyAsync(staged, "0.2.0", Ct));

        Assert.Equal("1.0.0", File.ReadAllText(layout.VersionFile).Trim());
        Assert.False(Directory.Exists(layout.AppPrevious));
    }

    [Fact]
    public async Task StagedBuildThatDoesNotMatchTheReleaseIsRejectedBeforeAnyChange()
    {
        using var t = new Scratch();
        var layout = t.Layout();
        t.InstallOld(layout);
        var staged = t.Staged(layout, "0.9.9", "wrong build");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new UpdateApplier(layout, new FakeApi(true), TimeProvider.System, _ => { }).ApplyAsync(staged, "0.2.0", Ct));
        Assert.Equal("1.0.0", File.ReadAllText(layout.VersionFile).Trim());
    }

    [Fact]
    public async Task BackupsAreBoundedAndOnlyOurFilesArePruned()
    {
        using var t = new Scratch();
        var layout = t.Layout();
        t.InstallOld(layout);
        Directory.CreateDirectory(layout.Backups);
        for (var i = 0; i < 5; i++)
            File.WriteAllText(Path.Combine(layout.Backups, $"onearr-old-{i}.db"), "old");
        File.WriteAllText(Path.Combine(layout.Backups, "notes.txt"), "keep me");
        var staged = t.Staged(layout, "0.2.0", "new build");

        await new UpdateApplier(layout, new FakeApi(true), TimeProvider.System, _ => { }).ApplyAsync(staged, "0.2.0", Ct);

        Assert.Equal(UpdateApplier.BackupsToKeep, Directory.GetFiles(layout.Backups, "onearr-*.db").Length);
        Assert.True(File.Exists(Path.Combine(layout.Backups, "notes.txt")));
    }

    [Fact]
    public async Task ManualRollbackRestoresPreviousAppAndDatabaseBackup()
    {
        using var t = new Scratch();
        var layout = t.Layout();
        t.InstallOld(layout);
        var before = File.ReadAllText(layout.Database);
        var staged = t.Staged(layout, "0.2.0", "new build");
        await new UpdateApplier(layout, new FakeApi(true), TimeProvider.System, _ => { }).ApplyAsync(staged, "0.2.0", Ct);
        File.WriteAllText(layout.Database, "changed after the update");

        await new UpdateApplier(layout, new FakeApi(true), TimeProvider.System, _ => { }).RollBackAsync(Ct);

        Assert.Equal("1.0.0", File.ReadAllText(layout.VersionFile).Trim());
        Assert.Equal(before, File.ReadAllText(layout.Database));
    }

    /// <summary>Scratch install tree in the temp directory. Deleted on dispose.</summary>
    private sealed class Scratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "onearr-upd-" + Guid.NewGuid().ToString("N"));
        public Scratch() => Directory.CreateDirectory(Root);
        public InstallLayout Layout() => new(Path.Combine(Root, "install"));
        public string Write(string name, string text)
        {
            var p = Path.Combine(Root, name);
            File.WriteAllText(p, text);
            return p;
        }
        public string MakeZip(params (string Name, string Text)[] entries)
        {
            var path = Path.Combine(Root, "artifact-" + Guid.NewGuid().ToString("N")[..6] + ".zip");
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (name, text) in entries)
            {
                var e = archive.CreateEntry(name);
                using var w = new StreamWriter(e.Open());
                w.Write(text);
            }
            return path;
        }
        public void InstallOld(InstallLayout layout)
        {
            Directory.CreateDirectory(layout.App);
            File.WriteAllText(layout.ApiExe, "old exe");
            File.WriteAllText(layout.VersionFile, "1.0.0");
            Directory.CreateDirectory(Path.Combine(layout.App, "wwwroot"));
            File.WriteAllText(Path.Combine(layout.App, "wwwroot", "index.html"), "old ui");
            Directory.CreateDirectory(layout.Config);
            File.WriteAllText(layout.Database, "database v1 data");
            File.WriteAllText(Path.Combine(layout.Config, "secret.key"), "keep");
        }
        public string Staged(InstallLayout layout, string version, string label)
        {
            var dir = Path.Combine(layout.Root, "downloads", "staged-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(Path.Combine(dir, "wwwroot"));
            File.WriteAllText(Path.Combine(dir, "OneArr.Api.exe"), label);
            File.WriteAllText(Path.Combine(dir, "wwwroot", "index.html"), "new ui");
            File.WriteAllText(Path.Combine(dir, "version.txt"), version);
            return dir;
        }
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FakeApi : IApiController
    {
        private readonly bool _healthy, _stops;
        private readonly Action<int>? _onStart;
        public int Starts { get; private set; }

        public FakeApi(bool healthy, bool stops = true, Action<int>? onStart = null)
        {
            _healthy = healthy;
            _stops = stops;
            _onStart = onStart;
        }

        public Task<bool> StopGracefullyAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(_stops);
        public void Start()
        {
            Starts++;
            _onStart?.Invoke(Starts);
        }
        public Task<bool> WaitForVersionAsync(string expectedVersion, TimeSpan timeout, CancellationToken ct) => Task.FromResult(_healthy);
    }

    /// <summary>Returns a fixed body for every request, so no test reaches the network.</summary>
    private sealed class FakeResponses : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;
        public FakeResponses(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
    }
}
