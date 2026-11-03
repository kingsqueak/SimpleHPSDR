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
    private static readonly int[] Steps = [10, 100, 500, 1_000, 5_000, 10_000, 100_000];

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
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreLabel))]
    private bool _preampOn;
    [ObservableProperty] private bool _autoAtt;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AttenText))]
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
        SpanLabel = RateLabel(current);
    }

    private async Task SelectSpanAsync(int hz)
    {
        if (!IsConnected) return;
        await Try(async () =>
        {
            await _client!.SetSampleRateAsync(hz);
            Mark(SpanRates, hz);
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

    private async Task SelectResolutionAsync(int fft) => await Try(async () =>
    {
        await _client!.SetDisplayTuningAsync(fftSize: fft);
        Mark(Resolutions, fft);
        ResolutionLabel = $"{fft / 1024}k";
    });

    private async Task SelectSpeedAsync(int period) => await Try(async () =>
    {
        await _client!.SetDisplayTuningAsync(waterfallPeriod: period);
        Mark(WaterfallSpeeds, period);
        SpeedLabel = SpeedText(period);
    });

    // Panadapter zoom, cycled by the Zoom button. At 192 kHz, 16x is a 12 kHz span.
    private static readonly int[] ZoomLevels = [1, 2, 4, 8, 16];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ZoomLabel))]
    private int _zoomLevel = 1;
    public string ZoomLabel => $"{ZoomLevel}×";

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

    /// <summary>Set by the window: shows the settings dialog.</summary>
    public Func<SettingsViewModel, Task>? ShowSettings { get; set; }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (_client is null || ShowSettings is null) return;
        var settings = new SettingsViewModel(() => _client!)
        {
            MeasuredTxPeak = MeasuredTxPeak,
            AppSettings = _settings,
            RestartEngine = RestartEngineAsync,
        };
        settings.PaMaxPowerSaved += SetPowerScale;
        settings.AudioSaved += () => _ = LoadMicSourcesAsync();
        _ = settings.LoadAsync();
        await ShowSettings(settings);
        if (IsConnected) await LoadAntennasAsync();
    }

    /// <summary>Set by the window: shows the radio picker and returns the choice.</summary>
    public Func<RadioPickerViewModel, Task<RadioChoice?>>? ShowRadioPicker { get; set; }
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
            Bands.Add(new Choice(b.Name, b, c => SelectBand((Band)c.Value)));
        foreach (var m in ModeOrder)
            Modes.Add(new Choice(m.ToString(), m, c => _ = SelectModeAsync((RxMode)c.Value)));
        foreach (var s in Steps)
            TuneSteps.Add(new Choice(s >= 1000 ? $"{s / 1000}k" : $"{s}", s, c => SelectStep((int)c.Value)));
        foreach (var nb in new[] { NbMode.Nb1, NbMode.Nb2 })
            NoiseBlankers.Add(new Choice(nb.ToString().ToUpperInvariant(), nb,
                c => _ = SelectNbAsync(c.IsSelected ? NbMode.Off : (NbMode)c.Value)));
        // Thetis numbering: NR1 LMS, NR2 Ephraim-Malah, NR3 RNNoise, NR4 libspecbleach.
        foreach (var (label, mode) in new[] { ("NR1", NrMode.Anr), ("NR2", NrMode.Emnr), ("NR3", NrMode.Rnnr), ("NR4", NrMode.Sbnr) })
            NoiseReductions.Add(new Choice(label, mode,
                c => _ = SelectNrAsync(c.IsSelected ? NrMode.Off : (NrMode)c.Value)));
        Mark(TuneSteps, _settings.TuneStepHz);
        _specMinDb = _settings.SpecMinDb;
        _specMaxDb = _settings.SpecMaxDb;
        _autoSpectrum = _settings.AutoSpectrum;
        _panFraction = _settings.PanFraction;
    }

    // ---- startup and connection ---------------------------------------

    public async Task StartAsync()
    {
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
        _client.Vfo += v => Dispatcher.UIThread.Post(() =>
        {
            if (v.Receiver == ActiveRx) { if (!_vfoSender.Busy) SetVfoLocal(v.VfoHz); }
            else if (v.Receiver == 0 && _sliceA is { } a) _sliceA = a with { VfoHz = v.VfoHz };
            else if (v.Receiver == 1 && _sliceB is { } b) _sliceB = b with { VfoHz = v.VfoHz };
        });
        _client.StreamClosed += reason => Dispatcher.UIThread.Post(() =>
        {
            if (IsConnected) Status = $"Engine stream closed: {reason}";
        });
    }

    /// <summary>
    /// Restart the engine and reconnect to the same radio, so settings the
    /// engine only reads at startup (TCI and network CAT servers) take effect.
    /// Unkeys and disconnects cleanly first.
    /// </summary>
    public async Task RestartEngineAsync()
    {
        var radio = _radio;
        Status = "Restarting the engine…";
        await DisconnectAsync();
        _client?.Dispose();
        _client = null;
        _engine.Dispose();
        try
        {
            await _engine.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Status = $"Engine did not restart: {ex.Message}";
            return;
        }
        CreateClient();
        if (radio is not null) await ConnectAsync(radio);
        else Status = "Engine restarted. Choose a radio.";
    }

    [RelayCommand]
    private async Task ChooseRadioAsync()
    {
        if (_client is null || ShowRadioPicker is null) return;
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
            catch (StationException ex) when (ex.Status == HttpStatusCode.ServiceUnavailable && DateTime.UtcNow < deadline)
            {
                // The engine bakes FFTW plans on first run; connect waits for it.
                Status = "Preparing DSP (the first run can take a minute)…";
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                Status = $"Could not connect to {radio.Label}: {ex.Message}";
                return false;
            }
        }

        _radio = radio with { TakeOver = false };
        _settings.LastRadioIp = radio.Ip;
        _settings.LastRadioMac = radio.Mac;
        _settings.LastRadioP2 = radio.Protocol2;
        _settings.LastRadioBoardId = radio.BoardId;
        _settings.Save();

        // Policy: if the radio will transmit there, SimpleHPSDR allows it. No
        // region band plan gets in the way.
        await Try(() => client.SetBandPlanGuardIgnoredAsync(true));
        var state = await client.GetStateAsync();
        await ApplyStateAsync(state);
        ActiveRx = 0;
        UpdateSlices(state);
        LoadSpanRates(radio.Protocol2, state.SampleRate);
        await LoadDisplayTuningAsync();
        await LoadAntennasAsync();
        await LoadTransmitAsync(state);
        LoadPureSignal(state);
        await LoadMonitorAsync();
        await client.OpenStreamAsync();
        IsConnected = true;
        _statePoll.Start();
        RadioLabel = radio.Label;
        Status = $"Connected to {radio.Label}";
        return true;
    }

    private async Task DisconnectAsync()
    {
        if (_client is null || _radio is null) return;
        await UnkeyAsync();
        try { await _client.DisconnectAsync(_radio.Protocol2); } catch (Exception) { }
        IsConnected = false;
        _statePoll.Stop();
        RadioLabel = "No radio";
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
            AttOffsetDb = s.AttOffsetDb;
            AdcOverload = s.AdcOverloadWarning;
            Muted = s.Rx1Muted;
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

    public void TuneTo(long hz)
    {
        if (!IsConnected) return;
        hz = Math.Clamp(hz, 10_000, 60_000_000);
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

    public void BeginFrequencyEntry()
    {
        if (!IsConnected) return;
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
            TuneTo((long)Math.Round(v < 100 ? v * 1e6 : v * 1e3));
        CancelFrequencyEntry();
    }

    private void SelectBand(Band band)
    {
        if (!IsConnected) return;
        TuneTo(_settings.BandMemory.TryGetValue(band.Name, out var hz) && band.Contains(hz) ? hz : band.DefaultHz);
        var mode = band.DefaultHz < 10_000_000 ? RxMode.LSB : RxMode.USB;
        if (Mode is RxMode.LSB or RxMode.USB && Mode != mode) _ = SelectModeAsync(mode);
    }

    private void SelectStep(int hz)
    {
        _settings.TuneStepHz = hz;
        _settings.Save();
        Mark(TuneSteps, hz);
        OnPropertyChanged(nameof(StepHz));
        OnPropertyChanged(nameof(StepLabel));
    }

    /// <summary>1x → 2x → 4x → 8x → 16x → 1x.</summary>
    [RelayCommand]
    private async Task CycleZoomAsync()
    {
        if (!IsConnected) return;
        int i = Array.IndexOf(ZoomLevels, ZoomLevel);
        int next = ZoomLevels[(i + 1) % ZoomLevels.Length];
        ZoomLevel = next;
        await Try(async () =>
        {
            await _client!.SetZoomAsync(next);
            await SyncSliceBZoomAsync();
        });
    }

    [RelayCommand]
    private void ToggleCtun() => CtunOn = !CtunOn;

    partial void OnCtunOnChanged(bool value)
    {
        if (!_applyingState && IsConnected) _ = Try(() => _client!.SetCtunAsync(value));
    }

    // ---- mode and filter ------------------------------------------------

    private async Task SelectModeAsync(RxMode mode)
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
    }

    private async Task SetModeLocalAsync(RxMode mode, string? presetName)
    {
        Mode = mode;
        OnPropertyChanged(nameof(Mode));
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
        });
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

    private async Task ResetFilterAsync(FilterSlot slot)
    {
        await Try(async () =>
        {
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

    // ---- audio and front end -------------------------------------------

    partial void OnAfGainDbChanged(double value)
    {
        OnPropertyChanged(nameof(AfGainText));
        OnPropertyChanged(nameof(AfTip));
        if (!_applyingState && IsConnected) _afSender.Send(Math.Round(value));
    }

    partial void OnAttenDbChanged(double value)
    {
        OnPropertyChanged(nameof(AttenText));
        OnPropertyChanged(nameof(PreLabel));
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
            await _client.SetNrAsync(change(nr));
        });
        var applied = (await _client!.GetStateAsync()).Nr ?? new NrConfig();
        Mark(NoiseBlankers, applied.NbMode);
        Mark(NoiseReductions, applied.NrMode);
    }

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
        if (!_applyingState && IsConnected) _ = Try(() => _client!.SetMuteAsync(value));
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
        _settings.Save();
        try { _statePoll.Stop(); } catch (InvalidOperationException) { }  // signal thread
        UnkeyForShutdown();
        if (_client is not null && _radio is not null)
        {
            var (client, p2) = (_client, _radio.Protocol2);
            try { Task.Run(() => client.DisconnectAsync(p2)).Wait(TimeSpan.FromSeconds(2)); }
            catch (Exception) { }
        }
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
    }
}
