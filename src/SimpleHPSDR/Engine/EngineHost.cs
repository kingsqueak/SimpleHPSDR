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

    private bool _running;

    /// <summary>The engine stopped by itself, after starting. Its exit code.</summary>
    public event Action<int>? Stopped;

    /// <summary>Play receive audio on the computer. Off when it goes to the
    /// radio's speaker jack instead; the engine only reads this at startup.</summary>
    public bool ComputerAudio { get; set; } = true;

    /// <summary>This computer is inside an ANAN-G2: the engine tunes its display and
    /// microphone choice for it. Read at startup.</summary>
    public bool NativeG2 { get; set; }

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

    /// <summary>This run's engine log: engine.log, or engine.2.log and so on while
    /// another (hung) copy still holds it.</summary>
    public static string LogPath { get; private set; } = Path.Combine(DataDir, "engine.log");
    private static string PreviousLogPath => Path.Combine(DataDir, "engine.previous.log");
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
                     "--native-audio-output", ComputerAudio ? "true" : "false",
                 })
            psi.ArgumentList.Add(a);

        // Our own settings database, separate from Zeus's.
        psi.Environment["SIMPLEHPSDR_PREFS_PATH"] = Path.Combine(DataDir, "prefs.db");
        // The fork has no plugin or VST host (removed 2026-11-08), so nothing to point away from Zeus's.
        // Everything else the engine would keep in Zeus's folder (FFT wisdom, LAN
        // certificate, NR3 models, WSPR files) goes to ours, backups leave Zeus Link's
        // files alone, and the Windows firewall rule has our own name, so granting it
        // never replaces Zeus's rule. TCI clients see SimpleHPSDR as the device.
        // Engine fork, 2.0.68-simplehpsdr.4.
        psi.Environment["STATION_ENGINE_DATA_DIR"] = DataDir;
        psi.Environment["STATION_ENGINE_FIREWALL_RULE"] = FirewallRuleName;
        psi.Environment["STATION_ENGINE_TCI_DEVICE"] = "SimpleHPSDR";
        if (NativeG2) psi.Environment["STATION_ENGINE_NATIVE_G2"] = "1";
        CopyFromZeusFolder();
        RenameOldEngineFiles();
        psi.Environment["Logging__LogLevel__Microsoft.AspNetCore"] = "Warning";

        _log = OpenLog();
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += (_, _) => OnExited();
        _process.OutputDataReceived += (_, e) => WriteLog(e.Data);
        _process.ErrorDataReceived += (_, e) => WriteLog(e.Data);
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        // The path too: an orphan is only ours if it runs this executable
        // (other programs run an engine with the same process name).
        File.WriteAllText(PidPath, $"{_process.Id}\n{Path.GetFullPath(exe)}");

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
                if (r.IsSuccessStatusCode)
                {
                    _running = true;
                    return;
                }
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
    /// <summary>The Windows firewall rule the engine grants and checks for.</summary>
    public const string FirewallRuleName = "SimpleHPSDR (HPSDR receive)";

    /// <summary>Before 0.7.2 the engine kept its FFT wisdom and NR3 models in Zeus's
    /// folder. Copy them over once, so the first start in our own folder doesn't spend
    /// minutes rebuilding the wisdom. Zeus's files are left as they are.</summary>
    private static void CopyFromZeusFolder()
    {
        try
        {
            var zeus = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zeus");
            if (!Directory.Exists(zeus)) return;
            var wisdom = Path.Combine(DataDir, "wdspWisdom01");
            if (!File.Exists(wisdom) && File.Exists(Path.Combine(zeus, "wdspWisdom01")))
            {
                // The stamp says which WDSP build made it; the engine rebuilds on a mismatch.
                var stamp = Path.Combine(zeus, "wdspWisdom01.version");
                if (File.Exists(stamp)) File.Copy(stamp, wisdom + ".version", overwrite: true);
                File.Copy(Path.Combine(zeus, "wdspWisdom01"), wisdom);
            }
            var models = Path.Combine(zeus, "nr3-models");
            var ours = Path.Combine(DataDir, "nr3-models");
            if (Directory.Exists(models) && !Directory.Exists(ours))
            {
                Directory.CreateDirectory(ours);
                foreach (var f in Directory.GetFiles(models))
                    File.Copy(f, Path.Combine(ours, Path.GetFileName(f)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Engines before 2.0.68-simplehpsdr.18 named some of their files in
    /// our folder after Zeus. Keep the logbook and LAN certificate under the new
    /// names, and drop the old logs.</summary>
    private static void RenameOldEngineFiles()
    {
        foreach (var (from, to) in new[] { ("zeus-logbook.db", "simplehpsdr-logbook.db"), ("zeus-lan.pfx", "simplehpsdr-lan.pfx") })
        {
            try
            {
                string a = Path.Combine(DataDir, from), b = Path.Combine(DataDir, to);
                if (File.Exists(a) && !File.Exists(b)) File.Move(a, b);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        try
        {
            var logs = Path.Combine(DataDir, "logs");
            if (Directory.Exists(logs))
                foreach (var f in Directory.GetFiles(logs, "zeus-app*.log")) File.Delete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static StreamWriter OpenLog()
    {
        // Each start begins a new log; keep the last one, which may hold a crash.
        var main = Path.Combine(DataDir, "engine.log");
        try { if (File.Exists(main)) File.Move(main, PreviousLogPath, overwrite: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        for (int n = 1; ; n++)
        {
            var path = n == 1 ? main : Path.Combine(DataDir, $"engine.{n}.log");
            try
            {
                var w = new StreamWriter(path, append: false) { AutoFlush = true };
                LogPath = path;
                return w;
            }
            // Another copy of SimpleHPSDR, hung, still has it open: use the next.
            catch (Exception e) when (n < 9 && e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// An engine left running by a crash of ours. Only a process with the saved
    /// number that is running the saved StationEngine executable is ours: the
    /// number may have been reused, and other programs run an engine with the same name.
    /// Best effort: anything unexpected leaves the process alone.
    /// </summary>
    private static void KillOrphan()
    {
        try
        {
            if (!File.Exists(PidPath)) return;
            var lines = File.ReadAllLines(PidPath);
            if (lines.Length < 2 || !int.TryParse(lines[0], out var pid) || lines[1].Length == 0) return;
            string ours = Path.GetFullPath(lines[1]);
            using var p = Process.GetProcessById(pid);
            string? running = p.MainModule?.FileName;
            var compare = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (running is null || !string.Equals(Path.GetFullPath(running), ours, compare)) return;
            p.Kill(entireProcessTree: true);
            p.WaitForExit(3000);
        }
        catch (Exception) { }   // not running, not ours to read, or already gone
        finally
        {
            try { File.Delete(PidPath); } catch (Exception) { }
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private void OnExited()
    {
        if (!_running) return;   // we stopped it, or it never started
        _running = false;
        int code;
        try { code = _process?.ExitCode ?? -1; }
        catch (InvalidOperationException) { code = -1; }
        Stopped?.Invoke(code);
    }

    /// <summary>The last fatal line in the log, if there is one.</summary>
    public static string? LastFatalLine()
    {
        try
        {
            using var f = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(f);
            var line = r.ReadToEnd().Split('\n')
                .LastOrDefault(l => l.Contains("fatal", StringComparison.OrdinalIgnoreCase)
                                 || l.StartsWith("Unhandled exception", StringComparison.OrdinalIgnoreCase));
            return line?.Trim() is { Length: > 0 } t ? t[..Math.Min(t.Length, 200)] : null;
        }
        catch (IOException) { return null; }
    }

    public void Dispose()
    {
        _running = false;
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
