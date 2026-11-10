using Zeus.Contracts;

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
