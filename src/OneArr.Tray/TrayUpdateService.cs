using OneArr.Updates;

namespace OneArr.Tray;

/// <summary>
/// Check, download, verify, extract and apply a release. Nothing is compiled on the user's machine. The download is
/// hashed against the published checksum before it is extracted, and the extracted build is applied only through
/// UpdateApplier, which keeps the database backup and rollback guarantees.
/// </summary>
internal sealed class TrayUpdateService
{
    private readonly InstallLayout _layout;
    private readonly ApiProcess _api;
    private readonly ReleaseClient _releases;

    public TrayUpdateService(InstallLayout layout, ApiProcess api, ReleaseClient releases)
    {
        _layout = layout;
        _api = api;
        _releases = releases;
    }

    public bool HasPreviousVersion => Directory.Exists(_layout.AppPrevious);

    /// <returns>A newer release than the installed one, or null if up to date (or no release exists yet).</returns>
    public async Task<ReleaseInfo?> CheckAsync(CancellationToken ct)
    {
        var latest = await _releases.GetLatestAsync(ct);
        if (latest is null) return null;
        var installed = _layout.InstalledVersion();
        return installed is null || latest.Version > installed ? latest : null;
    }

    public async Task<UpdateOutcome> InstallAsync(ReleaseInfo release, Action<string> status, CancellationToken ct)
    {
        Directory.CreateDirectory(_layout.Downloads);
        var zipName = Path.GetFileName(release.ZipName);
        var zip = Path.Combine(_layout.Downloads, zipName);
        var staged = Path.Combine(_layout.Downloads, "staged-" + release.Version.ToString(3));
        try
        {
            status($"Downloading OneArr {release.Version.ToString(3)}…");
            var expected = await _releases.ReadExpectedHashAsync(release.ShaUrl, zipName, ct);
            await _releases.DownloadAsync(release.ZipUrl, zip, ArtifactVerifier.MaxArtifactBytes, ct);
            ArtifactVerifier.RequireHash(zip, expected);

            status("Verifying and unpacking…");
            if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
            ArtifactVerifier.SafeExtract(zip, staged);

            var outcome = await new UpdateApplier(_layout, _api, TimeProvider.System, m => Log.Write("tray.log", m))
                .ApplyAsync(Path.Combine(staged, "app"), release.Version.ToString(3), ct);
            return outcome;
        }
        finally
        {
            // The downloaded artifact is only needed for this attempt. The config folder is never touched here.
            TryDelete(zip);
            if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
        }
    }

    public async Task RollBackAsync(CancellationToken ct) =>
        await new UpdateApplier(_layout, _api, TimeProvider.System, m => Log.Write("tray.log", m)).RollBackAsync(ct);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}
