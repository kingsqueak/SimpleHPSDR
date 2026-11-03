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
            if (f.MaxTxEnvelope > 0) MeasuredTxPeak = f.MaxTxEnvelope;
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
        _ = MaybeLowerHwPeakAsync(s);
    }

    // Auto HW peak: calcc sorts samples into amplitude bins up to HW peak and
    // only calculates once the top bin has some. If the signal never gets
    // that high, calibration sits in LCOLLECT forever. When that happens
    // while keyed, lower HW peak to just under the measured peak. Only ever
    // lowers, at most once per transmission. The engine holds PureSignal
    // changes until unkey (changing calcc mid-transmission can splatter), so
    // the new value applies from the next key-down; the badge says so.
    private async Task MaybeLowerHwPeakAsync(StateDto s)
    {
        // PS CAL makes its own HW peak changes between bursts.
        if (PsCalibrating || !_settings.AutoHwPeak || !PsOn || _hwPeakAdjustedThisTx || _keyedSince is not { } since) return;
        bool stuck = s.PsCalibrationStalled
                     || (_latestPs.CalState == CalStateCollect && !_latestPs.Correcting
                         && DateTime.UtcNow - since > TimeSpan.FromSeconds(3));
        double target = Math.Round(MeasuredTxPeak * 0.98, 4);
        if (!stuck || MeasuredTxPeak <= 0 || target >= s.PsHwPeak * 0.95) return;

        _hwPeakAdjustedThisTx = true;
        await Try(async () =>
        {
            await _client!.SetPsAdvancedAsync(new PsAdvancedSetRequest(HwPeak: target));
            _rekeyToApply = true;
            UpdatePsStatus();
            Status = $"PureSignal: HW peak {s.PsHwPeak:0.0000} → {target:0.0000} to match your signal. Unkey and key again to calibrate.";
        });
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
        if (!PsOn)
        {
            _keyedSince = null;
            (PsStatus, PsLevel, PsFeedbackText) = ("PS off", "off", "");
            return;
        }
        if (IsTransmitting)
        {
            // A fresh key-down after an adjustment runs with the new value.
            if (_keyedSince is null) _rekeyToApply = false;
            _keyedSince ??= DateTime.UtcNow;
        }
        else
        {
            _keyedSince = null;
            _hwPeakAdjustedThisTx = false;
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
