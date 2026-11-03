using System.Diagnostics;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR;

/// <summary>
/// Display pipeline timing, written to ui-stats.log every 5 s when
/// SIMPLEHPSDR_STATS=1: frames off the socket, frames applied on the UI
/// thread, renders, and the worst gap at each stage.
/// </summary>
public static class UiStats
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SIMPLEHPSDR_STATS") == "1";
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Lock Gate = new();
    private static Stage _received, _applied, _rendered;
    private static int _dropped;
    private static double _windowStart;

    private struct Stage
    {
        public int Count;
        public double Last, MaxGap;
        public void Hit(double now)
        {
            if (Last > 0) MaxGap = Math.Max(MaxGap, now - Last);
            Last = now;
            Count++;
        }
    }

    public static void FrameReceived() { if (Enabled) lock (Gate) _received.Hit(Clock.Elapsed.TotalMilliseconds); }
    public static void FrameApplied() { if (Enabled) lock (Gate) _applied.Hit(Clock.Elapsed.TotalMilliseconds); }
    public static void FrameDropped() { if (Enabled) Interlocked.Increment(ref _dropped); }

    public static void Rendered()
    {
        if (!Enabled) return;
        double now = Clock.Elapsed.TotalMilliseconds;
        string? line = null;
        lock (Gate)
        {
            _rendered.Hit(now);
            if (now - _windowStart >= 5000)
            {
                line = $"{DateTime.Now:HH:mm:ss} recv={_received.Count} maxGap={_received.MaxGap:F0}ms  " +
                       $"applied={_applied.Count} maxGap={_applied.MaxGap:F0}ms  " +
                       $"renders={_rendered.Count} maxGap={_rendered.MaxGap:F0}ms  dropped={_dropped}";
                _received = _received with { Count = 0, MaxGap = 0 };
                _applied = _applied with { Count = 0, MaxGap = 0 };
                _rendered = _rendered with { Count = 0, MaxGap = 0 };
                _dropped = 0;
                _windowStart = now;
            }
        }
        if (line is not null)
            try { File.AppendAllText(Path.Combine(EngineHost.DataDir, "ui-stats.log"), line + Environment.NewLine); }
            catch (IOException) { }
    }
}
