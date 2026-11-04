using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>Right-click on the Mode button: options for AM and FM. Also
/// PureSignal's saved correction, on the PS button.</summary>
public sealed partial class MainViewModel
{
    public string Theme => _settings.Theme;

    public bool ModeIsAm => Mode is RxMode.AM or RxMode.SAM;
    public bool ModeIsFm => Mode == RxMode.FM;
    public bool ModeHasNoOptions => !ModeIsAm && !ModeIsFm;

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

    private bool _applyingModeOptions;
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
            _applyingModeOptions = false;
        });
    }

    partial void OnAmBroadcastOnChanged(bool value) => SendAm();
    partial void OnAmPositivePeakChanged(double value) => SendAm();
    partial void OnAmNegativePeakChanged(double value) => SendAm();
    partial void OnAmPreEmphasisChanged(bool value) => SendAm();
    partial void OnAmInvertChanged(bool value) => SendAm();
    partial void OnAmCarrierPctChanged(double value) => SendAm();

    private void SendAm()
    {
        if (_applyingModeOptions || !IsConnected) return;
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

    private void SendFm()
    {
        if (_applyingModeOptions || !IsConnected) return;
        (_fmSender ??= new Latest<int>(async _ =>
        {
            _fm = _fm with
            {
                DeviationHz = FmNarrow ? 2500 : 5000, CtcssEnabled = FmCtcssOn, CtcssToneHz = FmCtcssTone,
                RxToneSquelch = (FmRxToneSquelch)FmRxToneIndex, RepeaterShift = (FmRepeaterShift)FmShiftIndex,
                RepeaterReverse = FmReverse, ToneBurstOnKeyUp = FmBurstOnKey,
            };
            await _client!.SetFmAsync(_fm);
        })).Send(++_modeOptionSeq);
    }

    // ---- PureSignal correction ------------------------------------------

    private static string PsCorrectionPath => Path.Combine(EngineHost.DataDir, "ps-correction.txt");

    /// <summary>Save what PureSignal has learned, to bring back without recalibrating.</summary>
    [RelayCommand]
    private Task SavePsCorrectionAsync() => Try(async () =>
    {
        await _client!.SavePsCorrectionAsync(PsCorrectionPath);
        Status = "PureSignal correction saved.";
    });

    [RelayCommand]
    private Task RestorePsCorrectionAsync() => Try(async () =>
    {
        if (!File.Exists(PsCorrectionPath))
        {
            Status = "No saved PureSignal correction yet.";
            return;
        }
        await _client!.RestorePsCorrectionAsync(PsCorrectionPath);
        Status = "PureSignal correction restored.";
    });
}
