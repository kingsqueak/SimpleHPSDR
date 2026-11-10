using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// The antenna analyser window, on the engine's VnaService. Sources: the
/// connected radio (its own bridge under ~5 W TUNE: SWR and return loss, no
/// phase; an HL2 uses its FPGA VNA instead), or a NanoVNA on a serial port the
/// operator picks (the engine never probes ports itself). The radio source
/// transmits across the sweep, so it asks first; the engine checks every point
/// against the transmit safety rules and band plan before keying.
/// </summary>
public sealed partial class AnalyzerViewModel : ObservableObject
{
    private readonly Func<StationClient?> _client;
    private readonly string _antenna;
    private DispatcherTimer? _poll;
    private CancellationTokenSource? _sweepCts;

    public AnalyzerViewModel(Func<StationClient?> client, long vfoHz, string antenna)
    {
        _client = client;
        _antenna = antenna;
        BandNames = [.. Band.All.Select(b => $"{b.Name} m"), "Custom"];
        int at = Array.FindIndex(Band.All, b => b.Contains(vfoHz));
        _bandIndex = at < 0 ? BandNames.Length - 1 : at;
        ApplyBand();
    }

    public string[] Sources { get; } = ["This radio", "NanoVNA (USB)"];
    public string[] BandNames { get; }
    public ObservableCollection<string> Ports { get; } = [];
    public ObservableCollection<StationClient.VnaSweep> Saved { get; } = [];
    public ObservableCollection<StationClient.VnaCalibration> Calibrations { get; } = [];

    [ObservableProperty] private int _sourceIndex;
    [ObservableProperty] private string? _selectedPort;
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private bool _sourceReady;
    [ObservableProperty] private bool _vector;
    [ObservableProperty] private bool _needsCalibration;
    [ObservableProperty] private int _maxPoints = 201;

    [ObservableProperty] private int _bandIndex;
    [ObservableProperty] private decimal _startMHz;
    [ObservableProperty] private decimal _endMHz;
    [ObservableProperty] private decimal _points = 101;

    [ObservableProperty] private bool _confirming;
    [ObservableProperty] private bool _running;
    [ObservableProperty] private string _status = "";

    [ObservableProperty] private IReadOnlyList<StationClient.VnaPoint>? _plotPoints;
    [ObservableProperty] private long _plotBandLow;
    [ObservableProperty] private long _plotBandHigh;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private StationClient.VnaSweep? _selectedSaved;
    [ObservableProperty] private StationClient.VnaCalibration? _selectedCalibration;
    [ObservableProperty] private string _calibrationName = "Cal 1";

    public bool UsesRadio => SourceIndex == 0;
    public bool UsesNanoVna => SourceIndex == 1;
    public string WarningText =>
        $"This transmits a carrier of about 5 W from {StartMHz:0.000} to {EndMHz:0.000} MHz through the antenna on {_antenna}. " +
        "Put an amplifier on standby or bypass first. Every frequency is checked against your band plan before it keys; " +
        "the radio's frequency returns when it's done.";

    partial void OnSourceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(UsesRadio));
        OnPropertyChanged(nameof(UsesNanoVna));
        Confirming = false;
        if (value == 0) _ = Do(async c => ShowSource(await c.SetVnaSourceAsync("radio", null)));
        else _ = LoadAsync();
    }

    partial void OnBandIndexChanged(int value) => ApplyBand();
    partial void OnStartMHzChanged(decimal value) => OnPropertyChanged(nameof(WarningText));
    partial void OnEndMHzChanged(decimal value) => OnPropertyChanged(nameof(WarningText));
    partial void OnSelectedSavedChanged(StationClient.VnaSweep? value) { if (value is not null) Show(value); }

    private Band? CurrentBand => BandIndex >= 0 && BandIndex < Band.All.Length ? Band.All[BandIndex] : null;

    private void ApplyBand()
    {
        if (CurrentBand is not { } b) return;   // Custom: leave the range as typed
        StartMHz = b.LowHz / 1_000_000m;
        EndMHz = b.HighHz / 1_000_000m;
    }

    private async Task Do(Func<StationClient, Task> act)
    {
        if (_client() is not { } c) { Status = "Connect to the radio first."; return; }
        try { await act(c); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException)
        {
            Status = ex.Message;
        }
    }

    public Task LoadAsync() => Do(async c =>
    {
        ShowSource(await c.GetVnaSourceAsync());
        var cap = await c.GetVnaCapabilityAsync();
        ShowCapability(cap);
        Saved.Clear();
        foreach (var s in (await c.GetVnaSweepsAsync()).OrderByDescending(s => s.CapturedUtc)) Saved.Add(s);
        Calibrations.Clear();
        foreach (var k in await c.GetVnaCalibrationsAsync()) Calibrations.Add(k);
    });

    private void ShowSource(StationClient.VnaSource s)
    {
        string? keep = SelectedPort;
        Ports.Clear();
        foreach (var d in s.Devices) Ports.Add(d);
        SelectedPort = keep is not null && Ports.Contains(keep) ? keep : s.DeviceId ?? Ports.FirstOrDefault();
        if (s.Source == "nanovna" && SourceIndex != 1) SourceIndex = 1;
        SourceText = s.Source == "nanovna"
            ? s.Connected ? $"NanoVNA connected: {s.DeviceName ?? s.DeviceId}" : $"NanoVNA not connected{(s.Error is { } e ? $": {e}" : ".")}"
            : s.Connected ? $"Using {s.DeviceName}." : "Connect to the radio to sweep with it.";
        SourceReady = s.Connected;
    }

    private void ShowCapability(StationClient.VnaCapability cap)
    {
        Vector = cap.NativeVector;
        NeedsCalibration = cap.RequiresCalibration;
        MaxPoints = Math.Max(3, cap.MaximumPoints);
        if (Points > MaxPoints) Points = MaxPoints;
        if (!cap.Available) Status = cap.Reason;
    }

    [RelayCommand]
    private Task ConnectNanoVna() => Do(async c =>
    {
        if (SelectedPort is null) { Status = "Plug in the NanoVNA and pick its port."; return; }
        ShowSource(await c.SetVnaSourceAsync("nanovna", SelectedPort));
        ShowCapability(await c.GetVnaCapabilityAsync());
    });

    [RelayCommand]
    private Task RefreshPorts() => Do(async c => ShowSource(await c.GetVnaSourceAsync()));

    /// <summary>The radio source asks first (it transmits); a NanoVNA sweeps at once.</summary>
    [RelayCommand]
    private async Task SweepAsync()
    {
        if (Running) return;
        if (EndMHz <= StartMHz) { Status = "The end has to be above the start."; return; }
        if (UsesRadio && !Confirming) { Confirming = true; return; }
        Confirming = false;
        await RunSweepAsync();
    }

    [RelayCommand]
    private void CancelConfirm() => Confirming = false;

    private async Task RunSweepAsync()
    {
        if (_client() is not { } c) { Status = "Connect to the radio first."; return; }
        long start = (long)(StartMHz * 1_000_000m), end = (long)(EndMHz * 1_000_000m);
        int points = (int)Math.Clamp(Points, 3, MaxPoints);
        string band = CurrentBand is { } b ? $"{b.Name}m" : "custom";
        Running = true;
        Status = "Sweeping…";
        _sweepCts = new CancellationTokenSource();
        _poll ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, async (_, _) => await PollAsync());
        _poll.Start();
        try
        {
            var sweep = await c.SweepVnaAsync(_antenna, band, start, end, points,
                NeedsCalibration ? SelectedCalibration?.Id : null, null, _sweepCts.Token);
            Saved.Insert(0, sweep);
            SelectedSaved = sweep;
            Status = "Done.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException or OperationCanceledException)
        {
            Status = $"Sweep stopped: {ex.Message}";
        }
        finally
        {
            Running = false;
            _poll.Stop();
        }
    }

    private async Task PollAsync()
    {
        if (_client() is not { } c) return;
        try
        {
            var s = await c.GetVnaStatusAsync();
            if (s.Running && s.PointsExpected > 0)
                Status = $"Sweeping… {s.PointsReceived} of {s.PointsExpected}";
        }
        catch (Exception) { }
    }

    [RelayCommand]
    private Task Cancel() => Do(async c =>
    {
        await c.CancelVnaAsync();
        _sweepCts?.Cancel();
    });

    [RelayCommand]
    private Task DeleteSaved() => Do(async c =>
    {
        if (SelectedSaved is not { } s) return;
        await c.DeleteVnaSweepAsync(s.Id);
        Saved.Remove(s);
        SelectedSaved = Saved.FirstOrDefault();
        if (SelectedSaved is null) { PlotPoints = null; ResultText = ""; }
    });

    // Calibration standards (only for sources that need it).
    [RelayCommand] private Task CalOpen() => CaptureAsync(1);
    [RelayCommand] private Task CalShort() => CaptureAsync(2);
    [RelayCommand] private Task CalLoad() => CaptureAsync(3);
    [RelayCommand] private Task CalThru() => CaptureAsync(0);

    private Task CaptureAsync(int standard) => Do(async c =>
    {
        long start = (long)(StartMHz * 1_000_000m), end = (long)(EndMHz * 1_000_000m);
        string band = CurrentBand is { } b ? $"{b.Name}m" : "custom";
        Status = "Measuring the standard…";
        var cal = await c.CaptureVnaCalibrationAsync(CalibrationName, _antenna, band, start, end,
            (int)Math.Clamp(Points, 3, MaxPoints), standard, SelectedCalibration?.Id);
        var old = Calibrations.FirstOrDefault(k => k.Id == cal.Id);
        if (old is not null) Calibrations.Remove(old);
        Calibrations.Insert(0, cal);
        SelectedCalibration = cal;
        Status = cal.ReflectionReady ? "Calibration ready." : "Standard measured; open, short and load are all needed.";
    });

    private void Show(StationClient.VnaSweep s)
    {
        PlotPoints = s.Points;
        var band = Band.All.FirstOrDefault(b => b.HighHz > s.StartHz && b.LowHz < s.EndHz);
        (PlotBandLow, PlotBandHigh) = band is null ? (0, 0) : (band.LowHz, band.HighHz);
        var m = s.Metrics;
        var lines = new List<string>();
        if (m.MinimumSwr is { } swr) lines.Add($"Lowest SWR {swr:0.00} at {m.ResonantFrequencyHz / 1e6:0.0000} MHz");
        if (m.MaximumReturnLossDb is { } rl) lines.Add($"Return loss {rl:0.0} dB");
        var bw = new List<string>();
        if (m.Bandwidth15Hz is { } b15) bw.Add($"{b15 / 1e3:0} kHz under 1.5");
        if (m.Bandwidth20Hz is { } b20) bw.Add($"{b20 / 1e3:0} kHz under 2");
        if (m.Bandwidth30Hz is { } b30) bw.Add($"{b30 / 1e3:0} kHz under 3");
        if (bw.Count > 0) lines.Add("Bandwidth: " + string.Join(", ", bw));
        if (m.ResistanceAtResonanceOhms is { } r && m.ReactanceAtResonanceOhms is { } x)
            lines.Add($"Impedance there {r:0} {(x < 0 ? "-" : "+")} j{Math.Abs(x):0} Ω");
        if (m.EstimatedQ is { } q) lines.Add($"Q about {q:0}");
        lines.Add($"{s.CapturedUtc.ToLocalTime():g} · {s.Board} · {s.Antenna}{(s.Calibrated ? " · calibrated" : "")}");
        ResultText = string.Join("\n", lines);
    }

    public void Closed()
    {
        _poll?.Stop();
        if (Running) _ = Cancel();
    }
}
