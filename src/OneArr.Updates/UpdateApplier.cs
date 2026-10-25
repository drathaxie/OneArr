
namespace OneArr.Updates;

/// <summary>Controls the running OneArr.Api process. Implemented by the tray app and by test doubles.</summary>
public interface IApiController
{
    /// <summary>Asks the process to stop through the stop-request file, then waits. Returns false if it did not exit in time.</summary>
    Task<bool> StopGracefullyAsync(TimeSpan timeout, CancellationToken ct);
    void Start();
    /// <summary>True once /health answers with the expected version.</summary>
    Task<bool> WaitForVersionAsync(string expectedVersion, TimeSpan timeout, CancellationToken ct);
}

public enum UpdateOutcome { Updated, RolledBack }

/// <summary>
/// Replaces the installed application with a verified staged build. Ordering guarantees:
/// 1. the staged build is checked before anything running is touched;
/// 2. the API is stopped gracefully, then the database is copied to backups before any file moves;
/// 3. if the new version does not become healthy, the old app and the database backup are restored;
/// 4. config and backups are never deleted by an update. Old backups beyond the retention limit are pruned.
/// </summary>
public sealed class UpdateApplier
{
    public const int BackupsToKeep = 3;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(60);

    private readonly InstallLayout _layout;
    private readonly IApiController _api;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;

    public UpdateApplier(InstallLayout layout, IApiController api, TimeProvider time, Action<string> log)
    {
        _layout = layout;
        _api = api;
        _time = time;
        _log = log;
    }

    public async Task<UpdateOutcome> ApplyAsync(string stagedApp, string newVersion, CancellationToken ct)
    {
        ValidateStaged(stagedApp, newVersion);
        var oldVersion = _layout.InstalledVersion()?.ToString() ?? "none";

        if (!await _api.StopGracefullyAsync(StopTimeout, ct))
            throw new IOException("OneArr did not stop within the timeout; the update was not applied");

        var backup = BackupDatabase(oldVersion);
        _log($"Backed up database to {backup ?? "(no database yet)"}");

        if (Directory.Exists(_layout.AppPrevious)) Directory.Delete(_layout.AppPrevious, recursive: true);
        if (Directory.Exists(_layout.App)) Directory.Move(_layout.App, _layout.AppPrevious);
        Directory.Move(stagedApp, _layout.App);
        _log($"Activated {newVersion}");

        _api.Start();
        if (await _api.WaitForVersionAsync(newVersion, HealthTimeout, ct))
        {
            PruneBackups();
            return UpdateOutcome.Updated;
        }

        _log($"{newVersion} did not become healthy; rolling back to {oldVersion}");
        await _api.StopGracefullyAsync(StopTimeout, ct);
        RestoreFiles(backup);
        Swap(_layout.App, _layout.AppPrevious, _layout.App + ".failed");
        _api.Start();
        return UpdateOutcome.RolledBack;
    }

    /// <summary>Manual rollback to the previous app folder, together with the database backup taken before the last update.</summary>
    public async Task RollBackAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_layout.AppPrevious)) throw new InvalidOperationException("No previous version is available");
        if (!await _api.StopGracefullyAsync(StopTimeout, ct))
            throw new IOException("OneArr did not stop within the timeout; rollback was not applied");

        var marker = File.Exists(_layout.LastBackupMarker) ? File.ReadAllText(_layout.LastBackupMarker).Trim() : null;
        if (marker is not null) RestoreFiles(marker);
        Swap(_layout.App, _layout.AppPrevious, _layout.App + ".rolledback");
        _api.Start();
    }

    private void ValidateStaged(string stagedApp, string newVersion)
    {
        if (!File.Exists(Path.Combine(stagedApp, "OneArr.Api.exe"))) throw new InvalidDataException("Staged build has no OneArr.Api.exe");
        if (!File.Exists(Path.Combine(stagedApp, "wwwroot", "index.html"))) throw new InvalidDataException("Staged build has no web UI");
        var version = File.Exists(Path.Combine(stagedApp, "version.txt"))
            ? File.ReadAllText(Path.Combine(stagedApp, "version.txt")).Trim()
            : null;
        if (version != newVersion) throw new InvalidDataException($"Staged build reports version '{version}', expected '{newVersion}'");
    }

    /// <summary>Copies the database (and WAL files) into backups. Returns the main database backup path, or null if none exists yet.</summary>
    private string? BackupDatabase(string oldVersion)
    {
        if (!File.Exists(_layout.Database)) return null;
        Directory.CreateDirectory(_layout.Backups);
        var stamp = _time.GetUtcNow().ToString("yyyyMMdd-HHmmss");
        var target = Path.Combine(_layout.Backups, $"onearr-{oldVersion}-{stamp}.db");
        File.Copy(_layout.Database, target, overwrite: false);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var side = _layout.Database + suffix;
            if (File.Exists(side)) File.Copy(side, target + suffix, overwrite: false);
        }
        File.WriteAllText(_layout.LastBackupMarker, target);
        return target;
    }

    private void RestoreFiles(string? backup)
    {
        if (backup is null || !File.Exists(backup)) return;
        File.Copy(backup, _layout.Database, overwrite: true);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var side = backup + suffix;
            if (File.Exists(side)) File.Copy(side, _layout.Database + suffix, overwrite: true);
        }
    }

    private static void Swap(string live, string previous, string failedName)
    {
        // Keep the failed build for inspection, one slot only, then put the previous build back in place.
        if (Directory.Exists(failedName)) Directory.Delete(failedName, recursive: true);
        if (Directory.Exists(live)) Directory.Move(live, failedName);
        if (Directory.Exists(previous)) Directory.Move(previous, live);
    }

    /// <summary>Keeps the newest few database backups. Only files this class created (onearr-*.db) are touched.</summary>
    private void PruneBackups()
    {
        if (!Directory.Exists(_layout.Backups)) return;
        var files = Directory.GetFiles(_layout.Backups, "onearr-*.db")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Skip(BackupsToKeep);
        foreach (var old in files)
        {
            File.Delete(old);
            foreach (var suffix in new[] { "-wal", "-shm" }) File.Delete(old + suffix);
        }
    }
}
