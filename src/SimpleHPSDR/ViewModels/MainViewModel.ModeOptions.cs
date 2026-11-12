using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>Right-click on the Mode button: options for AM and FM. Also
/// PureSignal's saved correction, on the PS button.</summary>
public sealed partial class MainViewModel
{
    public string Theme => _settings.Theme;

    /// <summary>For the About window: the version the running engine reports.</summary>
    public async Task<string> EngineVersionAsync()
    {
        try { return _client is null ? "" : (await _client.GetEngineVersionAsync()).Split('+')[0]; }
        catch (Exception) { return ""; }
    }

    public bool ModeIsAm => Mode is RxMode.AM or RxMode.SAM;
    public bool ModeIsFm => Mode == RxMode.FM;
    /// <summary>Both sidebands are heard, so a filter stays centred on the dial.</summary>
    public bool FilterIsSymmetric => Mode is RxMode.AM or RxMode.SAM or RxMode.DSB or RxMode.FM;
    public bool ModeHasNoOptions => !ModeIsAm && !ModeIsFm && Mode != SimpleHPSDR.Contracts.RxMode.FreeDv;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AmSymmetric))] private bool _amBroadcastOn;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AmPositiveText))] private double _amPositivePeak;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AmNegativeText))] private double _amNegativePeak;
    [ObservableProperty] private bool _amPreEmphasis;
    [ObservableProperty] private bool _amInvert;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AmCarrierText))] private double _amCarrierPct;
    public bool AmSymmetric => !AmBroadcastOn;
    public string AmPositiveText => $"{AmPositivePeak:0}%";
    public string AmNegativeText => $"{AmNegativePeak:0}%";
    public string AmCarrierText => $"{AmCarrierPct:0}%";

    public IReadOnlyList<double> CtcssTones => FmConfig.CtcssTones;
    public string[] FmShiftNames { get; } = ["Simplex", "Plus", "Minus"];
    public string[] FmToneModes { get; } = ["Off", "CTCSS", "DCS"];
    [ObservableProperty] private bool _fmNarrow;
    [ObservableProperty] private bool _fmCtcssOn;
    [ObservableProperty] private double _fmCtcssTone = 100;
    [ObservableProperty] private int _fmRxToneIndex;
    [ObservableProperty] private int _fmShiftIndex;
    [ObservableProperty] private bool _fmReverse;
    [ObservableProperty] private bool _fmBurstOnKey;
    [ObservableProperty] private bool _fmPreEmphasisFirst;

    private bool _applyingModeOptions;
    // Nothing is sent until the engine's values have been read: a Mode popup
    // opened before that would have its sliders snap the empty defaults to
    // their minimums and write those over the saved settings.
    private bool _modeOptionsLoaded;

    /// <summary>On disconnect: what was read from the engine is no longer
    /// known, so nothing is sent until it has been read again.</summary>
    private void ForgetLoadedSettings()
    {
        _modeOptionsLoaded = false;
        _cwLoaded = false;
        _notchesLoaded = false;
        _rxLevLoaded = false;
    }
    private Latest<int>? _amSender, _fmSender;
    private int _modeOptionSeq;
    private FmConfig _fm = new();
    private AmBroadcastConfig _am = new();

    /// <summary>Right-click on Mode: show what the engine has.</summary>
    public async Task LoadModeOptionsAsync()
    {
        if (!IsConnected) return;
        await Try(async () =>
        {
            var s = await _client!.GetStateAsync();
            _am = await _client.GetAmBroadcastAsync();
            _fm = await _client.GetFmAsync();
            _applyingModeOptions = true;
            (AmBroadcastOn, AmPositivePeak, AmNegativePeak, AmPreEmphasis, AmInvert) =
                (_am.Enabled, _am.PositivePeakPct, _am.NegativePeakPct, _am.PreEmphasis, _am.InvertPolarity);
            AmCarrierPct = s.AmCarrierLevelPercent;
            FmNarrow = _fm.DeviationHz <= 2500;
            (FmCtcssOn, FmCtcssTone, FmRxToneIndex, FmShiftIndex, FmReverse, FmBurstOnKey) =
                (_fm.CtcssEnabled, _fm.CtcssToneHz, (int)_fm.RxToneSquelch, (int)_fm.RepeaterShift, _fm.RepeaterReverse, _fm.ToneBurstOnKeyUp);
            FmPreEmphasisFirst = _fm.PreEmphasisBeforeLimiting;
            _applyingModeOptions = false;
            _modeOptionsLoaded = true;
        });
        _applyingModeOptions = false;
    }

    partial void OnAmBroadcastOnChanged(bool value) => SendAm();
    partial void OnAmPositivePeakChanged(double value) => SendAm();
    partial void OnAmNegativePeakChanged(double value) => SendAm();
    partial void OnAmPreEmphasisChanged(bool value) => SendAm();
    partial void OnAmInvertChanged(bool value) => SendAm();
    partial void OnAmCarrierPctChanged(double value) => SendAm();

    private void SendAm()
    {
        if (_applyingModeOptions || !_modeOptionsLoaded || !IsConnected) return;
        (_amSender ??= new Latest<int>(async _ =>
        {
            await _client!.SetAmBroadcastAsync(new AmBroadcastConfig(AmBroadcastOn, Math.Round(AmPositivePeak), Math.Round(AmNegativePeak), AmPreEmphasis, AmInvert));
            await _client.SetAmCarrierAsync((int)Math.Round(AmCarrierPct));
        })).Send(++_modeOptionSeq);
    }

    partial void OnFmNarrowChanged(bool value) => SendFm();
    partial void OnFmCtcssOnChanged(bool value) => SendFm();
    partial void OnFmCtcssToneChanged(double value) => SendFm();
    partial void OnFmRxToneIndexChanged(int value) => SendFm();
    partial void OnFmShiftIndexChanged(int value) => SendFm();
    partial void OnFmReverseChanged(bool value) => SendFm();
    partial void OnFmBurstOnKeyChanged(bool value) => SendFm();
    partial void OnFmPreEmphasisFirstChanged(bool value) => SendFm();

    private void SendFm()
    {
        if (_applyingModeOptions || !_modeOptionsLoaded || !IsConnected) return;
        (_fmSender ??= new Latest<int>(async _ =>
        {
            _fm = _fm with
            {
                // Only flipping Narrow picks 2.5 or 5 kHz; any other deviation stays.
                DeviationHz = FmNarrow == (_fm.DeviationHz <= 2500) ? _fm.DeviationHz : FmNarrow ? 2500 : 5000,
                CtcssEnabled = FmCtcssOn, CtcssToneHz = FmCtcssTone,
                RxToneSquelch = (FmRxToneSquelch)FmRxToneIndex, RepeaterShift = (FmRepeaterShift)FmShiftIndex,
                RepeaterReverse = FmReverse, ToneBurstOnKeyUp = FmBurstOnKey,
                PreEmphasisBeforeLimiting = FmPreEmphasisFirst,
            };
            await _client!.SetFmAsync(_fm);
        })).Send(++_modeOptionSeq);
    }

    // ---- PureSignal correction, kept for each band ---------------------
    //
    // PureSignal adapts on speech, but each start and each band change began
    // from nothing. So the correction is saved after an over that updated it,
    // one file per band and feedback source, and loaded again on connect and
    // on a band change. A loaded correction is only the starting point: the
    // engine keeps adapting from it.

    private string? PsCorrectionPath(long hz)
    {
        if (SimpleHPSDR.Band.Find(hz) is not { } band) return null;
        string dir = Path.Combine(EngineHost.DataDir, "ps-corrections");
        Directory.CreateDirectory(dir);
        string name = string.Concat(band.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        return Path.Combine(dir, $"{name}-{(_psExternal ? "external" : "internal")}.txt");
    }

    /// <summary>Keep PureSignal's correction for each band and start from it.</summary>
    public bool PsRememberByBand
    {
        get => _settings.PsRememberByBand;
        set
        {
            if (_settings.PsRememberByBand == value) return;
            _settings.PsRememberByBand = value;
            _settings.Save();
            OnPropertyChanged();
            if (value) LoadPsCorrectionForBand(force: true);
        }
    }

    /// <summary>"Correction updated 14:32:05", or how it was loaded. Empty when there's nothing to say.</summary>
    [ObservableProperty] private string _psUpdatedText = "";

    private ushort _psFitCount;
    private bool _psFitCountKnown, _psFitThisOver, _psKeyed;
    private string? _psLoadedPath;

    /// <summary>Every PureSignal meter frame (UI thread).</summary>
    private void TrackPsFits(PsMetersFrame f)
    {
        if (!_psFitCountKnown)
        {
            (_psFitCount, _psFitCountKnown) = (f.FitCount, true);
            return;
        }
        if (f.FitCount == _psFitCount) return;
        _psFitCount = f.FitCount;
        if (_psKeyed) _psFitThisOver = true;
        PsUpdatedText = $"Correction updated {DateTime.Now:HH:mm:ss}";
    }

    /// <summary>Every engine key report (UI thread): after an over that
    /// updated the correction, save it for this band.</summary>
    private void TrackEngineKeyForPs(bool keyed)
    {
        if (keyed == _psKeyed) return;
        _psKeyed = keyed;
        if (keyed)
        {
            _psFitThisOver = false;
            return;
        }
        if (!_psFitThisOver || !PsOn || !PsRememberByBand || !IsConnected) return;
        if (PsCorrectionPath(VfoHz) is not { } path) return;
        _psLoadedPath = path;      // what the engine holds now is this band's
        _ = Try(() => _client!.SavePsCorrectionAsync(path));
    }

    /// <summary>On connect, on a band change and when PureSignal is switched
    /// on: start from the correction saved for this band, if there is one.</summary>
    private void LoadPsCorrectionForBand(bool force = false)
    {
        if (!IsConnected || !PsOn || !PsRememberByBand || _psKeyed || MoxOn || TunOn || TwoToneOn) return;
        if (PsCorrectionPath(VfoHz) is not { } path) return;
        if (!force && path == _psLoadedPath) return;
        _psLoadedPath = path;
        if (!File.Exists(path)) return;
        _ = Try(async () =>
        {
            await _client!.RestorePsCorrectionAsync(path);
            PsUpdatedText = "Started from the correction saved for this band";
        });
    }

    private void ForgetPsCorrectionState()
    {
        (_psFitCountKnown, _psFitThisOver, _psKeyed, _psLoadedPath) = (false, false, false, null);
        PsUpdatedText = "";
    }
}
