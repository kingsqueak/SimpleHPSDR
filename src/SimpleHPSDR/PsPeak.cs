namespace SimpleHPSDR;

/// <summary>
/// Where PureSignal's HW peak should sit against the signal. WDSP's calcc
/// sorts the transmit signal into 16 level steps up to HW peak and calibrates
/// only once every step, the top one (93.75-100% of HW peak) included, holds
/// 256 samples within 5 s. So the signal's real peak must land in that top
/// step: a HW peak at or under the peak puts the loudest samples over range,
/// and one more than about 6% above it leaves the top step empty.
/// </summary>
public static class PsPeak
{
    /// <summary>The signal peak's share of HW peak that needs no change.</summary>
    public const double LowShare = 0.94, HighShare = 1.0;

    /// <summary>Where a new HW peak puts the signal's peak.</summary>
    private const double TargetShare = 0.97;

    public static double Target(double measuredPeak) => Math.Round(measuredPeak / TargetShare, 4);

    /// <summary>The signal peak's share of HW peak is in the good window.</summary>
    public static bool Suits(double measuredPeak, double hwPeak) =>
        measuredPeak > 0 && hwPeak > 0 && measuredPeak / hwPeak is >= LowShare and <= HighShare;
}
