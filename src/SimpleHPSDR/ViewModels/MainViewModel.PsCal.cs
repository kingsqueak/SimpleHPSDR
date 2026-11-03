using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// PS CAL: key 2-TONE in short bursts until PureSignal calibrates. Each cycle
/// keys for up to 3 s, then unkeys. If calcc reached Correcting and HW peak
/// suits the signal, it's done. Otherwise it moves HW peak toward the
/// measured peak (the engine applies PureSignal changes on unkey) and keys
/// again. At most four cycles.
/// </summary>
public sealed partial class MainViewModel
{
    private const int PsCalCycles = 4;
    private static readonly TimeSpan PsCalKeyWindow = TimeSpan.FromSeconds(3);
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
        var until = DateTime.UtcNow + PsCalKeyWindow;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(100, ct);
            if (_latestPs.Correcting) return CalOutcome.Correcting;
            if (IsTransmitting && _latestTx.Swr > PsCalMaxSwr && _latestTx.FwdWatts > 1) return CalOutcome.HighSwr;
        }
        return _latestPs.Correcting ? CalOutcome.Correcting : CalOutcome.StillCollecting;
    }

    /// <summary>Any key action by the operator ends a running PS CAL.</summary>
    private void CancelPsCalForOperatorKey() => _psCalCts?.Cancel();
}
