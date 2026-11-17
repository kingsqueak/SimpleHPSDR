using System.Diagnostics;

namespace SimpleHPSDR.Engine;

/// <summary>
/// A Ulanzi Dial (D100H, Bluetooth) through the dial-helper program
/// (tools/dial-helper): the helper reads the dial, keeps its own keys, volume
/// and mouse clicks away from the rest of the computer, and prints one line
/// per control. This starts it, turns the lines into events, and ends it.
/// macOS only so far; elsewhere <see cref="Start"/> reports that and does nothing.
/// </summary>
public sealed class DialLink : IDisposable
{
    private Process? _process;

    /// <summary>Button codes as the mapping page has them (G2Panels, product Dial):
    /// 1 to 4 the side buttons, 5 to 7 the top ones, 8 the dial's press.</summary>
    private static readonly Dictionary<string, int> Buttons = new()
    {
        ["L1"] = 1, ["L2"] = 2, ["R1"] = 3, ["R2"] = 4, ["T1"] = 5, ["T2"] = 6, ["T3"] = 7,
    };
    public const int DialPress = 8;

    /// <summary>A button: its code, and 1 for pressed or 0 for let go.</summary>
    public event Action<int, int>? Button;
    /// <summary>The dial turned one detent: whether it was held down, and +1 (right) or -1.</summary>
    public event Action<bool, int>? Turned;
    /// <summary>What the link is doing, for the Config page.</summary>
    public event Action<string>? StatusChanged;

    public bool IsRunning => _process is { HasExited: false };

    private static string? FindHelper()
    {
        var env = Environment.GetEnvironmentVariable("SIMPLEHPSDR_DIAL_HELPER");
        if (!string.IsNullOrEmpty(env)) return File.Exists(env) ? env : null;
        var baseDir = AppContext.BaseDirectory;
        var beside = Path.Combine(baseDir, "dial-helper");
        if (File.Exists(beside)) return beside;
        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            var dev = Path.Combine(dir.FullName, "tools", "dial-helper", "dial-helper");
            if (File.Exists(dev)) return dev;
        }
        return null;
    }

    public void Start()
    {
        if (IsRunning) return;
        if (!OperatingSystem.IsMacOS())
        {
            StatusChanged?.Invoke("The dial is supported on a Mac only so far.");
            return;
        }
        if (FindHelper() is not { } exe)
        {
            StatusChanged?.Invoke("The dial helper program is missing from this copy of SimpleHPSDR.");
            return;
        }
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,    // the helper ends when this closes
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        try
        {
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data is { } line) OnLine(line); };
            p.Exited += (_, _) => StatusChanged?.Invoke("Off.");
            p.Start();
            p.BeginOutputReadLine();
            _process = p;
            StatusChanged?.Invoke("Waiting for the dial (is it switched on and paired?).");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusChanged?.Invoke($"The dial helper did not start: {ex.Message}");
        }
    }

    private void OnLine(string line)
    {
        var parts = line.Split(' ', 2);
        string arg = parts.Length > 1 ? parts[1] : "";
        switch (parts[0])
        {
            case "device":
                StatusChanged?.Invoke(arg == "on" ? "Connected." : "Waiting for the dial (is it switched on and paired?).");
                break;
            case "down" when Buttons.TryGetValue(arg, out int down): Button?.Invoke(down, 1); break;
            case "up" when Buttons.TryGetValue(arg, out int up): Button?.Invoke(up, 0); break;
            case "click": Button?.Invoke(DialPress, 1); Button?.Invoke(DialPress, 0); break;
            case "turn": Turned?.Invoke(false, arg == "cw" ? 1 : -1); break;
            case "pushturn": Turned?.Invoke(true, arg == "cw" ? 1 : -1); break;
            case "warn": StatusChanged?.Invoke($"Connected, but: {arg}."); break;
        }
    }

    public void Stop()
    {
        var p = _process;
        _process = null;
        if (p is null) return;
        try
        {
            p.StandardInput.Close();           // it undoes its key mapping and exits
            if (!p.WaitForExit(1500)) p.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
        p.Dispose();
    }

    public void Dispose() => Stop();
}
