namespace SimpleHPSDR;

/// <summary>
/// Transmit/receive timing defaults. Shortened in 0.10.7 once the engine's
/// release tail followed the speech (engine .30-.33): the old values were
/// workarounds for the old engine and cost about 400 ms per over.
/// </summary>
public static class TimingDefaults
{
    /// <summary>PTT release delay: a short extra wait after the speech in the TX chain has gone out.</summary>
    public const int ReleaseDelayMs = 60;
    /// <summary>RX resume: one 33 ms receive block, enough to hide the T/R switch click.</summary>
    public const int RxResumeDelayMs = 33;
    /// <summary>VOX delay: below about 350 ms the last word is still leaving the TX chain anyway.</summary>
    public const double VoxDelayMs = 300;

    // The defaults before 0.10.7. Values still exactly on these are moved once.
    public const int OldReleaseDelayMs = 275;
    public const int OldRxResumeDelayMs = 200;
    public const int OlderRxResumeDelayMs = 100;
    public const double OldVoxDelayMs = 1000;
}
