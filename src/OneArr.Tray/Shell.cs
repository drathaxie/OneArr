using System.Collections.Concurrent;
using System.Diagnostics;

namespace OneArr.Tray;

/// <summary>Runs git, npm and dotnet with output captured to a log file. No shell is involved except for npm.</summary>
internal static class Shell
{
    public static async Task<int> RunAsync(string tool, IReadOnlyList<string> args, string workDir, string logFile, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // npm is a .cmd shim on Windows and cannot be started directly without cmd.exe.
        if (tool == "npm")
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("npm");
        }
        else
        {
            psi.FileName = tool;
        }
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) Log.Write(logFile, e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log.Write(logFile, e.Data); };
        if (!p.Start()) throw new InvalidOperationException($"Could not start {tool}");
        Log.Write(logFile, $"> {tool} {string.Join(' ', args)}");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }

    /// <summary>Runs a command and throws with a readable message if it fails.</summary>
    public static async Task RunCheckedAsync(string tool, IReadOnlyList<string> args, string workDir, string logFile, CancellationToken ct, string failure)
    {
        var code = await RunAsync(tool, args, workDir, logFile, ct);
        if (code != 0) throw new InvalidOperationException($"{failure} (exit code {code}). See {logFile}");
    }

    /// <summary>Runs a command and returns its first line of output, e.g. a git commit hash.</summary>
    public static async Task<string> CaptureAsync(string tool, IReadOnlyList<string> args, string workDir, CancellationToken ct)
    {
        var lines = new ConcurrentQueue<string>();
        var psi = new ProcessStartInfo(tool) { WorkingDirectory = workDir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lines.Enqueue(e.Data); };
        if (!p.Start()) throw new InvalidOperationException($"Could not start {tool}");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"{tool} {args[0]} failed (exit code {p.ExitCode})");
        return lines.TryDequeue(out var first) ? first : "";
    }
}
