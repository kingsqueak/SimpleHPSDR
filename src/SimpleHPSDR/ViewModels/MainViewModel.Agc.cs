using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Contracts;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>AGC: the button turns the engine's auto AGC on (blue) or off;
/// off is manual, where the speed (Off/Long/Slow/Med/Fast) is chosen; Off
/// is the engine's Fixed mode, whose gain gets its own slider. The settings
/// act on the active receiver; each receiver keeps its own.</summary>
public sealed partial class MainViewModel
{
    private static readonly AgcMode[] AgcSpeeds = [AgcMode.Fixed, AgcMode.Long, AgcMode.Slow, AgcMode.Med, AgcMode.Fast];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgcManual))] [NotifyPropertyChangedFor(nameof(AgcSubLabel))]
    [NotifyPropertyChangedFor(nameof(AgcFixedShown))]
    private bool _agcAuto;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AgcFixedText))]
    private double _agcFixedGainDb = 20;
    private bool _applyingAgc;
    private Latest<double>? _agcFixedSender;
    [ObservableProperty] private string _agcSpeedLabel = "Med";
    private AgcMode _agcMode = AgcMode.Med;

    // AGC-T, the AGC's maximum gain. The engine removed its AGC knee control,
    // so this sets the same thing from the other end, and the yellow line
    // shows where the knee lands.
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AgcTopText))]
    private double _agcTopDb = 90;
    private double _agcOffsetDb;      // auto AGC's adjustment on top
    private Latest<double>? _agcTopSender;
    public string AgcTopText => $"{AgcTopDb:0} dB";

    // Auto AGC's one setting (right-click AGC): the threshold sits this far above
    // the band noise floor. Saved here; the engine is told after connecting.
    public const double DefaultAutoAgcOffsetDb = 20;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AutoAgcOffsetText))]
    private double _autoAgcOffsetDb = DefaultAutoAgcOffsetDb;
    public string AutoAgcOffsetText => $"+{AutoAgcOffsetDb:0} dB";
    private Latest<double>? _autoAgcOffsetSender;

    partial void OnAutoAgcOffsetDbChanged(double value)
    {
        _settings.AutoAgcOffsetDb = value;
        _settings.Save();
        if (IsConnected)
            (_autoAgcOffsetSender ??= new Latest<double>(db => Try(() => _client!.SetAutoAgcOffsetAsync(db)))).Send(value);
    }

    [RelayCommand]
    private void ResetAutoAgcOffset() => AutoAgcOffsetDb = DefaultAutoAgcOffsetDb;

    // Filter close-up averaging (right-click the close-up). Saved, app only.
    public const double MinCloseUpAverageSec = 0.05, MaxCloseUpAverageSec = 3, DefaultCloseUpAverageSec = 0.05;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CloseUpAverageText))]
    private double _closeUpAverageSec = DefaultCloseUpAverageSec;
    public string CloseUpAverageText => $"{CloseUpAverageSec:0.00} s";

    partial void OnCloseUpAverageSecChanged(double value)
    {
        _settings.CloseUpAverageSec = value;
        _settings.Save();
    }

    [ObservableProperty] private bool _closeUpColoured = true;

    partial void OnCloseUpColouredChanged(bool value)
    {
        _settings.CloseUpColoured = value;
        _settings.Save();
    }

    [ObservableProperty] private bool _closeUpHighRes = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CloseUpFftSize))] private int _closeUpDetail = 1;
    public static string[] CloseUpDetails { get; } = ["4 Hz", "2 Hz", "1 Hz"];
    public int CloseUpFftSize => 4096 << Math.Clamp(CloseUpDetail, 0, 2);   // at 16 kHz: 3.9, 2.0, 1.0 Hz
    private DateTime _closeUpRetryAfter;

    partial void OnCloseUpHighResChanged(bool value) { _settings.CloseUpHighRes = value; _settings.Save(); }
    partial void OnCloseUpDetailChanged(int value) { _settings.CloseUpDetail = value; _settings.Save(); }

    /// <summary>One high-resolution close-up for the active receiver, or null (off,
    /// not connected, not ready, or an engine without it: then it waits a minute).</summary>
    internal async Task<(int RateHz, float[] Db)?> FetchCloseUpAsync()
    {
        if (!CloseUpHighRes || !IsConnected || _client is not { } c || DateTime.UtcNow < _closeUpRetryAfter) return null;
        try { return await c.GetCloseUpAsync(ActiveRx, CloseUpFftSize); }
        catch (StationException) { _closeUpRetryAfter = DateTime.UtcNow.AddMinutes(1); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
        return null;
    }

    [RelayCommand]
    private void ResetCloseUpAverage() => CloseUpAverageSec = DefaultCloseUpAverageSec;

    public bool AgcManual => !AgcAuto;
    /// <summary>The fixed-gain slider: manual AGC with the speed Off.</summary>
    public bool AgcFixedShown => !AgcAuto && _agcMode == AgcMode.Fixed;
    public string AgcFixedText => $"{AgcFixedGainDb:0} dB";
    /// <summary>AGC-T doesn't apply with AGC off (the fixed-gain slider does).</summary>
    public bool AgcTopShown => _agcMode != AgcMode.Fixed;
    private static string SpeedName(AgcMode m) => m == AgcMode.Fixed ? "Off" : m.ToString();
    public string AgcSubLabel => AgcAuto ? "auto" : "manual";
    public ObservableCollection<Choice> AgcSpeedChoices { get; } = [];

    /// <summary>From engine state (connect and the 1 s poll): the active
    /// receiver's AGC, restored into the controls.</summary>
    private void UpdateAgc(RxSettings view)
    {
        if (AgcSpeedChoices.Count == 0)
            foreach (var m in AgcSpeeds)
                AgcSpeedChoices.Add(new Choice(SpeedName(m), m, c => _ = SelectAgcSpeedAsync((AgcMode)c.Value)));
        AgcAuto = view.AutoAgcEnabled;
        _agcMode = view.Agc?.Mode ?? AgcMode.Med;
        AgcSpeedLabel = SpeedName(_agcMode);
        Mark(AgcSpeedChoices, _agcMode);
        OnPropertyChanged(nameof(AgcFixedShown));
        OnPropertyChanged(nameof(AgcTopShown));
        if (_agcTopSender is null || !_agcTopSender.Busy)
        {
            _applyingAgc = true;
            AgcTopDb = view.AgcTopDb;
            _applyingAgc = false;
        }
        if (_agcFixedSender is null || !_agcFixedSender.Busy)
        {
            _applyingAgc = true;
            AgcFixedGainDb = view.Agc?.FixedGainDb ?? 20;
            _applyingAgc = false;
        }
    }

    /// <summary>Remember an AGC change this side just sent, so the panadapter
    /// chips show it before the next poll.</summary>
    private void NoteAgcSent(int rx, AgcConfig? agc = null, double? topDb = null, bool? auto = null)
    {
        if (rx == 0) { if (_sliceA is { } a) _sliceA = a with { Agc = agc ?? a.Agc, AgcTopDb = topDb ?? a.AgcTopDb, AutoAgcEnabled = auto ?? a.AutoAgcEnabled }; }
        else if (_sliceB is { } b) _sliceB = b with { Agc = agc ?? b.Agc, AgcTopDb = topDb ?? b.AgcTopDb, AutoAgcEnabled = auto ?? b.AutoAgcEnabled };
        OnPropertyChanged(RxReadouts);
    }

    /// <summary>Send an AGC mode/speed (or fixed gain) to the active receiver.</summary>
    private async Task SendAgcAsync(AgcConfig agc)
    {
        if (ActiveRx == 0) await _client!.SetAgcAsync(agc);
        else await _client!.SetReceiverAsync(1, new ReceiverSetRequest(Agc: agc));
        NoteAgcSent(ActiveRx, agc: agc);
    }

    [RelayCommand]
    private async Task ToggleAgcAutoAsync()
    {
        if (!IsConnected) return;
        bool on = !AgcAuto;
        await Try(async () =>
        {
            if (ActiveRx == 0) await _client!.SetAutoAgcAsync(on);
            else await _client!.SetReceiverAsync(1, new ReceiverSetRequest(AutoAgcEnabled: on));
            NoteAgcSent(ActiveRx, auto: on);
            AgcAuto = on;
            Status = on ? "AGC auto: the engine sets the AGC gain." : $"AGC manual, speed {SpeedName(_agcMode)}.";
        });
    }

    private async Task SelectAgcSpeedAsync(AgcMode mode)
    {
        if (!IsConnected) return;
        await Try(async () =>
        {
            await SendAgcAsync(new AgcConfig(mode, FixedGainDb: Math.Round(AgcFixedGainDb)));
            _agcMode = mode;
            AgcSpeedLabel = SpeedName(mode);
            Mark(AgcSpeedChoices, mode);
            OnPropertyChanged(nameof(AgcFixedShown));
            OnPropertyChanged(nameof(AgcTopShown));
        });
    }

    partial void OnAgcTopDbChanged(double value)
    {
        if (_applyingAgc || !IsConnected) return;
        _agcTopSender ??= new Latest<double>(async db =>
        {
            if (ActiveRx == 0) await _client!.SetAgcGainAsync(db);
            else await _client!.SetReceiverAsync(1, new ReceiverSetRequest(AgcTopDb: db));
            NoteAgcSent(ActiveRx, topDb: db);
        });
        _agcTopSender.Send(Math.Round(value));
    }

    /// <summary>Where the AGC starts to act, on the panadapter's dB scale; NaN
    /// when there's no knee (AGC off). WDSP's own bandscope line, worked out
    /// from its formula: -(max gain) - 10 log10(filter width x FFT size / rate).
    /// An estimate: the engine doesn't report the real value.</summary>
    public double AgcLineDb()
    {
        if (!IsConnected || _agcMode == AgcMode.Fixed || _sampleRateHz <= 0) return double.NaN;
        double width = Math.Max(1, Math.Abs(FilterHighHz - FilterLowHz));
        return -(AgcTopDb + _agcOffsetDb) - 10 * Math.Log10(width * _displayFftSize / _sampleRateHz);
    }

    partial void OnAgcFixedGainDbChanged(double value)
    {
        if (_applyingAgc || !IsConnected) return;
        _agcFixedSender ??= new Latest<double>(async db =>
            await SendAgcAsync(new AgcConfig(AgcMode.Fixed, FixedGainDb: db)));
        _agcFixedSender.Send(Math.Round(value));
    }
}
