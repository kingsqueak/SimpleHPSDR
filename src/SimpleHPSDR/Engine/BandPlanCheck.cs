using Zeus.Contracts;

namespace SimpleHPSDR.Engine;

/// <summary>
/// Is the transmit signal inside the band plan? For Warn only, where the
/// engine's guard is off and this app has to look for itself. It follows the
/// engine's rule (the whole signal inside an amateur segment that allows the
/// mode), but the engine's own check stays the one that blocks.
/// </summary>
public static class BandPlanCheck
{
    public static bool Inside(IEnumerable<BandSegment> plan, long lowHz, long highHz, RxMode mode) =>
        plan.Any(s => s.Allocation == BandAllocation.Amateur && Allows(s.ModeRestriction, mode)
                      && lowHz >= s.LowHz && highHz <= s.HighHz);

    private static bool Allows(ModeRestriction restriction, RxMode mode) => restriction switch
    {
        ModeRestriction.Any => true,
        ModeRestriction.CwOnly => mode is RxMode.CWU or RxMode.CWL,
        ModeRestriction.PhoneOnly => mode is RxMode.USB or RxMode.LSB or RxMode.AM or RxMode.SAM or RxMode.DSB or RxMode.FM,
        ModeRestriction.DigitalOnly => mode is RxMode.DIGL or RxMode.DIGU,
        ModeRestriction.CwAndDigital => mode is RxMode.CWU or RxMode.CWL or RxMode.DIGL or RxMode.DIGU,
        _ => false,
    };

    /// <summary>The TX receiver's mode and the edges of the transmitted
    /// signal, in Hz. A tune carrier or CW is just the carrier.</summary>
    public static (RxMode Mode, long LowHz, long HighHz) Emission(StateDto s, bool tune)
    {
        var rx = s.TxReceiverIndex > 0
            ? s.Receivers?.FirstOrDefault(r => r.Index == s.TxReceiverIndex)
            : null;
        var (mode, vfo, split, txVfo) = rx is { } r
            ? (r.Mode, r.VfoHz, r.SplitEnabled, r.TxVfoHz)
            : (s.Mode, s.VfoHz, s.SplitEnabled, s.SplitTxHz);

        long carrier = split
            ? (txVfo > 0 ? txVfo : vfo)
            : vfo + (mode == RxMode.FM ? (s.Fm ?? FmConfig.Default).SignedRepeaterOffsetHz(vfo) : 0);
        if (s.XitEnabled) carrier += s.XitHz;
        if (tune || mode is RxMode.CWU or RxMode.CWL) return (mode, carrier, carrier);

        int lo = Math.Min(Math.Abs(s.TxFilterLowHz), Math.Abs(s.TxFilterHighHz));
        int hi = Math.Max(Math.Abs(s.TxFilterLowHz), Math.Abs(s.TxFilterHighHz));
        if (mode == RxMode.FM)
        {
            var fm = (s.Fm ?? FmConfig.Default).Normalized();
            hi = fm.DeviationHz + fm.TxHighCutHz;
        }
        return mode switch
        {
            RxMode.USB => (mode, carrier + lo, carrier + hi),
            RxMode.DIGU => (mode, carrier, carrier + hi),
            RxMode.LSB => (mode, carrier - hi, carrier - lo),
            RxMode.DIGL => (mode, carrier - hi, carrier),
            _ => (mode, carrier - hi, carrier + hi),
        };
    }
}
