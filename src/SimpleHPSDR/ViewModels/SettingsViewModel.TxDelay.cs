using SimpleHPSDR.Engine;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>The estimated transmit delay, shown on the TX Audio and Filters
/// pages and updated as the settings that drive it change.</summary>
public sealed partial class SettingsViewModel
{
    private int _txDspRateHz = 96_000;

    private static readonly HashSet<string> TxDelayInputs =
    [
        nameof(TxWindowIndex), nameof(TxPhaseIndex), nameof(CfcEnabled), nameof(CfcFftSize),
        nameof(CompressorEnabled), nameof(CessbEnabled), nameof(DexpEnabled), nameof(ReleaseDelayMs),
    ];

    public string TxDelayText
    {
        get
        {
            var e = TxLatency.Calculate(TxWindowIndex, (FilterPhaseMode)TxPhaseIndex, _txDspRateHz,
                CompressorEnabled, CessbEnabled, CfcEnabled, CfcFftSize,
                DexpEnabled && _txLoaded?.Dexp is { LookAheadEnabled: true } dexp ? (int)dexp.LookAheadMs : 0);
            return $"Estimated transmit delay: {e.TotalMs} ms (CFC {e.CfcMs}, TX filter {e.FilterMs}, "
                + $"other {e.OtherPartMs}). Up to {TxLatency.ReleaseHoldMs(e, ReleaseDelayMs)} ms keyed after release.";
        }
    }

    private void UpdateTxDelay(string? changed)
    {
        if (changed is not null && TxDelayInputs.Contains(changed))
            OnPropertyChanged(nameof(TxDelayText));
    }

    private async Task LoadTxDelayAsync()
    {
        _txDspRateHz = await _client.GetTxDspRateAsync();
        OnPropertyChanged(nameof(TxDelayText));
    }
}
