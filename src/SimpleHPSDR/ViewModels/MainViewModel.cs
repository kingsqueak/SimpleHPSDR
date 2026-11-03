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
    private static readonly int[] Steps = [10, 100, 500, 1_000, 5_000, 10_000];

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
    [ObservableProperty] private bool _preampOn;
    [ObservableProperty] private bool _autoAtt;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AttenText))]
    private int _attOffsetDb;
    [ObservableProperty] private bool _adcOverload;
    [ObservableProperty] private bool _muted;
    [ObservableProperty] private double _signalDbm = -140;
    [ObservableProperty] private string _sUnits = "S0";
    [ObservableProperty] private string _frequencyEntry = "";

    public ObservableCollection<Choice> Bands { get; } = [];
    public ObservableCollection<Choice> Modes { get; } = [];
    public ObservableCollection<Choice> Filters { get; } = [];
    public ObservableCollection<Choice> TuneSteps { get; } = [];
    public ObservableCollection<Choice> Zooms { get; } = [];
    public ObservableCollection<Choice> NoiseBlankers { get; } = [];
    public ObservableCollection<Choice> NoiseReductions { get; } = [];

    public RxMode Mode { get; private set; } = RxMode.USB;
    public int StepHz => _settings.TuneStepHz;
    public string FrequencyText => VfoHz.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', '.');
    public string AfGainText => $"{AfGainDb:0} dB";
    public string AttenText => AttOffsetDb > 0 ? $"{AttenDb:0}+{AttOffsetDb} dB" : $"{AttenDb:0} dB";

    /// <summary>Set by the window: shows the settings dialog.</summary>
    public Func<SettingsViewModel, Task>? ShowSettings { get; set; }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (_client is null || ShowSettings is null) return;
        var settings = new SettingsViewModel(_client) { MeasuredTxPeak = MeasuredTxPeak };
        settings.PaMaxPowerSaved += SetPowerScale;
        settings.AudioSaved += () => _ = LoadMicSourcesAsync();
        _ = settings.LoadAsync();
        await ShowSettings(settings);
    }

    /// <summary>Set by the window: shows the radio picker and returns the choice.</summary>
    public Func<RadioPickerViewModel, Task<RadioChoice?>>? ShowRadioPicker { get; set; }
    public event Action<DisplayFrame>? Display;
    public event Action? DisplayReset;

    public MainViewModel()
    {
        _vfoSender = new Latest<long>(hz => _client!.SetVfoAsync(hz));
        _afSender = new Latest<double>(db => _client!.SetAfGainAsync(db));
        _attSender = new Latest<int>(db => _client!.SetAttenuatorAsync(db));

        foreach (var b in SimpleHPSDR.Band.All)
            Bands.Add(new Choice(b.Name, b, c => SelectBand((Band)c.Value)));
        foreach (var m in ModeOrder)
            Modes.Add(new Choice(m.ToString(), m, c => _ = SelectModeAsync((RxMode)c.Value)));
        foreach (var s in Steps)
            TuneSteps.Add(new Choice(s >= 1000 ? $"{s / 1000}k" : $"{s}", s, c => SelectStep((int)c.Value)));
        foreach (var z in new[] { 1, 2, 4, 8 })
            Zooms.Add(new Choice($"{z}×", z, c => _ = SelectZoomAsync((int)c.Value)));
        foreach (var nb in new[] { NbMode.Nb1, NbMode.Nb2 })
            NoiseBlankers.Add(new Choice(nb.ToString().ToUpperInvariant(), nb,
                c => _ = SelectNbAsync(c.IsSelected ? NbMode.Off : (NbMode)c.Value)));
        // Thetis numbering: NR1 LMS, NR2 Ephraim-Malah, NR3 RNNoise, NR4 libspecbleach.
        foreach (var (label, mode) in new[] { ("NR1", NrMode.Anr), ("NR2", NrMode.Emnr), ("NR3", NrMode.Rnnr), ("NR4", NrMode.Sbnr) })
            NoiseReductions.Add(new Choice(label, mode,
                c => _ = SelectNrAsync(c.IsSelected ? NrMode.Off : (NrMode)c.Value)));
        Mark(TuneSteps, _settings.TuneStepHz);
        Mark(Zooms, 1);
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

        _client = new StationClient(_engine.BaseUri);
        _client.Display += QueueFrame;
        _client.SignalDbm += dbm =>
        {
            _latestDbm = dbm;
            if (Interlocked.Exchange(ref _meterScheduled, 1) == 0)
                Dispatcher.UIThread.Post(() =>
                {
                    _meterScheduled = 0;
                    UpdateMeter(_latestDbm);
                });
        };
        _statePoll.Tick += async (_, _) => await PollStateAsync();
        WireTransmit(_client);
        WirePureSignal(_client);
        _client.Vfo += v => Dispatcher.UIThread.Post(() =>
        {
            if (v.Receiver == 0 && !_vfoSender.Busy) SetVfoLocal(v.VfoHz);
        });
        _client.StreamClosed += reason => Dispatcher.UIThread.Post(() =>
        {
            if (IsConnected) Status = $"Engine stream closed: {reason}";
        });

        if (_settings is { AutoConnect: true, LastRadioIp: not null })
        {
            Status = "Looking for your radio…";
            RadioInfo[] found = [];
            try { found = await _client.DiscoverAsync(); } catch (HttpRequestException) { }
            var match = found.Select(r => new RadioEntry(r)).FirstOrDefault(r =>
                (r.Mac == _settings.LastRadioMac || r.Info.IpAddress == _settings.LastRadioIp) && !r.Busy);
            if (match is not null && await ConnectAsync(match.ToChoice()))
                return;
        }
        await ChooseRadioAsync();
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
        await LoadTransmitAsync(state);
        LoadPureSignal(state);
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
            Mark(Zooms, s.ZoomLevel);
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
    }

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

    [RelayCommand]
    private void EnterFrequency()
    {
        // Accept kHz ("14074", "7074.5") or MHz ("14.074").
        var text = FrequencyEntry.Trim().Replace(',', '.');
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) || v <= 0)
            return;
        TuneTo((long)Math.Round(v < 100 ? v * 1e6 : v * 1e3));
        FrequencyEntry = "";
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
    }

    private async Task SelectZoomAsync(int level)
    {
        if (!IsConnected) return;
        Mark(Zooms, level);
        await Try(() => _client!.SetZoomAsync(level));
    }

    // ---- mode and filter ------------------------------------------------

    private async Task SelectModeAsync(RxMode mode)
    {
        if (!IsConnected) return;
        await Try(() => _client!.SetModeAsync(mode));
        var s = await _client!.GetStateAsync();
        FilterLowHz = s.FilterLowHz;
        FilterHighHz = s.FilterHighHz;
        await SetModeLocalAsync(s.Mode, s.FilterPresetName);
    }

    private async Task SetModeLocalAsync(RxMode mode, string? presetName)
    {
        Mode = mode;
        OnPropertyChanged(nameof(Mode));
        Mark(Modes, mode);

        Filters.Clear();
        FilterPreset[] presets = [];
        try { presets = await _client!.GetFilterPresetsAsync(mode); } catch (Exception) { }
        foreach (var p in presets.Where(p => p.LowHz != p.HighHz).Take(10))
            Filters.Add(new Choice(p.Label, p, c => _ = SelectFilterAsync((FilterPreset)c.Value)));
        foreach (var c in Filters)
            c.IsSelected = ((FilterPreset)c.Value).SlotName == presetName;
    }

    private async Task SelectFilterAsync(FilterPreset p)
    {
        foreach (var c in Filters) c.IsSelected = ReferenceEquals(c.Value, p);
        FilterLowHz = p.LowHz;
        FilterHighHz = p.HighHz;
        await Try(() => _client!.SetFilterAsync(p.LowHz, p.HighHz, p.SlotName));
    }

    // ---- audio and front end -------------------------------------------

    partial void OnAfGainDbChanged(double value)
    {
        OnPropertyChanged(nameof(AfGainText));
        if (!_applyingState && IsConnected) _afSender.Send(Math.Round(value));
    }

    partial void OnAttenDbChanged(double value)
    {
        OnPropertyChanged(nameof(AttenText));
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

    // Auto-ATT and the overload flag change inside the engine; no frame
    // carries them, so poll.
    private async Task PollStateAsync()
    {
        if (!IsConnected || _client is null) return;
        try
        {
            var s = await _client.GetStateAsync();
            AttOffsetDb = s.AttOffsetDb;
            AdcOverload = s.AdcOverloadWarning;
            PollPureSignal(s);
        }
        catch (Exception) { }
    }

    private void UpdateMeter(float dbm)
    {
        // Fast attack, slower release, like an analogue meter.
        double k = dbm > SignalDbm ? 0.6 : 0.2;
        dbm = (float)(SignalDbm + (dbm - SignalDbm) * k);
        SignalDbm = dbm;
        // S9 = -73 dBm, 6 dB per S-unit below it.
        SUnits = dbm >= -73
            ? $"S9+{Math.Max(0, Math.Round(dbm + 73)):0}"
            : $"S{Math.Clamp((int)Math.Round(9 + (dbm + 73) / 6), 0, 9)}";
    }

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
