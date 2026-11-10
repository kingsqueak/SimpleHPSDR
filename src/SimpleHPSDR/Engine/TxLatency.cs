using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Engine;

/// <summary>
/// How long speech takes to get through the transmit chain, for Config. This
/// follows the engine's own bound (TxaVoiceLatencyModel, PlanVoiceTail), which
/// it uses to hold transmit after release. Each linear filter stage adds half
/// its taps at the DSP rate, a minimum-phase filter two DSP blocks, and CFC
/// its FFT size less one block.
/// </summary>
public static class TxLatency
{
    private const int DspSize = 512, OtherMs = 35;
    private const int SettleMinMs = 20, SettleMaxMs = 200, RadioFifoGuardMs = 30;

    public sealed record Estimate(int CfcMs, int FilterMs, int ExtraMs)
    {
        public int TotalMs => CfcMs + FilterMs + ExtraMs + OtherMs;
        public int OtherPartMs => OtherMs + ExtraMs;
    }

    /// <summary>Soft is 1k taps, and each step up doubles it.</summary>
    public static int Taps(int shapeIndex) => 1024 << Math.Clamp(shapeIndex, 0, 8);

    public static Estimate Calculate(int shapeIndex, FilterPhaseMode phase, int dspRateHz,
        bool compressor, bool cessb, bool cfc, int cfcFftSize, int lookAheadMs)
    {
        int stages = 1 + (compressor ? 1 : 0) + (compressor && cessb ? 1 : 0);
        int filterMs = phase == FilterPhaseMode.Linear
            ? stages * (int)Math.Ceiling((Taps(shapeIndex) - 1) * 500.0 / dspRateHz)
            : (int)Math.Ceiling(2.0 * DspSize * 1000.0 / dspRateHz);
        int cfcMs = cfc ? (int)Math.Ceiling((cfcFftSize - DspSize) * 1000.0 / dspRateHz) : 0;
        return new Estimate(cfcMs, filterMs, lookAheadMs);
    }

    /// <summary>How long transmit stays keyed after release, at most.</summary>
    public static int ReleaseHoldMs(Estimate e, double releaseDelayMs) =>
        e.TotalMs + Math.Clamp((int)releaseDelayMs / 3, SettleMinMs, SettleMaxMs) + RadioFifoGuardMs;
}
