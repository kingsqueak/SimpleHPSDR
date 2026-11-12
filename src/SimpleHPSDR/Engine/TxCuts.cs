using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Engine;

/// <summary>
/// The transmit low and high cut as the operator sets them, read from the
/// engine's state. In USB/LSB/DIGU/DIGL they are the passband's edges. In AM
/// and SAM the engine reports the passband as -high..+high (both sidebands) and
/// keeps the low cut apart, as the modulator's high-pass (TxLowCutHz); FM keeps
/// both in its own config. Reading AM's edges as cuts gave a low cut equal to
/// the high cut (5000/5000), which then went into saved TX profiles.
/// </summary>
public static class TxCuts
{
    public static RxMode TxMode(StateDto s) =>
        s.TxReceiverIndex == 1 && s.Receivers?.FirstOrDefault(r => r.Index == 1) is { } b ? b.Mode : s.Mode;

    /// <summary>Send operator cuts for the transmitting slice's mode (slice B's
    /// when B transmits), with DSB's passband on both sides of the carrier.</summary>
    public static async Task SendAsync(StationClient client, int low, int high)
    {
        var mode = TxMode(await client.GetStateAsync());
        if (mode == RxMode.DSB) await client.SetTxFilterAsync(mode, -high, high);
        else await client.SetTxFilterAsync(mode, low, high);
    }

    public static (int Low, int High) Of(StateDto s)
    {
        int hi = Math.Max(Math.Abs(s.TxFilterLowHz), Math.Abs(s.TxFilterHighHz));
        return TxMode(s) switch
        {
            RxMode.AM or RxMode.SAM => (Math.Clamp(s.TxLowCutHz, 0, hi), hi),
            RxMode.FM when s.Fm is { } fm => (fm.TxLowCutHz, fm.TxHighCutHz),
            _ => (Math.Min(Math.Abs(s.TxFilterLowHz), Math.Abs(s.TxFilterHighHz)), hi),
        };
    }
}
