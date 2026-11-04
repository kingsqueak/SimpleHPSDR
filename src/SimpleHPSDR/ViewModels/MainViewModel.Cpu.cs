using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SimpleHPSDR.ViewModels;

/// <summary>CPU use of SimpleHPSDR and its engine, averaged over 5 s.</summary>
public sealed partial class MainViewModel
{
    // Beside the CPU readout, while the TCI server runs.
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TciStatusShown))] private string _tciStatusText = "";
    public bool TciStatusShown => TciStatusText.Length > 0;

    private async Task RefreshTciStatusAsync()
    {
        if (!IsConnected || _client is null)
        {
            TciStatusText = "";
            return;
        }
        try
        {
            var t = await _client.GetTciStatusAsync();
            TciStatusText = !t.CurrentlyEnabled ? ""
                : $"TCI {t.CurrentBindAddress}:{t.CurrentPort} · {(t.ClientCount == 1 ? "1 client" : $"{t.ClientCount} clients")}";
        }
        catch (Exception) { TciStatusText = ""; }
    }

    private readonly DispatcherTimer _cpuTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Stopwatch _cpuWall = new();
    private TimeSpan _lastAppCpu, _lastEngineCpu;

    [ObservableProperty] private string _cpuText = "CPU —";
    [ObservableProperty] private string _cpuDetail = "";
    /// <summary>ok below 25%, warn 25-50%, bad above 50%: picks the colour.</summary>
    [ObservableProperty] private string _cpuLevel = "ok";

    private void StartCpuMonitor()
    {
        _lastAppCpu = Process.GetCurrentProcess().TotalProcessorTime;
        _lastEngineCpu = _engine.CpuTime;
        _cpuWall.Restart();
        _cpuTimer.Tick += (_, _) =>
        {
            SampleCpu();
            _ = RefreshTciStatusAsync();
        };
        _cpuTimer.Start();
    }

    private void SampleCpu()
    {
        var app = Process.GetCurrentProcess().TotalProcessorTime;
        var engine = _engine.CpuTime;
        double wall = _cpuWall.Elapsed.TotalSeconds;
        _cpuWall.Restart();
        if (wall <= 0) return;

        // Percent of one core for each, then the pair as a share of the machine.
        double appPct = (app - _lastAppCpu).TotalSeconds / wall * 100;
        double enginePct = engine < _lastEngineCpu ? 0 : (engine - _lastEngineCpu).TotalSeconds / wall * 100;
        (_lastAppCpu, _lastEngineCpu) = (app, engine);

        double machine = (appPct + enginePct) / Environment.ProcessorCount;
        CpuText = $"CPU {machine:0}%";
        CpuLevel = machine < 25 ? "ok" : machine <= 50 ? "warn" : "bad";
        CpuDetail = $"SimpleHPSDR + engine: {machine:0.0}% of this {Environment.ProcessorCount}-core machine " +
                    $"(app {appPct:0}%, engine {enginePct:0}% of one core), 5 s average";
    }
}
