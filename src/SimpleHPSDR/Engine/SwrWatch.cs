namespace SimpleHPSDR.Engine;

/// <summary>
/// The user's own SWR levels, watched from the TX meters. It times things
/// the way the engine's SWR trip does: 300 ms grace after keying, then the
/// SWR has to stay above a level for 500 ms.
/// </summary>
public sealed class SwrWatch
{
    /// <summary>The engine always stops MOX here and can't be told otherwise,
    /// so a limit can only be below it.</summary>
    public const double EngineLimit = 2.5;

    private const long GraceMs = 300, SustainMs = 500;

    public enum Result { None, Warn, Stop }

    private long _keyedMs = -1, _warnSince = -1, _stopSince = -1;

    public void Reset() => _keyedMs = _warnSince = _stopSince = -1;

    public Result Observe(double swr, double warnAt, double stopAt, long nowMs)
    {
        if (_keyedMs < 0) _keyedMs = nowMs;
        if (nowMs - _keyedMs < GraceMs) return Result.None;
        if (stopAt < EngineLimit && Sustained(swr > stopAt, ref _stopSince, nowMs)) return Result.Stop;
        return Sustained(swr > warnAt, ref _warnSince, nowMs) ? Result.Warn : Result.None;
    }

    private static bool Sustained(bool above, ref long since, long nowMs)
    {
        if (!above)
        {
            since = -1;
            return false;
        }
        if (since < 0) since = nowMs;
        return nowMs - since >= SustainMs;
    }
}
