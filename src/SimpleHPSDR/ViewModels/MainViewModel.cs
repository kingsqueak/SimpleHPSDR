using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly RxMode[] ModeOrder =
        [RxMode.LSB, RxMode.USB, RxMode.CWL, RxMode.CWU, RxMode.AM, RxMode.SAM, RxMode.FM, RxMode.DIGL, RxMode.DIGU];
    private static readonly int[] Steps = [1, 5, 10, 100, 500, 1_000, 5_000, 10_000, 100_000];

    private readonly Settings _settings = Settings.Load();
    private readonly EngineHost _engine = new();
    private StationClient? _client;
    private RadioChoice? _radio;
    private readonly Latest<long> _vfoSender;
    private readonly Latest<double> _afSender;
    private readonly Latest<int> _attSender;
    private bool _applyingState;
    private readonly ConcurrentQueue<DisplayFrame> _frames = new();
    private int _framesScheduled;
    private float _latestDbm = -140;
    private int _meterScheduled;
    private readonly DispatcherTimer _statePoll = new() { Interval = TimeSpan.FromSeconds(1) };

    [ObservableProperty] private string _status = "Starting engine…";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _radioLabel = "No radio";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FrequencyText))]
    private long _vfoHz;
    [ObservableProperty] private int _filterLowHz;
    [ObservableProperty] private int _filterHighHz;
    [ObservableProperty] private double _afGainDb;
    [ObservableProperty] private double _attenDb;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreLabel))] [NotifyPropertyChangedFor(nameof(RfText))]
    private bool _preampOn;
    [ObservableProperty] private bool _autoAtt;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AttenText))] [NotifyPropertyChangedFor(nameof(RfText))]
    private int _attOffsetDb;
    [ObservableProperty] private bool _adcOverload;
    [ObservableProperty] private bool _muted;
    [ObservableProperty] private double _signalDbm = -140;
    [ObservableProperty] private string _sUnits = "S0";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PeakSUnits))]
    private double _peakDbm = -140;
    public string PeakSUnits => ToSUnits(PeakDbm);
    private DateTime _peakAt;
    private static readonly TimeSpan PeakHold = TimeSpan.FromSeconds(3);
    [ObservableProperty] private string _frequencyEntry = "";
    /// <summary>True while the frequency display is replaced by its entry box.</summary>
    [ObservableProperty] private bool _editingFrequency;

    public ObservableCollection<Choice> Bands { get; } = [];
    public ObservableCollection<Choice> Modes { get; } = [];
    /// <summary>The five filter buttons: engine slots F1-F5 for the current mode.</summary>
    public ObservableCollection<FilterSlot> Filters { get; } = [];
    private static readonly string[] FilterSlotNames = ["F1", "F2", "F3", "F4", "F5"];
    public ObservableCollection<Choice> TuneSteps { get; } = [];
    // Span = receiver sample rate. 768k and 1.5M are Protocol 2 only.
    private static readonly int[] SpanRatesP1 = [48_000, 96_000, 192_000, 384_000];
    private static readonly int[] SpanRatesP2 = [48_000, 96_000, 192_000, 384_000, 768_000, 1_536_000];
    public ObservableCollection<Choice> SpanRates { get; } = [];
    [ObservableProperty] private string _spanLabel = "";

    private static string RateLabel(int hz) => hz >= 1_000_000 ? $"{hz / 1e6:0.#}M" : $"{hz / 1000}k";

    private void LoadSpanRates(bool protocol2, int current)
    {
        SpanRates.Clear();
        foreach (var r in protocol2 ? SpanRatesP2 : SpanRatesP1)
            SpanRates.Add(new Choice(RateLabel(r), r, c => _ = SelectSpanAsync((int)c.Value)));
        Mark(SpanRates, current);
        _sampleRateHz = current;
        NotifyZoomSpan();
        SpanLabel = RateLabel(current);
    }

    private async Task SelectSpanAsync(int hz)
    {
        if (!IsConnected) return;
        await Try(async () =>
        {
            await _client!.SetSampleRateAsync(hz);
            Mark(SpanRates, hz);
            _sampleRateHz = hz;
            NotifyZoomSpan();
            SpanLabel = RateLabel(hz);
            // Reconnects use this rate, so remember it.
            _settings.SampleRate = hz;
            _settings.Save();
        });
    }

    // Waterfall resolution (engine display FFT size) and speed (a row every N
    // display frames at 30 frames/s). Both live in the engine's display settings.
    private static readonly int[] FftSizes = [4096, 8192, 16_384, 32_768, 65_536, 131_072];
    private static readonly int[] WaterfallPeriods = [1, 2, 3, 5, 10];
    private const int DefaultFftSize = 16_384;
    public ObservableCollection<Choice> Resolutions { get; } = [];
    public ObservableCollection<Choice> WaterfallSpeeds { get; } = [];
    [ObservableProperty] private string _resolutionLabel = "";
    [ObservableProperty] private string _speedLabel = "";

    private static string SpeedText(int period) => $"{30 / period}/s";

    private async Task LoadDisplayTuningAsync()
    {
        int fft = DefaultFftSize, period = 1;
        try
        {
            var t = await _client!.GetDisplayTuningAsync();
            fft = t.RxDisplayFftSize ?? DefaultFftSize;
            period = t.WaterfallUpdatePeriod;
        }
        catch (Exception) { }
        _displayFftSize = fft;
        Resolutions.Clear();
        foreach (var n in FftSizes)
            Resolutions.Add(new Choice($"{n / 1024}k", n, c => _ = SelectResolutionAsync((int)c.Value)));
        WaterfallSpeeds.Clear();
        foreach (var p in WaterfallPeriods)
            WaterfallSpeeds.Add(new Choice(SpeedText(p), p, c => _ = SelectSpeedAsync((int)c.Value)));
        Mark(Resolutions, fft);
        Mark(WaterfallSpeeds, period);
        ResolutionLabel = $"{fft / 1024}k";
        SpeedLabel = SpeedText(period);
    }

    private int _displayFftSize = DefaultFftSize;

    private async Task SelectResolutionAsync(int fft) => await Try(async () =>
    {
        await _client!.SetDisplayTuningAsync(fftSize: fft);
        _displayFftSize = fft;
        Mark(Resolutions, fft);
        ResolutionLabel = $"{fft / 1024}k";
    });

    private async Task SelectSpeedAsync(int period) => await Try(async () =>
    {
        await _client!.SetDisplayTuningAsync(waterfallPeriod: period);
        Mark(WaterfallSpeeds, period);
        SpeedLabel = SpeedText(period);
    });

    // Panadapter zoom: off is the full span. At 384 kHz, level 8 is a 48 kHz span.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomLabel))] [NotifyPropertyChangedFor(nameof(ZoomOn))]
    [NotifyPropertyChangedFor(nameof(ZoomSpanText))] [NotifyPropertyChangedFor(nameof(ZoomSlider))]
    private int _zoomLevel = 1;
    public string ZoomLabel => ZoomOn ? ZoomSpanText : "full";
    public bool ZoomOn => ZoomLevel > 1;
    public string ZoomSpanText => SpanText(Math.Max(ZoomLevel > 1 ? ZoomLevel : _settings.ZoomOnLevel, 1));
    private string SpanText(int level) => _sampleRateHz <= 0 ? $"{level}×"
        : _sampleRateHz / level >= 1000 ? $"{_sampleRateHz / level / 1000} kHz" : $"{_sampleRateHz / level} Hz";

    // The slider is logarithmic: each step of travel is the same ratio of span.
    private const int MinZoomOnLevel = 2, MaxZoomLevel = 32;
    public double ZoomSlider
    {
        get => Math.Log2(ZoomOn ? ZoomLevel : _settings.ZoomOnLevel);
        set
        {
            int level = Math.Clamp((int)Math.Round(Math.Pow(2, value)), MinZoomOnLevel, MaxZoomLevel);
            if (level != ZoomLevel) SetZoom(level);
        }
    }
    private Latest<int>? _zoomSender;

    [ObservableProperty] private bool _ctunOn;

    // Panadapter trace range, kept at least 10 dB apart. With Auto on it
    // follows the noise floor; moving either slider turns Auto off.
    [ObservableProperty] private double _specMinDb;
    [ObservableProperty] private double _specMaxDb;
    [ObservableProperty] private bool _autoSpectrum;
    [ObservableProperty] private double _panFraction;

    partial void OnPanFractionChanged(double value)
    {
        _settings.PanFraction = value;
        _settings.Save();
    }
    private bool _applyingAutoRange;
    private const double AutoRangeDb = 50;
    private DateTime _lastAutoRange;

    partial void OnSpecMinDbChanged(double value)
    {
        if (value > SpecMaxDb - 10) SpecMaxDb = value + 10;
        SpecRangeEdited();
    }

    partial void OnSpecMaxDbChanged(double value)
    {
        if (value < SpecMinDb + 10) SpecMinDb = value - 10;
        SpecRangeEdited();
    }

    private void SpecRangeEdited()
    {
        if (_applyingAutoRange) return;
        AutoSpectrum = false;
        _settings.SpecMinDb = SpecMinDb;
        _settings.SpecMaxDb = SpecMaxDb;
        _settings.Save();
    }

    partial void OnAutoSpectrumChanged(bool value)
    {
        _settings.AutoSpectrum = value;
        _settings.Save();
    }

    [RelayCommand]
    private void ToggleAutoSpectrum() => AutoSpectrum = !AutoSpectrum;

    /// <summary>POP on the waterfall: remembered between runs.</summary>
    public bool WaterfallPop
    {
        get => _settings.WaterfallPop;
        set
        {
            if (_settings.WaterfallPop == value) return;
            _settings.WaterfallPop = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void ToggleWaterfallPop() => WaterfallPop = !WaterfallPop;

    /// <summary>With Auto on, put the noise floor at the bottom with 50 dB above
    /// it (bright waterfall, tall trace); a few times a second so the sliders
    /// don't jitter.</summary>
    public void FollowNoiseFloor(double floorDb)
    {
        if (!AutoSpectrum || DateTime.UtcNow - _lastAutoRange < TimeSpan.FromMilliseconds(400)) return;
        _lastAutoRange = DateTime.UtcNow;
        _applyingAutoRange = true;
        SpecMinDb = Math.Round(floorDb);
        SpecMaxDb = Math.Round(floorDb + AutoRangeDb);
        _applyingAutoRange = false;
    }

    private DateTime _lastAutoRangeB;
    private (double Min, double Max) _rangeB = (-130, -80);

    /// <summary>Slice B's panadapter range: with Auto it follows its own noise
    /// floor (another band can sit far lower or higher), otherwise the sliders.</summary>
    public (double Min, double Max) SliceBRange(double floorDb)
    {
        if (!AutoSpectrum) return (SpecMinDb, SpecMaxDb);
        if (DateTime.UtcNow - _lastAutoRangeB >= TimeSpan.FromMilliseconds(400))
        {
            _lastAutoRangeB = DateTime.UtcNow;
            _rangeB = (Math.Round(floorDb), Math.Round(floorDb + AutoRangeDb));
        }
        return _rangeB;
    }
    public ObservableCollection<Choice> NoiseBlankers { get; } = [];
    public ObservableCollection<Choice> NoiseReductions { get; } = [];

    public RxMode Mode { get; private set; } = RxMode.USB;
    public int StepHz => _settings.TuneStepHz;
    public string StepLabel => StepHz >= 1000 ? $"{StepHz / 1000}k" : $"{StepHz}";
    // The frequency in three pieces, so the digit the step moves can be yellow.
    private int StepDigitIndex
    {
        get
        {
            int fromRight = _hoverDigitPlace >= 0 ? _hoverDigitPlace : (int)Math.Floor(Math.Log10(StepHz));
            var text = FrequencyText;
            for (int i = text.Length - 1; i >= 0; i--)
                if (char.IsDigit(text[i]) && fromRight-- == 0) return i;
            return -1;
        }
    }
    // The digit under the pointer, as a power of ten (0 = Hz), or -1. It takes
    // the highlight while the pointer is there, and the wheel tunes that place.
    private int _hoverDigitPlace = -1;
    public int HoverDigitPlace
    {
        get => _hoverDigitPlace;
        set
        {
            if (_hoverDigitPlace == value) return;
            _hoverDigitPlace = value;
            NotifyFrequencyDigits();
        }
    }

    public string FrequencyHead => StepDigitIndex < 0 ? FrequencyText : FrequencyText[..StepDigitIndex];
    public string FrequencyDigit => StepDigitIndex < 0 ? "" : FrequencyText[StepDigitIndex].ToString();
    public string FrequencyTail => StepDigitIndex < 0 ? "" : FrequencyText[(StepDigitIndex + 1)..];

    partial void OnVfoHzChanged(long value)
    {
        NotifyFrequencyDigits();
        RefreshSwlStation();
    }

    private void NotifyFrequencyDigits()
    {
        OnPropertyChanged(nameof(FrequencyHead));
        OnPropertyChanged(nameof(FrequencyDigit));
        OnPropertyChanged(nameof(FrequencyTail));
    }

    public string FrequencyText => VfoHz.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', '.');
    public string AfGainText => $"{AfGainDb:0} dB";
    // Thetis-style front-end steps: the G2 has one preamp switch, so the
    // other steps are step-attenuator presets.
    private static readonly int[] PreAttenSteps = [0, 10, 20, 30];

    /// <summary>Second line of the Pre button: "on", "0 dB", or the attenuation.</summary>
    public string PreLabel => PreampOn ? "on" : AttenDb < 0.5 ? "0 dB" : $"−{AttenDb:0}";

    /// <summary>preamp on → 0 dB → −10 → −20 → −30 → preamp on. From an
    /// in-between attenuation, go to the next step down.</summary>
    [RelayCommand]
    private void CyclePre()
    {
        if (!IsConnected) return;
        if (PreampOn)
        {
            PreampOn = false;
            AttenDb = 0;
            return;
        }
        int att = (int)Math.Round(AttenDb);
        int next = PreAttenSteps.FirstOrDefault(s => s > att, -1);
        if (next < 0)
        {
            AttenDb = 0;
            PreampOn = true;
        }
        else
        {
            AttenDb = next;
        }
    }

    public string AttenText => AttOffsetDb > 0 ? $"{AttenDb:0}+{AttOffsetDb} dB" : $"{AttenDb:0} dB";
    /// <summary>What the RF button shows.</summary>
    public string RfText => PreampOn ? "pre on" : AttenText;

    /// <summary>Set by the window: shows Config beside the main window, and
    /// completes when it closes. The radio stays usable meanwhile.</summary>
    public Func<SettingsViewModel, Task>? ShowSettings { get; set; }
    /// <summary>Set by the window: closes Config if it's open.</summary>
    public Action? CloseSettings { get; set; }
    private bool _settingsOpen;
    /// <summary>Set by the window: brings an open Config forward.</summary>
    public Action? FocusSettings { get; set; }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenSettingsAsync()
    {
        if (_settingsOpen)
        {
            FocusSettings?.Invoke();
            return;
        }
        if (_client is null || ShowSettings is null) return;
        var settings = new SettingsViewModel(() => _client!)
        {
            MeasuredTxPeak = MeasuredTxPeak,
            AppSettings = _settings,
            RestartEngine = RestartEngineAsync,
            FollowRxFilterNow = () => FollowRxFilterAsync(),
        };
        settings.PaMaxPowerSaved += SetPowerScale;
        settings.AudioSaved += () => _ = LoadMicSourcesAsync();
        _ = settings.LoadAsync();
        _settingsOpen = true;
        try { await ShowSettings(settings); }
        finally { _settingsOpen = false; }
        if (IsConnected) await LoadAntennasAsync();
    }

    /// <summary>Set by the window: shows the radio picker and returns the choice.</summary>
    public Func<RadioPickerViewModel, Task<RadioChoice?>>? ShowRadioPicker { get; set; }
    public Func<DspPrepViewModel, Task>? ShowDspPrep { get; set; }
    public (double Width, double Height) MiniSize => (_settings.MiniWidth, _settings.MiniHeight);
    public (int X, int Y)? MiniPosition =>
        _settings.MiniX is int x && _settings.MiniY is int y ? (x, y) : null;

    public void RememberMiniWindow(double width, double height, int x, int y)
    {
        (_settings.MiniWidth, _settings.MiniHeight) = (width, height);
        (_settings.MiniX, _settings.MiniY) = (x, y);
        _settings.Save();
    }
    /// <summary>Plan names and the one preselected; the index chosen.</summary>
    public Func<IReadOnlyList<string>, int, Task<int>>? ShowFirstStart { get; set; }
    /// <summary>Radio, reason, log path; true to restart the engine.</summary>
    public Func<string, string, string, Task<bool>>? ShowEngineStopped { get; set; }
    public event Action<DisplayFrame>? Display;
    public event Action? DisplayReset;

    public MainViewModel()
    {
        _vfoSender = new Latest<long>(hz => _client!.SetVfoAsync(hz, ActiveRx));
        // Main AF is the station-wide master; each slice has its own trim on top.
        _afSender = new Latest<double>(db => _client!.SetAfGainAsync(db));
        _sliceAfSenders = [.. Enumerable.Range(0, 2).Select(rx => new Latest<double>(
            db => _client!.SetReceiverAsync(rx, new ReceiverSetRequest(AfGainDb: db))))];
        _attSender = new Latest<int>(db => _client!.SetAttenuatorAsync(db));

        foreach (var b in SimpleHPSDR.Band.All)
            Bands.Add(new Choice(b.Name, b, c => _ = SelectBandAsync((Band)c.Value)));
        foreach (var m in ModeOrder)
            Modes.Add(new Choice(m.ToString(), m, c => _ = SelectModeAsync((RxMode)c.Value)));
        Modes.Add(new Choice(SwlChoice, SwlChoice, c => _ = SelectSwlAsync()));
        if (DrmNative.Available) Modes.Add(new Choice(DrmChoice, DrmChoice, c => _ = SelectDrmAsync()));
        foreach (var s in Steps)
            TuneSteps.Add(new Choice(s >= 1000 ? $"{s / 1000}k" : $"{s}", s, c => SelectStep((int)c.Value)));
        foreach (var (label, mode) in new[] { ("Off", NbMode.Off), ("NB1", NbMode.Nb1), ("NB2", NbMode.Nb2) })
            NoiseBlankers.Add(new Choice(label, mode, c => _ = SelectNbAsync((NbMode)c.Value)));
        // Thetis numbering: NR1 LMS, NR2 Ephraim-Malah, NR3 RNNoise, NR4
        // libspecbleach; NNR is WDSP's neural reduction.
        foreach (var (label, mode) in new[]
                 {
                     ("Off", NrMode.Off), ("NR1", NrMode.Anr), ("NR2", NrMode.Emnr), ("NR3", NrMode.Rnnr),
                     ("NR4", NrMode.Sbnr), ("NNR", NrMode.Nnr),
                 })
            NoiseReductions.Add(new Choice(label, mode, c => _ = SelectNrAsync((NrMode)c.Value))
            {
                Params = mode == NrMode.Off ? null : NoisePanelFor(mode),
            });
        WireNoiseButtons();
        Mark(TuneSteps, _settings.TuneStepHz);
        LoadCwDecoderChoice();
        _specMinDb = _settings.SpecMinDb;
        _specMaxDb = _settings.SpecMaxDb;
        _autoSpectrum = _settings.AutoSpectrum;
        _panFraction = _settings.PanFraction;
    }

    // ---- startup and connection ---------------------------------------

    public async Task StartAsync()
    {
        StartClock();
        _engine.ComputerAudio = !_settings.AudioToRadio;
        _engine.Stopped += code => Dispatcher.UIThread.Post(() => _ = OnEngineStoppedAsync(code));
        try
        {
            await _engine.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            return;
        }

        StartCpuMonitor();
        _statePoll.Tick += async (_, _) => await PollStateAsync();
        CreateClient();
        await PrepareDspAsync();
        await ChooseBandPlanAsync();

        if (_settings is { AutoConnect: true, LastRadioIp: not null })
        {
            Status = "Looking for your radio…";
            RadioInfo[] found = [];
            try { found = await _client!.DiscoverAsync(); } catch (HttpRequestException) { }
            var match = found.Select(r => new RadioEntry(r)).FirstOrDefault(r =>
                (r.Mac == _settings.LastRadioMac || r.Info.IpAddress == _settings.LastRadioIp) && !r.Busy);
            if (match is not null && await ConnectAsync(match.ToChoice()))
                return;
        }
        await ChooseRadioAsync();
    }

    /// <summary>First start: the user picks a band plan, which the engine then keeps.</summary>
    private async Task ChooseBandPlanAsync()
    {
        if (_settings.BandPlanDefaulted || ShowFirstStart is null) return;
        try
        {
            var regions = await _client!.GetBandRegionsAsync();
            int preselected = Math.Max(0, regions.FindIndex(r => r.Id == DefaultBandPlan));
            // The engine starts on Region 1 when nobody has chosen. Anything else
            // was chosen on purpose, so offer that.
            var current = await _client.GetBandRegionAsync();
            if (current != UnchosenBandPlan) preselected = Math.Max(preselected, regions.FindIndex(r => r.Id == current));
            int chosen = await ShowFirstStart([.. regions.Select(r => r.DisplayName), GeneralCoverageName], preselected);
            _settings.GeneralCoverage = chosen == regions.Count;
            // General Coverage still leaves the engine a plan for PA calibration.
            var id = regions[_settings.GeneralCoverage ? preselected : Math.Clamp(chosen, 0, regions.Count - 1)].Id;
            if (id != current) await _client.SetBandRegionAsync(id);
            _settings.BandPlanDefaulted = true;
            _settings.Save();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException)
        {
            Status = $"Band plan not set: {ex.Message}";
        }
    }

    /// <summary>Show the engine's first-start preparation, if it's still going.</summary>
    private async Task PrepareDspAsync()
    {
        if (ShowDspPrep is null) return;
        var prep = new DspPrepViewModel(_client!);
        if (!await prep.NeededAsync()) return;
        Status = "Preparing DSP (first start only)…";
        await ShowDspPrep(prep);
    }

    /// <summary>A client for the running engine, with all stream handlers.</summary>
    private void CreateClient()
    {
        _client = new StationClient(_engine.BaseUri);
        _client.Display += QueueFrame;
        _client.SignalDbm += (rx, dbm) =>
        {
            if (rx != ActiveRx) return;
            _latestDbm = dbm;
            if (Interlocked.Exchange(ref _meterScheduled, 1) == 0)
                Dispatcher.UIThread.Post(() =>
                {
                    _meterScheduled = 0;
                    UpdateMeter(_latestDbm);
                });
        };
        WireTransmit(_client);
        WirePureSignal(_client);
        WireMonitor(_client);
        WireAlerts(_client);
        _client.Vfo += v => Dispatcher.UIThread.Post(() =>
        {
            if (v.Receiver == ActiveRx) { if (!_vfoSender.Busy) SetVfoLocal(v.VfoHz); }
            else if (v.Receiver == 0 && _sliceA is { } a) _sliceA = a with { VfoHz = v.VfoHz };
            else if (v.Receiver == 1 && _sliceB is { } b) _sliceB = b with { VfoHz = v.VfoHz };
        });
        _client.CwStatus += f => Dispatcher.UIThread.Post(() => OnCwStatus(f));
        _client.CwDecoded += f => Dispatcher.UIThread.Post(() => OnCwDecoded(f));
        _client.CwSettings += s => Dispatcher.UIThread.Post(() => ShowCwSettings(s));
        _client.StreamClosed += reason => Dispatcher.UIThread.Post(() => _ = OnStreamClosedAsync(reason));
    }

    /// <summary>Keep the engine's speaker output in step with the app's choice,
    /// and say so if this radio can't honour it, as nothing would be heard.</summary>
    private async Task ApplyRadioSpeakerAsync(StationClient client)
    {
        try
        {
            if (_settings.AudioToRadio && !await client.GetRadioSpeakerAvailableAsync())
            {
                // Nothing would be heard, so go back to the computer.
                _settings.AudioToRadio = false;
                _settings.Save();
                await client.SetRadioSpeakerAsync(false);
                Dispatcher.UIThread.Post(() => _ = RestartEngineAsync());
                Status = "This radio has no speaker jack, so audio is back on the computer.";
                return;
            }
            await client.SetRadioSpeakerAsync(_settings.AudioToRadio);
        }
        catch (Exception ex) { Status = $"Radio speaker: {ex.Message}"; }
    }

    private bool _lostRadio;

    // The stream carries the meters and the display. Without it nobody can see
    // what the radio is doing, and the engine only drops MOX for us, not TUNE
    // or 2-TONE, so unkey at once. If the engine itself died, its handler
    // takes over; give that a moment to show.
    private async Task OnStreamClosedAsync(string reason)
    {
        if (!IsConnected || _disposed != 0) return;
        _lostRadio = true;
        await UnkeyAsync();
        ClearKeyButtons();
        Status = $"Lost the engine stream, so transmit was turned off: {reason}";
        await Task.Delay(2000);
        if (!IsConnected || _engine.HasExited) return;
        await DisconnectAsync();
        Status = "Lost the engine stream, so transmit was turned off. Choose a radio to reconnect.";
    }

    // Only asks while on a radio: otherwise (the firmware window included) a
    // restart must not reconnect by itself, and Choose radio restarts it anyway.
    private async Task OnEngineStoppedAsync(int exitCode)
    {
        if (_disposed != 0) return;
        Status = "The radio engine stopped.";
        if (!IsConnected && !_lostRadio) return;
        _lostRadio = false;
        var radio = _radio?.Label ?? RadioLabel;
        await DisconnectAsync();
        Status = "The radio engine stopped.";
        var reason = EngineHost.LastFatalLine() ?? $"It exited with code {exitCode}.";
        if (ShowEngineStopped is { } ask && await ask(radio, reason, EngineHost.LogPath))
            await RestartEngineAsync();
    }

    /// <summary>
    /// Restart the engine and reconnect to the same radio, so settings the
    /// engine only reads at startup (TCI and network CAT servers) take effect.
    /// Unkeys and disconnects cleanly first.
    /// </summary>
    public async Task RestartEngineAsync()
    {
        var radio = _radio;
        // A new engine starts from what it last saved, so carry these across.
        // Connecting makes slice A active, so it's slice A's dial that's kept.
        long hz = ActiveRx == 0 ? VfoHz : _sliceA?.VfoHz ?? VfoHz;
        var (nb, anf) = (NoiseBlankers.FirstOrDefault(c => c.IsSelected)?.Value as NbMode?, AnfOn);
        Status = "Restarting the engine…";
        if (!await StartEngineAgainAsync()) return;
        if (radio is null)
        {
            Status = "Engine restarted. Choose a radio.";
            return;
        }
        if (!await ConnectAsync(radio)) return;
        TuneTo(hz);
        await UpdateNrAsync(nr => nr with { NbMode = nb ?? nr.NbMode, AnfEnabled = anf });
    }

    /// <summary>Stop the engine if it's running and start a new one. Whatever
    /// happens, Choose radio can try again.</summary>
    private async Task<bool> StartEngineAgainAsync()
    {
        await DisconnectAsync();
        _client?.Dispose();
        _client = null;
        _engine.Dispose();
        _engine.ComputerAudio = !_settings.AudioToRadio;
        try
        {
            await _engine.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Status = $"Engine did not restart: {ex.Message}";
            return false;
        }
        CreateClient();
        return true;
    }

    [RelayCommand]
    private async Task ChooseRadioAsync()
    {
        // Config talks to the connection this is about to replace.
        CloseSettings?.Invoke();
        if (ShowRadioPicker is null) return;
        if ((_client is null || _engine.HasExited) && !await StartEngineAgainAsync()) return;
        if (_client is null) return;
        if (IsConnected) await DisconnectAsync();

        var picker = new RadioPickerViewModel(_client, _settings.AutoConnect);
        _ = picker.RefreshAsync();
        var choice = await ShowRadioPicker(picker);
        _settings.AutoConnect = picker.AutoConnect;
        _settings.Save();
        if (choice is null)
        {
            Status = "Not connected. Choose a radio to start.";
            return;
        }
        await ConnectAsync(choice);
    }

    private async Task<bool> ConnectAsync(RadioChoice radio)
    {
        var client = _client!;
        Status = $"Connecting to {radio.Label}…";
        var deadline = DateTime.UtcNow.AddMinutes(3);
        var preparingDeadline = DateTime.UtcNow.AddMinutes(30);   // far beyond the slowest machine
        while (true)
        {
            try
            {
                if (radio.TakeOver)
                {
                    Status = $"Taking over {radio.Label}…";
                    await client.ReclaimAsync(radio.Ip, radio.Protocol2, radio.BoardId, _settings.SampleRate, CancellationToken.None);
                }
                // P2 reclaim already connected; P1 reclaim only freed the radio.
                if (!(radio.TakeOver && radio.Protocol2))
                    await client.ConnectAsync(radio.Ip, radio.Protocol2, radio.BoardId, _settings.SampleRate, CancellationToken.None);
                break;
            }
            catch (StationException ex) when (ex.Status == HttpStatusCode.ServiceUnavailable)
            {
                // The engine bakes FFTW plans on first run; connect waits for it,
                // however long this machine takes. Any other refusal gets 3 minutes.
                var prep = await client.GetDspPrepAsync();
                if (DateTime.UtcNow >= (prep is { Building: true } ? preparingDeadline : deadline))
                {
                    Status = $"Could not connect to {radio.Label}: {ex.Message}";
                    return false;
                }
                Status = DspPrepViewModel.StatusLine(prep?.Status ?? "");
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                Status = $"Could not connect to {radio.Label}: {ex.Message}";
                return false;
            }
        }

        // The radio is ours from here: a failure must let it go again, not
        // leave it connected with nothing showing.
        try
        {
            _radio = radio with { TakeOver = false };
            _settings.LastRadioIp = radio.Ip;
            _settings.LastRadioMac = radio.Mac;
            _settings.LastRadioP2 = radio.Protocol2;
            _settings.LastRadioBoardId = radio.BoardId;
            _settings.Save();

            // The engine's guard only runs when the user chose Block transmit.
            await Try(() => client.SetBandPlanGuardIgnoredAsync(_settings.GeneralCoverage || !_settings.BandPlanBlocks));
            var state = await client.GetStateAsync();
            bool firstFilters = await ApplyFilterDefaultsAsync();
            await ApplyStateAsync(state);
            ActiveRx = 0;
            UpdateSlices(state);
            LoadSpanRates(radio.Protocol2, state.SampleRate);
            await LoadDisplayTuningAsync();
            await LoadAntennasAsync();
            await LoadTransmitAsync(state);
            LoadPureSignal(state);
            await LoadMonitorAsync();
            await LoadNotchesAsync();
            if (firstFilters && Filters.FirstOrDefault(f => f.SlotName == DefaultFilterSlot) is { } start)
                await SelectFilterAsync(start);
            await client.OpenStreamAsync();
            await LoadCwAsync();
            if (CwDecoderOn) await client.SetCwDecoderAsync(true);
            await ApplyRadioSpeakerAsync(client);
            IsConnected = true;
            _lostRadio = false;
            _statePoll.Start();
            RadioLabel = radio.Label;
            Status = $"Connected to {radio.Label}";
            return true;
        }
        catch (Exception ex)
        {
            await UnkeyAsync();
            try { await client.DisconnectAsync(radio.Protocol2); } catch (Exception) { }
            IsConnected = false;
            _statePoll.Stop();
            RadioLabel = "No radio";
            DisplayReset?.Invoke();
            Status = $"Could not finish connecting to {radio.Label}: {ex.Message}";
            return false;
        }
    }

    private async Task DisconnectAsync()
    {
        VoxOn = false;   // never live on a radio you've just reconnected to
        TurnDrmOff();
        EndPlacingNotch();
        if (_client is null || _radio is null) return;
        await UnkeyAsync();
        try { await _client.DisconnectAsync(_radio.Protocol2); } catch (Exception) { }
        IsConnected = false;
        _statePoll.Stop();
        RadioLabel = "No radio";
        (SignalDbm, PeakDbm, SUnits) = (-140, -140, "S0");
        DisplayReset?.Invoke();
    }

    private async Task ApplyStateAsync(StateDto s)
    {
        _applyingState = true;
        try
        {
            SetVfoLocal(s.VfoHz);
            FilterLowHz = s.FilterLowHz;
            FilterHighHz = s.FilterHighHz;
            AfGainDb = s.RxAfGainDb;
            AttenDb = s.AttenDb;
            PreampOn = s.PreampOn;
            AutoAtt = s.AutoAttEnabled;
            Mark(NoiseBlankers, s.Nr?.NbMode ?? NbMode.Off);
            Mark(NoiseReductions, s.Nr?.NrMode ?? NrMode.Off);
            AnfOn = s.Nr?.AnfEnabled ?? false;
            AttOffsetDb = s.AttOffsetDb;
            AdcOverload = s.AdcOverloadWarning;
            if (!DrmActive) Muted = s.Rx1Muted;
            ZoomLevel = s.ZoomLevel;
            CtunOn = s.CtunEnabled;
            await SetModeLocalAsync(s.Mode, s.FilterPresetName);
        }
        finally
        {
            _applyingState = false;
        }
    }

    // ---- tuning ---------------------------------------------------------

    private void SetVfoLocal(long hz)
    {
        VfoHz = hz;
        var band = SimpleHPSDR.Band.Find(hz);
        foreach (var c in Bands) c.IsSelected = ReferenceEquals(c.Value, band);
        OnPropertyChanged(nameof(BandLabel));
        ShowAntennasForBand();
    }

    /// <summary>Text of the blue Band button: the band the VFO is in, or
    /// "GEN" (general coverage) outside the ham bands.</summary>
    public string BandLabel => Bands.FirstOrDefault(c => c.IsSelected) is { } b ? $"{b.Label} m" : "GEN";

    /// <summary>Text of the blue Mode button.</summary>
    public string ModeLabel => Mode.ToString();

    private const long MinTuneHz = 10_000, MaxTuneHz = 60_000_000;

    public void TuneTo(long hz)
    {
        if (!IsConnected) return;
        if (VfoLocked)
        {
            Status = "VFO is locked.";
            return;
        }
        hz = Math.Clamp(hz, MinTuneHz, MaxTuneHz);
        SetVfoLocal(hz);
        if (SimpleHPSDR.Band.Find(hz) is { } band) _settings.BandMemory[band.Name] = hz;
        _vfoSender.Send(hz);
    }

    /// <summary>Wheel tuning: snap to the step grid, then move by whole steps.</summary>
    public void TuneBySteps(int steps)
    {
        long step = StepHz;
        long floor = VfoHz - VfoHz % step;
        TuneTo(floor == VfoHz ? VfoHz + steps * step
            : steps > 0 ? floor + steps * step
            : floor + (steps + 1) * step);
    }

    /// <summary>Wheel over one VFO digit: move that digit, leaving the ones
    /// below it alone.</summary>
    public void TuneDigit(int place, int steps)
    {
        if (steps == 0 || place is < 0 or > 7) return;
        long unit = (long)Math.Pow(10, place);
        long hz = VfoHz + steps * unit;
        if (hz < MinTuneHz || hz > MaxTuneHz) return;
        TuneTo(hz);
    }

    public bool SnapClickToStep => _settings.SnapClickToStep;

    public void ToggleSnapClickToStep()
    {
        _settings.SnapClickToStep = !_settings.SnapClickToStep;
        _settings.Save();
        OnPropertyChanged(nameof(SnapClickToStep));
        Status = _settings.SnapClickToStep
            ? $"Panadapter clicks snap to the {StepLabel} step."
            : "Panadapter clicks tune to the exact frequency.";
    }

    /// <summary>A panadapter click's frequency, rounded to the step when snapping is on.</summary>
    public long ClickFrequency(long hz)
    {
        if (!_settings.SnapClickToStep || StepHz <= 1) return hz;
        long step = StepHz;
        return (long)Math.Round(hz / (double)step, MidpointRounding.AwayFromZero) * step;
    }

    public void BeginFrequencyEntry()
    {
        if (!IsConnected) return;
        HoverDigitPlace = -1;   // the readout hides, so it never sees the pointer leave
        FrequencyEntry = "";
        EditingFrequency = true;
    }

    public void CancelFrequencyEntry()
    {
        EditingFrequency = false;
        FrequencyEntry = "";
    }

    [RelayCommand]
    private void EnterFrequency()
    {
        // Accept kHz ("14074", "7074.5") or MHz ("14.074").
        var text = FrequencyEntry.Trim().Replace(',', '.');
        if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0)
        {
            long hz = (long)Math.Round(v < 100 ? v * 1e6 : v * 1e3);
            if (hz is >= MinTuneHz and <= MaxTuneHz) TuneTo(hz);
            else Status = "Enter a frequency from 0.01 to 60 MHz.";
        }
        CancelFrequencyEntry();
    }

    private void SelectStep(int hz)
    {
        _settings.TuneStepHz = hz;
        _settings.Save();
        Mark(TuneSteps, hz);
        OnPropertyChanged(nameof(StepHz));
        NotifyFrequencyDigits();
        OnPropertyChanged(nameof(StepLabel));
    }

    private void NotifyZoomSpan()
    {
        OnPropertyChanged(nameof(ZoomLabel));
        OnPropertyChanged(nameof(ZoomSpanText));
    }

    /// <summary>Full span, or back to the last zoomed span.</summary>
    [RelayCommand]
    private void ToggleZoom() => SetZoom(ZoomOn ? 1 : Math.Clamp(_settings.ZoomOnLevel, MinZoomOnLevel, MaxZoomLevel));

    private void SetZoom(int level)
    {
        if (!IsConnected) return;
        if (level > 1 && level != _settings.ZoomOnLevel)
        {
            _settings.ZoomOnLevel = level;
            _settings.Save();
        }
        ZoomLevel = level;
        (_zoomSender ??= new Latest<int>(async z =>
        {
            await _client!.SetZoomAsync(z);
            await SyncSliceBZoomAsync();
            await CenterOnDialAsync();
        })).Send(level);
    }

    [RelayCommand]
    private void ToggleCtun() => CtunOn = !CtunOn;

    partial void OnCtunOnChanged(bool value)
    {
        if (!_applyingState && IsConnected) _ = Try(() => _client!.SetCtunAsync(value));
    }

    // ---- mode and filter ------------------------------------------------

    private async Task ChangeModeAsync(RxMode mode)
    {
        if (!IsConnected) return;
        await Try(() => _client!.SetModeAsync(mode, ActiveRx));
        var s = await _client!.GetStateAsync();
        UpdateSlices(s);
        var a = ActiveOf(s);
        FilterLowHz = a.Low;
        FilterHighHz = a.High;
        await SetModeLocalAsync(a.Mode, a.Preset);
        await Try(() => MirrorFilterAsync(a.Low, a.High, a.Preset));
        await Try(() => FollowRxFilterAsync());
    }

    private async Task SetModeLocalAsync(RxMode mode, string? presetName)
    {
        Mode = mode;
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(ModeIsAm));
        OnPropertyChanged(nameof(ModeIsFm));
        OnPropertyChanged(nameof(ModeIsCw));
        if (!ModeIsCw) ReleaseCwKeys();
        OnPropertyChanged(nameof(ModeHasNoOptions));
        Mark(Modes, mode);
        OnPropertyChanged(nameof(ModeLabel));

        await LoadFilterSlotsAsync(mode, presetName);
    }

    private async Task LoadFilterSlotsAsync(RxMode mode, string? selectedSlot)
    {
        FilterPreset[] presets = [];
        try { presets = await _client!.GetFilterPresetsAsync(mode); } catch (Exception) { }
        Filters.Clear();
        foreach (var p in presets.Where(p => FilterSlotNames.Contains(p.SlotName)))
            Filters.Add(new FilterSlot(mode, p, SelectFilterAsync, SaveFilterAsync, ResetFilterAsync)
            {
                IsSelected = p.SlotName == selectedSlot,
            });
    }

    private async Task SelectFilterAsync(FilterSlot slot)
    {
        foreach (var f in Filters) f.IsSelected = ReferenceEquals(f, slot);
        var p = slot.Preset;
        FilterLowHz = p.LowHz;
        FilterHighHz = p.HighHz;
        await Try(async () =>
        {
            await _client!.SetFilterAsync(p.LowHz, p.HighHz, p.SlotName, ActiveRx);
            await MirrorFilterAsync(p.LowHz, p.HighHz, p.SlotName);
            await FollowRxFilterAsync();
        });
    }

    private Latest<(int Low, int High)>? _filterDragSender;

    /// <summary>A passband edge dragged on the filter scope: a custom filter
    /// for the active slice (no slot lit), shared and followed as usual.</summary>
    public void DragFilter(int low, int high)
    {
        if (!IsConnected) return;
        FilterLowHz = low;
        FilterHighHz = high;
        foreach (var f in Filters) f.IsSelected = false;
        _filterDragSender ??= new Latest<(int Low, int High)>(async e =>
        {
            await _client!.SetFilterAsync(e.Low, e.High, null, ActiveRx);
            await MirrorFilterAsync(e.Low, e.High, null);
            await FollowRxFilterAsync();
        });
        _filterDragSender.Send((low, high));
    }

    /// <summary>Store an edited slot in the engine (per mode); if it's the
    /// filter in use, apply the new edges at once.</summary>
    private async Task SaveFilterAsync(FilterSlot slot)
    {
        var (lo, hi) = slot.EngineEdges;
        var label = string.IsNullOrWhiteSpace(slot.EditLabel) ? slot.Label : slot.EditLabel.Trim();
        if (hi - lo < 50)
        {
            Status = "Filter not saved: it must be at least 50 Hz wide.";
            return;
        }
        await Try(async () =>
        {
            await _client!.WriteFilterPresetAsync(slot.Mode, slot.SlotName, lo, hi, label);
            await AfterFilterEditAsync(slot);
            Status = $"Filter {slot.SlotName} saved as \"{label}\" ({slot.EditLowHz:0}-{slot.EditHighHz:0} Hz).";
        });
    }

    // The default SSB buttons as lower-sideband edges; USB is the mirror image.
    // The engine's own defaults stand for every other mode.
    private static readonly (string Slot, string Label, int Low, int High)[] DefaultSsbFilters =
    [
        ("F1", "5.0k", -5000, -63), ("F2", "4k", -4000, -63), ("F3", "3.5k", -3500, -63),
        ("F4", "3k", -3000, -80), ("F5", "2.8k", -2800, -100),
    ];
    private const string DefaultFilterSlot = "F2";
    public const string GeneralCoverageName = "General Coverage (no restrictions)";
    private const string DefaultBandPlan = "IARU_R2", UnchosenBandPlan = "IARU_R1";

    // AM and SAM; the widths are the whole passband. Raise the version when
    // these change so existing installs get them.
    private const int AmFilterDefaultsVersion = 2;
    private static readonly (string Slot, string Label, int Low, int High)[] DefaultAmFilters =
    [
        ("F1", "20k", -10000, 10000), ("F2", "18k", -9000, 9000), ("F3", "10k", -5000, 5000),
        ("F4", "8k", -4000, 4000), ("F5", "6k", -3000, 3000),
    ];

    private Task WriteDefaultFilterAsync(RxMode mode, (string Slot, string Label, int Low, int High) f) =>
        mode == RxMode.LSB
            ? _client!.WriteFilterPresetAsync(mode, f.Slot, f.Low, f.High, f.Label)
            : _client!.WriteFilterPresetAsync(mode, f.Slot, -f.High, -f.Low, f.Label);

    /// <summary>Once per install. True if it ran, so the start filter gets chosen.</summary>
    private async Task<bool> ApplyFilterDefaultsAsync()
    {
        if (_settings.AmFilterDefaultsVersion < AmFilterDefaultsVersion)
            await Try(async () =>
            {
                foreach (var mode in new[] { RxMode.AM, RxMode.SAM })
                    foreach (var f in DefaultAmFilters)
                        await _client!.WriteFilterPresetAsync(mode, f.Slot, f.Low, f.High, f.Label);
                _settings.AmFilterDefaultsVersion = AmFilterDefaultsVersion;
                _settings.Save();
            });
        if (_settings.FilterDefaultsApplied) return false;
        await Try(async () =>
        {
            foreach (var mode in new[] { RxMode.LSB, RxMode.USB })
                foreach (var f in DefaultSsbFilters)
                    await WriteDefaultFilterAsync(mode, f);
            _settings.FilterDefaultsApplied = true;
            _settings.Save();
        });
        return _settings.FilterDefaultsApplied;
    }

    private async Task ResetFilterAsync(FilterSlot slot)
    {
        await Try(async () =>
        {
            if (slot.Mode is RxMode.LSB or RxMode.USB && DefaultSsbFilters.Any(f => f.Slot == slot.SlotName))
                await WriteDefaultFilterAsync(slot.Mode, DefaultSsbFilters.First(f => f.Slot == slot.SlotName));
            else if (slot.Mode is RxMode.AM or RxMode.SAM && DefaultAmFilters.Any(f => f.Slot == slot.SlotName))
            {
                var f = DefaultAmFilters.First(d => d.Slot == slot.SlotName);
                await _client!.WriteFilterPresetAsync(slot.Mode, f.Slot, f.Low, f.High, f.Label);
            }
            else
                await _client!.ResetFilterPresetAsync(slot.Mode, slot.SlotName);
            await AfterFilterEditAsync(slot);
            Status = $"Filter {slot.SlotName} reset to its default.";
        });
    }

    private async Task AfterFilterEditAsync(FilterSlot slot)
    {
        string? selected = Filters.FirstOrDefault(f => f.IsSelected)?.SlotName;
        await LoadFilterSlotsAsync(slot.Mode, selected);
        if (selected == slot.SlotName && Filters.FirstOrDefault(f => f.SlotName == selected) is { } now)
            await SelectFilterAsync(now);
    }

    // ---- S-meter trim ---------------------------------------------------

    private Latest<double>? _trimSender;
    private bool _applyingTrim;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SMeterTrimText))]
    private double _sMeterTrimDb;
    public string SMeterTrimText => $"{SMeterTrimDb:+0.0;-0.0;0.0} dB";

    /// <summary>Right-click on the meter: read the trim the engine has.</summary>
    public async Task LoadSMeterTrimAsync()
    {
        if (!IsConnected) return;
        await Try(async () =>
        {
            double db = await _client!.GetSMeterTrimAsync();
            _applyingTrim = true;
            SMeterTrimDb = db;
            _applyingTrim = false;
        });
    }

    partial void OnSMeterTrimDbChanged(double value)
    {
        if (_applyingTrim || !IsConnected) return;
        _trimSender ??= new Latest<double>(db => _client!.SetSMeterTrimAsync(db));
        _trimSender.Send(Math.Round(value * 2) / 2);
    }

    [RelayCommand]
    private void ResetSMeterTrim() => SMeterTrimDb = 0;

    // ---- audio and front end -------------------------------------------

    partial void OnAfGainDbChanged(double value)
    {
        UpdateDrmVolume();
        OnPropertyChanged(nameof(AfGainText));
        OnPropertyChanged(nameof(AfTip));
        if (!_applyingState && IsConnected) _afSender.Send(Math.Round(value));
    }

    partial void OnAttenDbChanged(double value)
    {
        OnPropertyChanged(nameof(AttenText));
        OnPropertyChanged(nameof(PreLabel));
        OnPropertyChanged(nameof(RfText));
        if (!_applyingState && IsConnected) _attSender.Send((int)Math.Round(value));
    }

    private Task SelectNbAsync(NbMode mode)
    {
        Mark(NoiseBlankers, mode);
        return UpdateNrAsync(nr => nr with { NbMode = mode });
    }

    private Task SelectNrAsync(NrMode mode)
    {
        Mark(NoiseReductions, mode);
        return UpdateNrAsync(nr => nr with { NrMode = mode });
    }

    // The engine takes the whole NR/NB config, so read it, change one field,
    // and send it back. If the engine refuses (e.g. NR3 without a model), show
    // what it actually applied.
    private async Task UpdateNrAsync(Func<NrConfig, NrConfig> change)
    {
        if (!IsConnected) return;
        await Try(async () =>
        {
            var nr = (await _client!.GetStateAsync()).Nr ?? new NrConfig();
            await SendNrAsync(change(nr));
        });
        await Try(async () =>
        {
            var applied = (await _client!.GetStateAsync()).Nr ?? new NrConfig();
            Mark(NoiseBlankers, applied.NbMode);
            Mark(NoiseReductions, applied.NrMode);
            AnfOn = applied.AnfEnabled;
        });
    }

    [RelayCommand]
    private Task ToggleAnfAsync() => UpdateNrAsync(nr => nr with { AnfEnabled = !AnfOn });

    partial void OnAutoAttChanged(bool value)
    {
        if (!_applyingState && IsConnected) _ = Try(() => _client!.SetAutoAttAsync(value));
    }

    partial void OnPreampOnChanged(bool value)
    {
        if (!_applyingState && IsConnected) _ = Try(() => _client!.SetPreampAsync(value));
    }

    partial void OnMutedChanged(bool value)
    {
        if (DrmActive) UpdateDrmVolume();   // the engine stays muted for DRM
        else if (!_applyingState && IsConnected) _ = Try(() => _client!.SetMuteAsync(value));
    }

    // Display frames arrive at 30/s on the socket thread. Queue them and drain
    // in one UI pass, so a busy UI thread can't stack up separate dispatches
    // that then replay in a burst. The queue is bounded; on overflow the
    // oldest frame goes.
    private void QueueFrame(DisplayFrame f)
    {
        // RX1 feeds the main panadapter, RX2 slice B's own one; further
        // receivers aren't shown.
        if (f.RxId > 1) return;
        _frames.Enqueue(f);
        while (_frames.Count > 8 && _frames.TryDequeue(out _)) UiStats.FrameDropped();
        if (Interlocked.Exchange(ref _framesScheduled, 1) == 0)
            Dispatcher.UIThread.Post(DrainFrames, DispatcherPriority.Render);
    }

    private void DrainFrames()
    {
        _framesScheduled = 0;
        while (_frames.TryDequeue(out var f))
        {
            UiStats.FrameApplied();
            Display?.Invoke(f);
        }
    }

    // Auto-ATT, the overload flag and engine-side mode changes have no
    // frame of their own, so poll.
    private async Task PollStateAsync()
    {
        if (!IsConnected || _client is null) return;
        try
        {
            var s = await _client.GetStateAsync();
            ApplyDriveCeiling(s.DriveMaxPct);
            AttOffsetDb = s.AttOffsetDb;
            AdcOverload = s.AdcOverloadWarning;
            PollPureSignal(s);
            UpdateSlices(s);
            // The engine can change mode itself (it remembers a mode per
            // frequency range), so follow it, filters included.
            var a = ActiveOf(s);
            if (a.Mode != Mode)
            {
                FilterLowHz = a.Low;
                FilterHighHz = a.High;
                await SetModeLocalAsync(a.Mode, a.Preset);
            }
        }
        catch (Exception) { }
    }

    private void UpdateMeter(float dbm)
    {
        TrackSquelchMark(dbm);
        // Peak of the RMS reading, held 3 s, then it follows the signal again.
        var now = DateTime.UtcNow;
        if (dbm >= PeakDbm || now - _peakAt > PeakHold)
        {
            PeakDbm = dbm;
            _peakAt = now;
        }

        // Fast attack, slower release, like an analogue meter.
        double k = dbm > SignalDbm ? 0.6 : 0.2;
        dbm = (float)(SignalDbm + (dbm - SignalDbm) * k);
        SignalDbm = dbm;
        SUnits = ToSUnits(dbm);
    }

    // S9 = -73 dBm, 6 dB per S-unit below it.
    private static string ToSUnits(double dbm) => dbm >= -73
        ? $"S9+{Math.Max(0, Math.Round(dbm + 73)):0}"
        : $"S{Math.Clamp((int)Math.Round(9 + (dbm + 73) / 6), 0, 9)}";

    // ---- helpers --------------------------------------------------------

    private static void Mark(IEnumerable<Choice> choices, object value)
    {
        foreach (var c in choices) c.IsSelected = Equals(c.Value, value);
    }

    private async Task Try(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Status = ex.Message; }
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        RememberBandMode();
        _settings.Save();
        try { _statePoll.Stop(); } catch (InvalidOperationException) { }  // signal thread
        try { _clockTimer.Stop(); _cpuTimer.Stop(); } catch (InvalidOperationException) { }
        UnkeyForShutdown();
        if (_client is not null && _radio is not null)
        {
            var (client, p2) = (_client, _radio.Protocol2);
            try { Task.Run(() => client.DisconnectAsync(p2)).Wait(TimeSpan.FromSeconds(2)); }
            catch (Exception) { }
        }
        _keyer?.Dispose();
        if (DrmActive && _ctunBeforeDrm && _client is { } engine)   // the engine remembers it
            try { Task.Run(() => engine.SetCtunAsync(true)).Wait(TimeSpan.FromSeconds(1)); } catch (Exception) { }
        StopDrmDecoder();
        _client?.Dispose();
        _engine.Dispose();
    }
}

/// <summary>
/// Sends only the newest value: while one request is in flight, later values
/// replace each other, so fast wheel or slider moves never queue up.
/// </summary>
internal sealed class Latest<T>(Func<T, Task> send) where T : struct
{
    private T? _pending;
    public bool Busy { get; private set; }

    public async void Send(T value)
    {
        _pending = value;
        if (Busy) return;
        Busy = true;
        try
        {
            while (_pending is { } v)
            {
                _pending = null;
                try { await send(v); } catch (Exception) { }
            }
        }
        finally
        {
            Busy = false;
        }
        if (_pending is { } late) Send(late);   // arrived as the loop ended
    }
}
