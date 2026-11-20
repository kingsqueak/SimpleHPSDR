using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Contracts;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>What the signal analyser should look at now: the active slice in receive,
/// the transmitting slice while keyed.</summary>
public sealed record SignalTarget(bool Transmitting, long Hz, RxMode Mode, int HighHz, string Slice,
    bool TestTone, double MicPeakDbfs, double AlcDb);

/// <summary>One reading in the analyser's list.</summary>
public sealed partial class FindingRow : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _advice = "";
    [ObservableProperty] private IBrush _brush = Brushes.Gray;
}

/// <summary>
/// The signal analyser: an envelope scope, the spectrum round the carrier, and readings
/// with advice, for SSB and AM. In receive it looks at the signal on the active slice;
/// while transmitting, at our own signal as RX1 hears it. The IQ comes from the engine's
/// TCI server (as DRM's does), so it only listens and the radio is left as it is.
/// </summary>
public sealed partial class SignalAnalyzerViewModel : ObservableObject
{
    private static readonly IBrush Grey = new SolidColorBrush(Color.Parse("#5A6270"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#3DD68C"));
    private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#5AA9FF"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F5D76E"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly double[] Seconds = [0.05, 0.2, 1, 5];
    private const int Columns = 900;
    private const int FindingsEvery = 6;             // ticks: readings change a few times a second, not fifteen

    private readonly Func<StationClient?> _client;
    private readonly Func<SignalTarget> _target;
    private readonly SignalAnalysis _analysis = new();
    private DispatcherTimer? _timer;
    private TciIq? _iq;
    private bool _connecting;
    private DateTime _nextTry, _lostSince;
    private int _tick;
    private long _lastHz;
    private double _lastShift = double.NaN;
    // The TX filter's high cut as the engine has it: the one thing that is certainly what goes out.
    private int? _txHigh;
    private bool _askingCuts;
    private DateTime _cutsAsked;

    [ObservableProperty] private SignalReport? _report;
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _status = "Starting…";
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private IBrush _headlineBrush = Grey;
    [ObservableProperty] private int _timeBaseIndex = 2;
    [ObservableProperty] private bool _frozen;

    public string[] TimeBases { get; } = ["50 ms", "200 ms", "1 s", "5 s"];
    public ObservableCollection<FindingRow> Findings { get; } = [];

    public SignalAnalyzerViewModel(Func<StationClient?> client, Func<SignalTarget> target)
    {
        _client = client;
        _target = target;
    }

    [RelayCommand] private void ToggleFrozen() => Frozen = !Frozen;

    public void Start()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        DropIq();
    }

    private void DropIq()
    {
        if (_iq is not { } iq) return;
        _iq = null;
        _lastShift = double.NaN;
        Task.Run(iq.Dispose);       // it waits for the socket to close: not on the UI thread
    }

    private void Idle(string status)
    {
        Status = status;
        if (Frozen) return;
        Report = null;
        Findings.Clear();
        Headline = "";
    }

    private void Tick()
    {
        if (_client() is not { } client)
        {
            DropIq();
            SourceText = "";
            Idle("Connect to a radio to analyse a signal.");
            return;
        }
        if (_iq is not { } iq)
        {
            _ = ConnectAsync(client);
            return;
        }
        if (!iq.Connected)
        {
            // The TCI server may have been turned off or moved: ask the engine again.
            if (_lostSince == default) _lostSince = DateTime.UtcNow;
            else if (DateTime.UtcNow - _lostSince > TimeSpan.FromSeconds(4)) { _lostSince = default; DropIq(); }
            Idle(iq.Error.Length > 0 ? $"TCI: {iq.Error}" : "Connecting to the TCI server…");
            return;
        }
        _lostSince = default;

        var t = _target();
        _ = RefreshTxCutsAsync(client);
        bool am = t.Mode is RxMode.AM or RxMode.SAM, ssb = t.Mode is RxMode.LSB or RxMode.USB;
        // In receive the other station's audio may be wider than our filter: judge it against a normal channel.
        int high = t.Transmitting ? _txHigh ?? t.HighHz : Math.Max(t.HighHz, am ? 5000 : 3000);
        _analysis.Configure(new SignalSetup(ssb ? SignalKind.Ssb : am ? SignalKind.Am : SignalKind.Other,
            t.Mode == RxMode.LSB, high, t.Transmitting, t.TestTone, t.MicPeakDbfs, t.AlcDb, t.Transmitting ? 0 : t.HighHz));
        SourceText = t.Transmitting
            ? $"Transmitting on {t.Slice}  ·  {t.Hz / 1e6:0.000000} MHz {t.Mode}  ·  your signal as RX1 hears it"
            : $"Receiving on {t.Slice}  ·  {t.Hz / 1e6:0.000000} MHz {t.Mode}";

        int rate = _analysis.InputRate;
        if (iq.DdsHz <= 0 || rate == 0 || DateTime.UtcNow - iq.LastIq > TimeSpan.FromSeconds(2))
        {
            // Radios with one receiver give it to PureSignal while keyed: nothing to see then.
            Idle(t.Transmitting && rate != 0
                ? "The receiver isn't running while you transmit on this radio, so your own signal can't be seen."
                : "Waiting for the receiver…");
            return;
        }
        // The IQ is RX1's stream: the signal has to be inside it, with room for its sidebands.
        double shift = t.Hz - iq.DdsHz;
        if (Math.Abs(shift) > rate / 2.0 - 12000)
        {
            Idle(t.Transmitting
                ? $"The transmit frequency is outside RX1's span: put A1 on the band you transmit on to see your signal."
                : $"{t.Slice} is outside RX1's span. The analyser reads RX1's receiver: make A1 active, or a slice inside its span.");
            return;
        }
        if (shift != _lastShift)
        {
            _lastShift = shift;
            _analysis.ShiftHz = shift;
            if (Math.Abs(t.Hz - _lastHz) > 20) _analysis.Restart();
            _lastHz = t.Hz;
        }
        Status = "";
        if (Frozen) return;

        bool readings = _tick++ % FindingsEvery == 0;
        var report = _analysis.Snapshot(Seconds[Math.Clamp(TimeBaseIndex, 0, Seconds.Length - 1)], Columns, readings);
        Report = report;
        if (readings && report.Findings is { } findings) Show(findings, report);
    }

    private async Task ConnectAsync(StationClient client)
    {
        if (_connecting || DateTime.UtcNow < _nextTry) return;
        _connecting = true;
        try
        {
            var tci = await client.GetTciStatusAsync();
            if (_timer is null || _iq is not null) return;      // closed while asking
            if (!tci.CurrentlyEnabled)
            {
                Idle("The signal analyser needs the TCI server: turn it on in Config > CAT & TCI.");
                return;
            }
            string host = tci.CurrentBindAddress is null or "" or "0.0.0.0" or "::" or "*" ? "127.0.0.1" : tci.CurrentBindAddress;
            var iq = new TciIq(host, tci.CurrentPort, 0);
            iq.IqReceived += (samples, rate) => _analysis.Push(samples, rate);
            _iq = iq;
            Status = "Connecting to the TCI server…";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or StationException)
        {
            Idle("The engine isn't answering.");
        }
        finally
        {
            _connecting = false;
            _nextTry = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        }
    }

    /// <summary>Once a second, ask the engine for the TX filter in force (for the transmitting slice's mode).</summary>
    private async Task RefreshTxCutsAsync(StationClient client)
    {
        if (_askingCuts || DateTime.UtcNow - _cutsAsked < TimeSpan.FromSeconds(1)) return;
        _askingCuts = true;
        try
        {
            int high = TxCuts.Of(await client.GetStateAsync()).High;
            if (high > 0) _txHigh = high;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or StationException) { }
        finally
        {
            _askingCuts = false;
            _cutsAsked = DateTime.UtcNow;
        }
    }

    private static IBrush BrushOf(FindingLevel level) => level switch
    {
        FindingLevel.Good => Green,
        FindingLevel.Info => Blue,
        FindingLevel.Caution => Amber,
        FindingLevel.Bad => Red,
        _ => Grey,
    };

    // Rows are changed in place, so the list doesn't flicker or lose its place.
    private void Show(IReadOnlyList<Finding> findings, SignalReport report)
    {
        Headline = report.Headline;
        HeadlineBrush = BrushOf(report.Overall);
        while (Findings.Count > findings.Count) Findings.RemoveAt(Findings.Count - 1);
        for (int i = 0; i < findings.Count; i++)
        {
            if (i == Findings.Count) Findings.Add(new FindingRow());
            var (row, f) = (Findings[i], findings[i]);
            (row.Name, row.Value, row.Advice, row.Brush) = (f.Name, f.Value, f.Advice, BrushOf(f.Level));
        }
    }
}
