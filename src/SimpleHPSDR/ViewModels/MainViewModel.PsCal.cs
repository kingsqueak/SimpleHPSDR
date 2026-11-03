using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// PS CAL: key 2-TONE in bursts until PureSignal calibrates. Each cycle keys
/// for 3-5 s, then unkeys. If calcc reached Correcting and HW peak
/// suits the signal, it's done. Otherwise it moves HW peak toward the
/// measured peak (the engine applies PureSignal changes on unkey) and keys
/// again. At most four cycles.
/// </summary>
public sealed partial class MainViewModel
{
    private const int PsCalCycles = 4;
    // A real fit lands about 2-2.5 s after key-down on a G2, and the
    // Correcting flag can flicker before that (the previous curve is still
    // loaded for a moment after a reset). So stay keyed at least 3 s before
    // judging, and up to 5 s for a slow lock.
    private static readonly TimeSpan PsCalMinKey = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PsCalMaxKey = TimeSpan.FromSeconds(5);
    private const double PsCalMaxSwr = 2.5;

    private CancellationTokenSource? _psCalCts;

    [ObservableProperty] private bool _psCalibrating;

    // Concurrent so the button stays live while running: a second press cancels.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CalibratePsAsync()
    {
        if (_psCalCts is not null)          // pressed again: cancel
        {
            _psCalCts.Cancel();
            return;
        }
        if (!IsConnected || _client is null) return;
        if (IsTransmitting)
        {
            Status = "PS CAL: unkey first.";
            return;
        }

        var client = _client;
        using var cts = _psCalCts = new CancellationTokenSource();
        PsCalibrating = true;
        try
        {
            if (!PsOn)
            {
                await client.SetPsAsync(true);
                _applyingTx = true;
                PsOn = true;
                _applyingTx = false;
            }

            for (int cycle = 1; cycle <= PsCalCycles; cycle++)
            {
                // Start from a clean slate so any "Correcting" seen in this
                // burst is a fresh fit at the current drive, not the flag left
                // over from an earlier calibration. Applied at once (unkeyed).
                await client.ResetPsAsync();
                _latestPs = default;
                ResetFreshFit();
                await Task.Delay(150, cts.Token);

                Status = $"PS CAL: cycle {cycle} of {PsCalCycles}, keying 2-TONE…";
                await client.SetTwoToneAsync(true);
                var outcome = await WatchCalibrationAsync(cts.Token);
                await client.SetTwoToneAsync(false);

                if (outcome == CalOutcome.HighSwr)
                {
                    Status = $"PS CAL stopped: SWR above {PsCalMaxSwr:0.0}:1. Check the load.";
                    return;
                }

                // Bring HW peak in line with the signal; the engine applies it
                // as we unkey. Calibrated with a HW peak that then needed
                // raising: recalibrate with the new one.
                var s = await client.GetStateAsync(cts.Token);
                bool calibrated = outcome == CalOutcome.Correcting;
                if (HwPeakTarget(s.PsHwPeak, stuck: !calibrated) is { } target)
                {
                    await client.SetPsAdvancedAsync(new PsAdvancedSetRequest(HwPeak: target));
                    Status = $"PS CAL: HW peak {s.PsHwPeak:0.0000} → {target:0.0000}, trying again…";
                }
                else if (calibrated)
                {
                    var f = _latestPs;
                    Status = $"PS CAL: calibrated in {cycle} cycle{(cycle > 1 ? "s" : "")}. " +
                             $"Feedback {f.FeedbackLevel:0}" +
                             (f.FeedbackLevel is >= 128 and <= 181 ? " (good)" : " (outside 128-181)") +
                             $", correction {f.CorrectionDb:0.0} dB, HW peak {s.PsHwPeak:0.0000}.";
                    return;
                }
                await Task.Delay(700, cts.Token);   // let the unkey and deferred changes land
            }
            Status = $"PS CAL: not calibrated after {PsCalCycles} cycles. Check drive, feedback source and HW peak.";
        }
        catch (OperationCanceledException)
        {
            Status = "PS CAL cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"PS CAL failed: {ex.Message}";
        }
        finally
        {
            // Whatever happened, leave the radio unkeyed.
            try { await client.SetTwoToneAsync(false); } catch (Exception) { }
            _psCalCts = null;
            PsCalibrating = false;
            UpdatePsStatus();
        }
    }

    private enum CalOutcome { Correcting, StillCollecting, HighSwr }

    private async Task<CalOutcome> WatchCalibrationAsync(CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < PsCalMaxKey)
        {
            await Task.Delay(100, ct);
            if (IsTransmitting && _latestTx.Swr > PsCalMaxSwr && _latestTx.FwdWatts > 1) return CalOutcome.HighSwr;
            if (DateTime.UtcNow - start >= PsCalMinKey && Locked()) return CalOutcome.Correcting;
        }
        return Locked() ? CalOutcome.Correcting : CalOutcome.StillCollecting;

        // Correcting now, after having seen the reset take effect.
        bool Locked() => _psSawNotCorrecting && _latestPs.Correcting;
    }

    /// <summary>Any key action by the operator ends a running PS CAL.</summary>
    private void CancelPsCalForOperatorKey() => _psCalCts?.Cancel();
}
