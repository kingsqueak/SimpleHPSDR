using System.Diagnostics;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// Follows the engine's FFTW planning (its "wisdom" file). The engine does this
/// by itself on first start and after an update that changes its DSP libraries,
/// and won't connect to a radio until it's done. The result is cached, so a
/// normal start finishes before anyone would see a window.
/// </summary>
public sealed partial class DspPrepViewModel(StationClient client) : ObservableObject
{
    // WDSP plans sizes 64 to 262144: 13 sizes, 3 complex plans each, then 2 real.
    private const int Sizes = 13;
    private const int TotalSteps = Sizes * 5;

    // The large sizes take nearly all the time, so the bar follows a measured
    // run: (step, seconds when it began) on an M2 Max, 474 s in all. Other
    // machines differ in scale, not much in shape.
    private static readonly (int Step, double Seconds)[] Timing =
    [
        (1, 0), (18, 21), (28, 59), (33, 135), (34, 146), (35, 166), (36, 192), (37, 237),
        (38, 282), (39, 327), (40, 328), (58, 358), (60, 368), (62, 388), (64, 418), (65, 433),
        (66, 474),
    ];

    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _progress = "Starting…";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Working))] private bool _failed;
    [ObservableProperty] private string _failure = "";

    public bool Working => !Failed;

    /// <summary>The planning finished; the window can go.</summary>
    public event Action? Finished;

    /// <summary>Whether to show the window. A cached result is ready within a
    /// moment; an engine without this report is left to the connect wait.</summary>
    public async Task<bool> NeededAsync(CancellationToken ct = default)
    {
        for (int i = 0; i < 15; i++)
        {
            var p = await client.GetDspPrepAsync(ct);
            if (p is null || p.Ready) return false;
            if (p.Failed) return true;
            await Task.Delay(100, ct);
        }
        return true;
    }

    /// <summary>Report progress until the engine finishes or fails.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        int missed = 0;
        while (!ct.IsCancellationRequested)
        {
            var p = await client.GetDspPrepAsync(ct);
            if (p is null)
            {
                if (++missed >= 10) { Finished?.Invoke(); return; }
            }
            else
            {
                missed = 0;
                if (p.Ready) { Finished?.Invoke(); return; }
                if (p.Failed)
                {
                    Failed = true;
                    Failure = "The signal processing couldn't be prepared in advance, so the radio will take "
                            + $"longer to start and may drop out at first. {p.Status}".TrimEnd();
                    return;
                }
                if (StepOf(p.Status) is { } s) Percent = 100 * Fraction(s);
                Progress = Describe(Percent / 100, clock.Elapsed);
            }
            try { await Task.Delay(500, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static double Fraction(int step)
    {
        for (int i = 1; i < Timing.Length; i++)
            if (step < Timing[i].Step)
            {
                var (s0, t0) = Timing[i - 1];
                var (s1, t1) = Timing[i];
                return (t0 + (t1 - t0) * (step - s0) / (s1 - s0)) / Timing[^1].Seconds;
            }
        return 1;
    }

    private static string Describe(double done, TimeSpan elapsed)
    {
        string time = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00} so far";
        if (done < 0.05) return time;   // too early to judge this machine's speed
        double left = elapsed.TotalMinutes * (1 - done) / done;
        return left < 1 ? $"{time}, under a minute left" : $"{time}, about {Math.Ceiling(left):0} min left";
    }

    /// <summary>A short line for the status bar while Connect waits.</summary>
    public static string StatusLine(string status) =>
        StepOf(status) is { } s
            ? $"Preparing DSP, {100 * Fraction(s):0}% done (first start only)…"
            : "Preparing DSP (first start only)…";

    /// <summary>Which of the steps WDSP's status line is on, 1 upward, or null
    /// if the wording isn't recognised.</summary>
    internal static int? StepOf(string status)
    {
        var m = Regex.Match(status, @"Planning\s+(COMPLEX|REAL)\s+(FORWARD|BACKWARD|INVERSE)\s+FFT size (\d+)");
        if (!m.Success || !int.TryParse(m.Groups[3].Value, out int size)) return null;
        bool odd = size % 2 == 1;   // the extra complex plan at size + 1
        int k = (int)Math.Log2((odd ? size - 1 : size) / 64.0);
        if (k < 0 || k >= Sizes) return null;
        bool real = m.Groups[1].Value == "REAL";
        bool forward = m.Groups[2].Value == "FORWARD";
        return 1 + (real
            ? Sizes * 3 + k * 2 + (forward ? 0 : 1)
            : k * 3 + (forward ? 0 : odd ? 2 : 1));
    }
}
