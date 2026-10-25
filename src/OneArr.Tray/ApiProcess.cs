using System.Diagnostics;

namespace OneArr.Tray;

/// <summary>Supervises the OneArr.Api process: hidden, with output written to api.log and the config folder set.</summary>
internal sealed class ApiProcess
{
    private Process? _process;

    /// <summary>Raised only for unexpected exits. Stop() clears the process first, so deliberate stops do not raise it.</summary>
    public event Action<int>? UnexpectedExit;

    public bool IsRunning => _process is { HasExited: false };

    public void Start()
    {
        if (IsRunning) return;
        if (!File.Exists(Paths.ApiExe)) throw new FileNotFoundException("OneArr has not been built yet", Paths.ApiExe);
        Directory.CreateDirectory(Paths.Config);

        var psi = new ProcessStartInfo
        {
            FileName = Paths.ApiExe,
            WorkingDirectory = Paths.App,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["ONEARR_CONFIG_DIR"] = Paths.Config;
        psi.Environment["ONEARR_LISTEN_ADDRESS"] = "127.0.0.1";
        psi.Environment["ONEARR_PORT"] = "8780";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) Log.Write("api.log", e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log.Write("api.log", e.Data); };
        p.Exited += (_, _) =>
        {
            if (!ReferenceEquals(_process, p)) return; // deliberate stop or replaced process
            UnexpectedExit?.Invoke(p.ExitCode);
        };

        _process = p;
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Log.Write("tray.log", $"Started OneArr (pid {p.Id})");
    }

    /// <summary>Stops OneArr and its child processes. Safe to call when it is not running.</summary>
    public void Stop()
    {
        var p = _process;
        _process = null;
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            Log.Write("tray.log", "Stopped OneArr");
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill.
        }
        finally
        {
            p.Dispose();
        }
    }
}
