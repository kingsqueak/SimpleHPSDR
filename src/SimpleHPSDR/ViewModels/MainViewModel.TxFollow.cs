using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>TX filter follows the RX filter (Configuration > TX Audio): the
/// TX cuts track the transmitting slice's RX filter. The engine has no such
/// mode, so the app copies it on every filter, mode or TX-slice change and
/// checks it in the 1 s poll (the engine also restores a per-mode TX filter
/// on its own). Sideband modes only: AM, FM and CW filters aren't shaped
/// like a TX passband.</summary>
public sealed partial class MainViewModel
{
    private bool _following;

    private async Task FollowRxFilterAsync(StateDto? state = null)
    {
        if (!_settings.TxFilterFollowsRx || !IsConnected || _following) return;
        _following = true;
        try
        {
            var s = state ?? await _client!.GetStateAsync();
            var (mode, low, high) = s.TxReceiverIndex == 1 && s.Receivers?.FirstOrDefault(r => r.Index == 1) is { } b
                ? (b.Mode, b.FilterLowHz, b.FilterHighHz)
                : (s.Mode, s.FilterLowHz, s.FilterHighHz);
            if (mode is not (RxMode.LSB or RxMode.USB or RxMode.DIGL or RxMode.DIGU)) return;
            int lo = Math.Min(Math.Abs(low), Math.Abs(high)), hi = Math.Max(Math.Abs(low), Math.Abs(high));
            int txLo = Math.Min(Math.Abs(s.TxFilterLowHz), Math.Abs(s.TxFilterHighHz));
            int txHi = Math.Max(Math.Abs(s.TxFilterLowHz), Math.Abs(s.TxFilterHighHz));
            if ((lo, hi) != (txLo, txHi) && hi - lo >= 50)
                await _client!.SetTxFilterAsync(mode, lo, hi);
        }
        catch (Exception) { }
        finally
        {
            _following = false;
        }
    }
}
