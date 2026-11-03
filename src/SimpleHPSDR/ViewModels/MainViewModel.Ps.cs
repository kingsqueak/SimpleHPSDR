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
    /// none yet). PureSignal's HW peak must not be above this, or calibration
    /// never collects its top bin and stalls.</summary>
    public double MeasuredTxPeak { get; private set; }

    // The engine's MaxTxEnvelope is a peak held for 2 s, and key-on carries a
    // transient (0.310 against a steady 0.284 on a G2), so only readings from
    // this long after key-down describe the steady signal.
    private static readonly TimeSpan PeakSettle = TimeSpan.FromSeconds(2.2);
    private long _keyDownTicks;              // UTC ticks of the latest key-down, 0 when unkeyed

    // A fresh fit is a not-correcting frame followed by a correcting one in
    // the same key-down. The first frame after key-down repeats the engine's
    // cached meters from the previous transmission (Correcting included), so
    // a bare "Correcting" proves nothing.
    private volatile bool _psSawNotCorrecting;
    private volatile bool _psFreshFit;

    private void ResetFreshFit()
    {
        _psSawNotCorrecting = false;
        _psFreshFit = false;
    }

    private DateTime? _keyedSince;
    private bool _rekeyToApply;
    private bool _hwPeakAdjustedThisTx;
    private const byte CalStateCollect = 4;   // calcc LCOLLECT

    [ObservableProperty] private bool _psOn;
    [ObservableProperty] private bool _twoToneOn;
    [ObservableProperty] private string _psStatus = "Off";
    /// <summary>off, ok, warn or bad: picks the badge colour.</summary>
    [ObservableProperty] private string _psLevel = "off";
    [ObservableProperty] private string _psFeedbackText = "";

    private void WirePureSignal(StationClient client)
    {
        client.PsMeters += f =>
        {
            _latestPs = f;
            long keyDown = Interlocked.Read(ref _keyDownTicks);
            if (keyDown != 0)
            {
                if (!f.Correcting) _psSawNotCorrecting = true;
                else if (_psSawNotCorrecting) _psFreshFit = true;
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
        _applyingTx = true;
        PsOn = s.PsEnabled;
        TwoToneOn = s.TwoToneEnabled;
        _applyingTx = false;
        _psStalled = s.PsCalibrationStalled;
        UpdatePsStatus();
    }

    /// <summary>From the 1 s state poll: two-tone and the stall flag have no frame.</summary>
    private void PollPureSignal(StateDto s)
    {
        _applyingTx = true;
        TwoToneOn = s.TwoToneEnabled;
        PsOn = s.PsEnabled;
        _applyingTx = false;
        _psStalled = s.PsCalibrationStalled;
        UpdatePsStatus();
        _ = MaybeAdjustHwPeakAsync(s);
    }

    // Auto HW peak: calcc sorts samples into amplitude bins up to HW peak and
    // only calculates once the top bin has some. HW peak should sit just
    // under the signal's real peak:
    //  - above it, calibration never fills the top bin and sits in LCOLLECT,
    //    so lower it, but only once calibration is actually stuck;
    //  - well below it, the peaks the amplifier distorts most are left out
    //    of the fit, so raise it.
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
    /// Raises whenever the signal clearly exceeds it; lowers only when
    /// calibration is stuck. 98% of the measured peak, so the top bin fills.</summary>
    private double? HwPeakTarget(double current, bool stuck)
    {
        if (MeasuredTxPeak <= 0) return null;
        double target = Math.Round(MeasuredTxPeak * 0.98, 4);
        if (MeasuredTxPeak > current * 1.05) return target;
        if (stuck && target < current * 0.95) return target;
        return null;
    }

    partial void OnPsOnChanged(bool value)
    {
        UpdatePsStatus();
        if (_applyingTx || !IsConnected) return;
        _ = Try(() => _client!.SetPsAsync(value));
    }

    partial void OnTwoToneOnChanged(bool value)
    {
        if (_applyingTx || !IsConnected) return;
        CancelPsCalForOperatorKey();
        _ = KeyAsync(() => _client!.SetTwoToneAsync(value), value);
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
            Interlocked.Exchange(ref _keyDownTicks, 0);
        }

        if (!PsOn)
        {
            (PsStatus, PsLevel, PsFeedbackText) = ("PS off", "off", "");
            return;
        }
        var f = _latestPs;
        int fb = (int)Math.Round(f.FeedbackLevel);
        PsFeedbackText = IsTransmitting ? $"FB {fb}" : "";
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
    }
}
