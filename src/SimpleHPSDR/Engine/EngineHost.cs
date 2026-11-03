using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SimpleHPSDR.Engine;

/// <summary>
/// Starts station-engine as a child process on a free loopback port with
/// native (Core Audio / WASAPI / ALSA) output, so receive audio and the mic
/// never pass through this UI. The engine only supports parent supervision in
/// receive-only mode, so we record its PID and clear any orphan on next start.
/// </summary>
public sealed class EngineHost : IDisposable
{
    private Process? _process;
    private StreamWriter? _log;

    public int Port { get; private set; }
    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");
    public bool HasExited => _process is null || _process.HasExited;

    /// <summary>The engine's total CPU time so far (zero if not running).</summary>
    public TimeSpan CpuTime
    {
        get
        {
            try
            {
                if (_process is not { HasExited: false } p) return TimeSpan.Zero;
                p.Refresh();
                return p.TotalProcessorTime;
            }
            catch (InvalidOperationException) { return TimeSpan.Zero; }
        }
    }

    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                    Environment.SpecialFolderOption.Create),
                "SimpleHPSDR");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string LogPath => Path.Combine(DataDir, "engine.log");
    private static string PidPath => Path.Combine(DataDir, "engine.pid");

    /// <summary>Find the engine: env override, beside the app, in a macOS
    /// bundle's Resources, or (development) artifacts/engine/&lt;rid&gt; above us.</summary>
    public static string? FindEngine()
    {
        var exe = OperatingSystem.IsWindows() ? "StationEngine.exe" : "StationEngine";
        var env = Environment.GetEnvironmentVariable("SIMPLEHPSDR_ENGINE");
        if (!string.IsNullOrEmpty(env))
            return File.Exists(env) ? env : Path.Combine(env, exe);

        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "engine", exe),
            Path.Combine(baseDir, "..", "Resources", "engine", exe),
        ];
        foreach (var c in candidates)
            if (File.Exists(c)) return Path.GetFullPath(c);

        var rid = RuntimeInformation.RuntimeIdentifier;
        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            var dev = Path.Combine(dir.FullName, "artifacts", "engine", rid, exe);
            if (File.Exists(dev)) return dev;
        }
        return null;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var exe = FindEngine() ?? throw new FileNotFoundException(
            "StationEngine not found. Run scripts/build-engine.sh, or set SIMPLEHPSDR_ENGINE.");

        KillOrphan();
        Port = FreePort();
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
                 {
                     "--port", Port.ToString(),
                     "--native-audio-output", "true",
                 })
            psi.ArgumentList.Add(a);

        // Our own settings database, separate from Zeus's.
        psi.Environment["ZEUS_PREFS_PATH"] = Path.Combine(DataDir, "prefs.db");
        psi.Environment["Logging__LogLevel__Microsoft.AspNetCore"] = "Warning";

        _log = new StreamWriter(LogPath, append: false) { AutoFlush = true };
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => WriteLog(e.Data);
        _process.ErrorDataReceived += (_, e) => WriteLog(e.Data);
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        File.WriteAllText(PidPath, _process.Id.ToString());

        using var http = new HttpClient { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_process.HasExited)
                throw new InvalidOperationException(
                    $"StationEngine exited with code {_process.ExitCode}; see {LogPath}");
            try
            {
                using var r = await http.GetAsync("api/station/version", ct);
                if (r.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(250, ct);
        }
        throw new TimeoutException($"StationEngine did not answer within 30 s; see {LogPath}");
    }

    private void WriteLog(string? line)
    {
        if (line is null) return;
        lock (this) _log?.WriteLine(line);
    }

    // An engine left running by a crashed UI still owns the radio and audio.
    private static void KillOrphan()
    {
        try
        {
            if (!File.Exists(PidPath) || !int.TryParse(File.ReadAllText(PidPath), out var pid)) return;
            using var p = Process.GetProcessById(pid);
            if (p.ProcessName.StartsWith("StationEngine", StringComparison.OrdinalIgnoreCase))
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
        }
        catch (ArgumentException) { }          // not running
        catch (InvalidOperationException) { }
        finally { File.Delete(PidPath); }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch (InvalidOperationException) { }
        _process?.Dispose();
        _process = null;
        try { File.Delete(PidPath); } catch (IOException) { }
        lock (this)
        {
            _log?.Dispose();
            _log = null;
        }
    }
}
