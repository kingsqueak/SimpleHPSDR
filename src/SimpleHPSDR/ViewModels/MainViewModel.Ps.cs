using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Engine;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>PureSignal (WDSP calcc/iqc adaptive predistortion) and the
/// two-tone generator used to calibrate it.</summary>
public sealed partial class MainViewModel
{
    private PsMetersFrame _latestPs;
    private int _psScheduled;
    private bool _psStalled;

    /// <summary>Highest TX envelope seen in the latest transmission (0 if
    /// none yet). It must land in calcc's top amplitude step, 93.75-100% of
    /// HW peak, or calibration never finishes (see HwPeakTarget).</summary>
    public double MeasuredTxPeak { get; private set; }

    // The engine's MaxTxEnvelope is a peak held for 2 s, and key-on carries a
    // transient (0.310 against a steady 0.284 on a G2), so only readings from
    // this long after key-down describe the steady signal.
    private static readonly TimeSpan PeakSettle = TimeSpan.FromSeconds(2.2);
    private long _keyDownTicks;              // UTC ticks of the latest key-down, 0 when unkeyed

    // Set once a not-correcting frame arrives in the current key-down. The
    // first frame after key-down repeats the engine's cached meters from the
    // previous transmission (Correcting included), so PS CAL only trusts
    // Correcting after seeing its reset take effect.
    private volatile bool _psSawNotCorrecting;

    private void ResetFreshFit() => _psSawNotCorrecting = false;

    private DateTime? _keyedSince;
    private bool _rekeyToApply;
    private bool _hwPeakAdjustedThisTx;
    private const byte CalStateCollect = 4;   // calcc LCOLLECT

    [ObservableProperty] private bool _psOn;
    /// <summary>PureSignal is on and correcting: the PS button shows green.</summary>
    [ObservableProperty] private bool _psLocked;
    [ObservableProperty] private bool _twoToneOn;
    [ObservableProperty] private string _psStatus = "Off";
    /// <summary>off, ok, warn or bad: picks the badge colour.</summary>
    [ObservableProperty] private string _psLevel = "off";
    [ObservableProperty] private string _psFeedbackText = "";
    /// <summary>What PureSignal is doing and what to do about it, in plain words.</summary>
    [ObservableProperty] private string _psAdvice = "";
    /// <summary>"Signal peak 0.590 · HW peak 0.612", with a tick when they suit each other.</summary>
    [ObservableProperty] private string _psPeakText = "";
    private double _psHwPeak;

    private const int MaxFeedbackAttnDb = 31;      // radio's TX-feedback attenuator range 0..31
    private int _psAttnDb;
    private bool _psExternal;
    private bool _psRangeWarned;

    private void WirePureSignal(StationClient client)
    {
        client.PsMeters += f =>
        {
            _latestPs = f;
            long keyDown = Interlocked.Read(ref _keyDownTicks);
            if (keyDown != 0)
            {
                if (!f.Correcting) _psSawNotCorrecting = true;
                if (f.MaxTxEnvelope > 0 && DateTime.UtcNow.Ticks - keyDown >= PeakSettle.Ticks)
                    MeasuredTxPeak = f.MaxTxEnvelope;
            }
            if (Interlocked.Exchange(ref _psScheduled, 1) == 0)
                Dispatcher.UIThread.Post(() =>
                {
                    _psScheduled = 0;
                    UpdatePsStatus();
                });
        };
    }

    private void LoadPureSignal(StateDto s)
    {
        FollowTwoToneTones(s);
        _psAttnDb = s.PsTxFeedbackAttenuationDb;
        _psExternal = s.PsFeedbackSource == PsFeedbackSource.External;
        KeepPsFeedbackSource();
        _applyingTx = true;
        PsOn = s.PsEnabled;
        TwoToneOn = s.TwoToneEnabled;
        _applyingTx = false;
        _psStalled = s.PsCalibrationStalled;
        _psHwPeak = s.PsHwPeak;
        UpdatePsStatus();
    }

    /// <summary>The feedback source is a station setting: put back the one chosen in
    /// Config if the engine came up with something else.</summary>
    private void KeepPsFeedbackSource()
    {
        if (_settings.PsExternalFeedback is not { } wanted)
        {
            _settings.PsExternalFeedback = _psExternal;
            _settings.Save();
        }
        else if (wanted != _psExternal)
        {
            _psExternal = wanted;
            _ = Try(() => _client!.SetPsFeedbackSourceAsync(wanted ? PsFeedbackSource.External : PsFeedbackSource.Internal));
        }
    }

    /// <summary>From the 1 s state poll: two-tone and the stall flag have no frame.</summary>
    private void PollPureSignal(StateDto s)
    {
        FollowTwoToneTones(s);
        _applyingTx = true;
        TwoToneOn = s.TwoToneEnabled;
        PsOn = s.PsEnabled;
        _applyingTx = false;
        _psStalled = s.PsCalibrationStalled;
        _psHwPeak = s.PsHwPeak;
        _psAttnDb = s.PsTxFeedbackAttenuationDb;
        _psExternal = s.PsFeedbackSource == PsFeedbackSource.External;
        UpdatePsStatus();
        WarnIfFeedbackOutOfRange();
        _ = MaybeAdjustHwPeakAsync(s);
    }

    // Auto HW peak: calcc sorts samples into 16 amplitude steps up to HW peak
    // and calculates only once every step holds 256 samples, the top one
    // (93.75-100% of HW peak) included, within 5 s of keying. So the signal's
    // real peak has to land inside that top step, a little under HW peak:
    //  - HW peak too high: the top step never fills and calibration sits in
    //    LCOLLECT (stalled), so lower it, but only once it's actually stuck;
    //  - HW peak below the signal: the loudest samples are over range and the
    //    fit doesn't lock reliably, so raise it.
    // At most once per transmission. The engine holds PureSignal changes until
    // unkey (changing calcc mid-transmission can splatter), so the new value
    // applies from the next key-down; the badge says so.
    private async Task MaybeAdjustHwPeakAsync(StateDto s)
    {
        // PS CAL makes its own HW peak changes between bursts.
        if (PsCalibrating || !_settings.AutoHwPeak || !PsOn || _hwPeakAdjustedThisTx || _keyedSince is not { } since) return;
        var keyedFor = DateTime.UtcNow - since;
        bool stuck = s.PsCalibrationStalled
                     || (_latestPs.CalState == CalStateCollect && !_latestPs.Correcting
                         && keyedFor > TimeSpan.FromSeconds(3));
        if (keyedFor < PeakSettle + TimeSpan.FromSeconds(0.3)
            || HwPeakTarget(s.PsHwPeak, stuck) is not { } target) return;

        _hwPeakAdjustedThisTx = true;
        await Try(async () =>
        {
            await _client!.SetPsAdvancedAsync(new PsAdvancedSetRequest(HwPeak: target));
            _rekeyToApply = true;
            UpdatePsStatus();
            Status = $"PureSignal: HW peak {s.PsHwPeak:0.0000} → {target:0.0000} to match your signal. Unkey and key again to calibrate.";
        });
    }

    /// <summary>The HW peak to switch to, or null to keep the current one.
    /// The signal's peak should be 94-100% of HW peak; the target puts it at
    /// 97%. Raises whenever the signal is over HW peak; lowers only when
    /// calibration is stuck.</summary>
    private double? HwPeakTarget(double current, bool stuck)
    {
        if (MeasuredTxPeak <= 0 || current <= 0) return null;
        double target = PsPeak.Target(MeasuredTxPeak);
        double share = MeasuredTxPeak / current;
        if (share > PsPeak.HighShare) return target;
        if (stuck && share < PsPeak.LowShare) return target;
        return null;
    }

    // Automatic attenuation keeps feedback in 128-181 only while the needed
    // attenuation fits 0..31 dB. At either end it can't do more; say so,
    // once per transmission, while correcting data is current.
    private void WarnIfFeedbackOutOfRange()
    {
        if (!PsOn || _keyedSince is not { } since || _psRangeWarned
            || DateTime.UtcNow - since < TimeSpan.FromSeconds(3)) return;
        double fb = _latestPs.FeedbackLevel;
        string source = _psExternal ? "amplifier sample" : "feedback";
        if (fb > 181 && _psAttnDb >= MaxFeedbackAttnDb)
            Status = $"PureSignal: {source} too strong (FB {fb:0}) even at {MaxFeedbackAttnDb} dB attenuation. Add a fixed attenuator in the sample line.";
        else if (fb is > 0 and < 128 && _psAttnDb <= 0)
            Status = $"PureSignal: {source} too weak (FB {fb:0}) with no attenuation. Check the sample cable and coupler.";
        else
            return;
        _psRangeWarned = true;
    }

    partial void OnPsCalibratingChanged(bool value) => UpdatePsStatus();

    partial void OnPsOnChanged(bool value)
    {
        UpdatePsStatus();
        if (_applyingTx || !IsConnected) return;
        if (value) OfferPsSetup();
        _ = Try(() => _client!.SetPsAsync(value));
    }

    partial void OnTwoToneOnChanged(bool value)
    {
        OnPropertyChanged(nameof(ImdShown));
        if (!value) RestoreAfterImd();
        if (_applyingTx || !IsConnected) return;
        CancelPsCalForOperatorKey();
        _ = value ? StartTwoToneAsync() : KeyAsync(() => _client!.SetTwoToneAsync(false), false);
    }

    // The dial must be centred before keying: the engine holds LO moves while keyed.
    private async Task StartTwoToneAsync()
    {
        await ZoomForImdAsync();
        if (!TwoToneOn) return;          // turned off again meanwhile
        await KeyAsync(() => _client!.SetTwoToneAsync(true), true);
    }

    // Feedback level is calcc's info[4], 0..256. Thetis colours it: about
    // 128-181 is right, lower means too little sample, higher too much.
    private void UpdatePsStatus()
    {
        if (IsTransmitting)
        {
            // A fresh key-down after an adjustment runs with the new value.
            if (_keyedSince is null)
            {
                _rekeyToApply = false;
                ResetFreshFit();
                Interlocked.Exchange(ref _keyDownTicks, DateTime.UtcNow.Ticks);
            }
            _keyedSince ??= DateTime.UtcNow;
        }
        else
        {
            _keyedSince = null;
            _hwPeakAdjustedThisTx = false;
            _psRangeWarned = false;
            Interlocked.Exchange(ref _keyDownTicks, 0);
        }

        if (!PsOn)
        {
            (PsStatus, PsLevel, PsFeedbackText) = ("PS off", "off", "");
            PsLocked = false;
            PsAdvice = "PureSignal cleans up your transmitted signal. Press PS CAL with a dummy load connected to set it up.";
            PsPeakText = "";
            return;
        }
        var f = _latestPs;
        int fb = (int)Math.Round(f.FeedbackLevel);
        PsFeedbackText = IsTransmitting ? $"FB {fb} · {_psAttnDb} dB" : "";
        if (_rekeyToApply)
            (PsStatus, PsLevel) = ("Re-key to apply", "warn");
        else if (_psStalled)
            (PsStatus, PsLevel) = ("Stalled: HW peak", "bad");
        else if (f.Correcting)
            (PsStatus, PsLevel) = ("Correcting", fb is >= 128 and <= 181 ? "ok" : "warn");
        else if (IsTransmitting)
            (PsStatus, PsLevel) = ("Calibrating", "warn");
        else
            (PsStatus, PsLevel) = ("Armed", "off");
        PsLocked = PsStatus == "Correcting";
        UpdatePsAdvice(f, fb);
    }

    private void UpdatePsAdvice(PsMetersFrame f, int fb)
    {
        double peak = MeasuredTxPeak, hw = _psHwPeak;
        PsPeakText = peak > 0 && hw > 0
            ? $"Signal peak {peak:0.000} · HW peak {hw:0.000} {(PsPeak.Suits(peak, hw) ? "✓" : "✗")}"
            : "";
        string source = _psExternal ? "amplifier sample" : "feedback";
        PsAdvice =
            PsCalibrating ? "Calibrating with 2-TONE in short bursts. This takes a few seconds."
            : _rekeyToApply ? "HW peak was changed to suit your signal. Stop transmitting, then press PS CAL or transmit again."
            : _psStalled && peak > 0 && hw > 0 && peak / hw > PsPeak.HighShare
                ? "Your signal goes above HW peak, so calibration can't finish. Press PS CAL to fix it."
            : _psStalled && peak > 0 && hw > 0 && peak / hw < PsPeak.LowShare
                ? "Your signal doesn't reach HW peak, so calibration can't finish. Press PS CAL to fix it."
            : _psStalled ? $"Calibration can't finish. Check the {source} cable and that the right feedback source is chosen in Config > PureSignal, then press PS CAL."
            : f.Correcting && IsTransmitting && fb > 181 ? $"The {source} is too strong (FB {fb}). It's best between 128 and 181. Add an attenuator in the sample line."
            : f.Correcting && IsTransmitting && fb is > 0 and < 128 ? $"The {source} is too weak (FB {fb}). It's best between 128 and 181. Check the sample cable and coupler."
            : f.Correcting ? "Working. The correction stays on while you talk. Run PS CAL again after changing band, power or amplifier."
            : IsTransmitting ? "Learning your signal. 2-TONE (or PS CAL) locks in a few seconds; voice alone usually can't."
            : "Ready. Press PS CAL with a dummy load connected to calibrate.";
    }
}
