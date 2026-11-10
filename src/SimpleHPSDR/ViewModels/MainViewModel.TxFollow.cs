using SimpleHPSDR.Engine;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>Keeps the TX filter the same in every mode. With "TX filter follows the RX
/// filter" (Configuration > TX Audio) the sideband modes track the transmitting slice's RX
/// filter, and AM, SAM and DSB take half its width as their high cut: the engine has no
/// such mode, so the app copies it on every filter, mode or
/// TX-slice change and checks it in the 1 s poll. Otherwise the profile's cuts apply. The
/// engine shares one set of cuts among USB, LSB, the digital modes, AM and SAM, but keeps
/// FM and DSB apart and only sets the mode in use, so those get the cuts when they're
/// selected. CW and FreeDV don't use an audio passband.</summary>
public sealed partial class MainViewModel
{
    private bool _following;
    /// <summary>The last cuts sent per mode: an engine limit mustn't cause a retry every second.</summary>
    private (RxMode Mode, int Low, int High)? _cutsSent;

    private async Task FollowRxFilterAsync(StateDto? state = null)
    {
        if (!IsConnected || _following || IsTransmitting) return;
        _following = true;
        try
        {
            var s = state ?? await _client!.GetStateAsync();
            var (mode, low, high) = s.TxReceiverIndex == 1 && s.Receivers?.FirstOrDefault(r => r.Index == 1) is { } b
                ? (b.Mode, b.FilterLowHz, b.FilterHighHz)
                : (s.Mode, s.FilterLowHz, s.FilterHighHz);
            bool sideband = mode is RxMode.LSB or RxMode.USB or RxMode.DIGL or RxMode.DIGU;
            var (txLo, txHi) = TxCuts.Of(s);
            if (_settings.TxFilterFollowsRx && sideband)
            {
                int lo = Math.Min(Math.Abs(low), Math.Abs(high)), hi = Math.Max(Math.Abs(low), Math.Abs(high));
                if ((lo, hi) != (txLo, txHi) && hi - lo >= 50)
                    await _client!.SetTxFilterAsync(mode, lo, hi);
                return;
            }
            // AM, SAM and DSB: the RX filter spans both sidebands, so the audio the
            // transmitter sends reaches half its width (a 10 kHz AM filter gives a
            // 5 kHz high cut). The low cut stays where the profile put it.
            if (_settings.TxFilterFollowsRx && mode is RxMode.AM or RxMode.SAM or RxMode.DSB)
            {
                int half = Math.Max(Math.Abs(low), Math.Abs(high));
                int lowCut = ProfileCuts()?.Low ?? txLo;
                if (half - lowCut < 50 || half == txHi) return;
                if (mode == RxMode.DSB) await _client!.SetTxFilterAsync(mode, -half, half);
                else await _client!.SetTxFilterAsync(mode, lowCut, half);
                return;
            }
            if (ProfileCuts() is not { } cuts) return;
            bool differs = mode switch
            {
                RxMode.LSB or RxMode.USB or RxMode.DIGL or RxMode.DIGU => (txLo, txHi) != cuts,
                RxMode.AM or RxMode.SAM or RxMode.FM or RxMode.DSB => txHi != cuts.High,
                _ => false,
            };
            if (!differs || _cutsSent == (mode, cuts.Low, cuts.High)) return;
            _cutsSent = (mode, cuts.Low, cuts.High);
            // DSB takes the passband itself, on both sides of the carrier.
            if (mode == RxMode.DSB) await _client!.SetTxFilterAsync(mode, -cuts.High, cuts.High);
            else await _client!.SetTxFilterAsync(mode, cuts.Low, cuts.High);
        }
        catch (Exception) { }
        finally
        {
            _following = false;
        }
    }

    /// <summary>The cuts to hold, starting from the last profile if none are saved yet.</summary>
    private (int Low, int High)? ProfileCuts()
    {
        if (_settings.TxLowCutHz is null && _settings.LastTxProfile is { } name && TxProfileStore.Get(name) is { } p)
        {
            (_settings.TxLowCutHz, _settings.TxHighCutHz) = (p.LowCutHz, p.HighCutHz);
            _settings.Save();
        }
        return _settings is { TxLowCutHz: { } lo, TxHighCutHz: { } hi } ? (lo, hi) : null;
    }
}
