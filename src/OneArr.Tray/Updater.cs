namespace OneArr.Tray;

/// <summary>
/// Pulls main, builds and tests it, publishes to a staging folder, and only then swaps it in. A failed build or test
/// leaves the running version untouched. The previous build is kept in app.previous for rollback.
/// </summary>
internal sealed class Updater
{
    private const string TrayLog = "tray.log";
    private const string BuildLog = "build.log";

    /// <returns>True if a new version was deployed; false if already current.</returns>
    public async Task<bool> UpdateAsync(ApiProcess api, Action<string> status, CancellationToken ct)
    {
        Directory.CreateDirectory(Paths.Root);

        if (!Directory.Exists(Path.Combine(Paths.Repo, ".git")))
        {
            status("Downloading OneArr…");
            await Shell.RunCheckedAsync("git", ["clone", "--branch", Paths.Branch, Paths.RepoUrl, Paths.Repo], Paths.Root, TrayLog, ct, "git clone failed");
        }

        await Shell.RunCheckedAsync("git", ["-C", Paths.Repo, "fetch", "--quiet", "origin", Paths.Branch], Paths.Root, TrayLog, ct, "git fetch failed");
        var local = await Shell.CaptureAsync("git", ["-C", Paths.Repo, "rev-parse", "HEAD"], Paths.Root, ct);
        var remote = await Shell.CaptureAsync("git", ["-C", Paths.Repo, "rev-parse", $"origin/{Paths.Branch}"], Paths.Root, ct);

        var needsBuild = local != remote || !File.Exists(Paths.ApiExe);
        if (!needsBuild)
        {
            if (!api.IsRunning) api.Start();
            return false;
        }

        if (local != remote)
        {
            status("Updating OneArr…");
            await Shell.RunCheckedAsync("git", ["-C", Paths.Repo, "merge", "--ff-only", $"origin/{Paths.Branch}"], Paths.Repo, TrayLog, ct,
                "Fast-forward failed; the repo folder has local changes");
        }

        var web = Path.Combine(Paths.Repo, "web");
        status("Building OneArr…");
        await Shell.RunCheckedAsync("npm", ["ci", "--no-audit", "--no-fund"], web, BuildLog, ct, "npm ci failed");
        await Shell.RunCheckedAsync("npm", ["run", "build"], web, BuildLog, ct, "Frontend build failed");

        status("Testing OneArr…");
        await Shell.RunCheckedAsync("dotnet", ["test", Path.Combine(Paths.Repo, "OneArr.slnx"), "-c", "Release"], Paths.Repo, BuildLog, ct,
            "Tests failed; keeping the current version running");

        var staging = Paths.App + ".staging";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        status("Publishing OneArr…");
        await Shell.RunCheckedAsync("dotnet",
            ["publish", Path.Combine(Paths.Repo, "src", "OneArr.Api", "OneArr.Api.csproj"), "-c", "Release", "-o", staging],
            Paths.Repo, BuildLog, ct, "Publish failed");

        // Swap only after everything above succeeded. Config and the database live outside the app folder.
        api.Stop();
        if (Directory.Exists(Paths.App + ".previous")) Directory.Delete(Paths.App + ".previous", recursive: true);
        if (Directory.Exists(Paths.App)) Directory.Move(Paths.App, Paths.App + ".previous");
        Directory.Move(staging, Paths.App);
        Log.Write(TrayLog, $"Deployed {remote[..7]}");

        api.Start();
        return true;
    }
}
