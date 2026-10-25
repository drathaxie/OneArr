using System.Diagnostics;
using OneArr.Updates;

namespace OneArr.Tray;

/// <summary>
/// Runs and stops OneArr.Api. Stopping is graceful first: the stop-request file asks the backend to shut down
/// cleanly, and only if it does not exit within the timeout is the child process tree killed. The kill is limited
/// to the process this tray started, never to any other process with the same name.
/// </summary>
internal sealed class ApiProcess : IApiController
{
    private static readonly HttpClient Health = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly InstallLayout _layout;
    private readonly int _port;
    private Process? _owned;

    public ApiProcess(InstallLayout layout, int port)
    {
        _layout = layout;
        _port = port;
    }

    public event Action<int>? UnexpectedExit;

    private volatile bool _healthy;

    /// <summary>True when this tray started the backend, or the last health probe succeeded. Never blocks the UI thread.</summary>
    public bool IsRunning => _owned is { HasExited: false } || _healthy;

    /// <summary>Refreshes the cached health flag. Called from a timer, off the UI thread.</summary>
    public async Task RefreshHealthAsync() => _healthy = await Probe() is not null;

    public string HealthUrl => $"http://127.0.0.1:{_port}/health";

    public void Start()
    {
        if (_owned is { HasExited: false }) return;
        if (!File.Exists(_layout.ApiExe)) throw new FileNotFoundException("OneArr is not installed", _layout.ApiExe);
        Directory.CreateDirectory(_layout.Config);

        var psi = new ProcessStartInfo
        {
            FileName = _layout.ApiExe,
            WorkingDirectory = _layout.App,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["ONEARR_CONFIG_DIR"] = _layout.Config;
        psi.Environment["ONEARR_LISTEN_ADDRESS"] = "127.0.0.1";
        psi.Environment["ONEARR_PORT"] = _port.ToString();
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["Logging__LogLevel__Default"] = "Warning";

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) Log.Write("api.log", e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log.Write("api.log", e.Data); };
        p.Exited += (_, _) =>
        {
            if (!ReferenceEquals(_owned, p)) return; // deliberate stop, or a replaced process
            UnexpectedExit?.Invoke(p.ExitCode);
        };

        _owned = p;
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Log.Write("tray.log", $"Started OneArr (pid {p.Id})");
    }

    /// <summary>Requests a clean stop, waits, and only then kills the tree of the process this tray started.</summary>
    public async Task<bool> StopGracefullyAsync(TimeSpan timeout, CancellationToken ct)
    {
        Directory.CreateDirectory(_layout.Config);
        File.WriteAllText(_layout.StopRequest, DateTime.UtcNow.ToString("O"));

        var owned = _owned;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (owned is null || owned.HasExited)
            {
                if (owned is not null) owned.Dispose();
                _owned = null;
                if (await Probe() is null) return true;
            }
            await Task.Delay(250, ct);
        }

        if (owned is { HasExited: false })
        {
            Log.Write("tray.log", "OneArr did not stop gracefully in time; ending the process this tray started");
            try
            {
                owned.Kill(entireProcessTree: true);
                owned.WaitForExit(5000);
            }
            catch (InvalidOperationException) { }
            owned.Dispose();
            _owned = null;
            return true;
        }
        // A backend this tray did not start is never killed; the caller treats this as "still running".
        return await Probe() is null;
    }

    /// <summary>Stops without waiting for a clean shutdown; used only when the tray itself is quitting.</summary>
    public void Stop()
    {
        var owned = _owned;
        _owned = null;
        File.WriteAllText(_layout.StopRequest, DateTime.UtcNow.ToString("O"));
        if (owned is null) return;
        try
        {
            if (!owned.WaitForExit(10_000) && !owned.HasExited)
                owned.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        finally
        {
            owned.Dispose();
        }
    }

    public async Task<bool> WaitForVersionAsync(string expectedVersion, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await Probe() == expectedVersion) return true;
            await Task.Delay(1000, ct);
        }
        return false;
    }

    /// <summary>The version reported by a healthy backend on the port, or null if none answers.</summary>
    public async Task<string?> Probe()
    {
        try
        {
            using var response = await Health.GetAsync(HealthUrl);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : "";
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
    }
}
