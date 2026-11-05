using System.Numerics;

namespace SimpleHPSDR.Dsp;

public enum EqBandType { Peak, HighPass, LowPass, LowShelf, HighShelf }

/// <summary>One band. Shape is the Q of a peak or shelf, or the dB per octave
/// of a pass filter (a multiple of 12).</summary>
public sealed record ParametricBand(bool On, EqBandType Type, double Hz, double GainDb, double Shape);

public sealed record ParametricEqSettings(bool Enabled, ParametricBand[] Bands)
{
    public static ParametricEqSettings Default => new(false,
    [
        new(true, EqBandType.Peak, 100, 0, 1), new(true, EqBandType.Peak, 300, 0, 1),
        new(true, EqBandType.Peak, 800, 0, 1), new(true, EqBandType.Peak, 1500, 0, 1),
        new(true, EqBandType.Peak, 2500, 0, 1), new(true, EqBandType.Peak, 3500, 0, 1),
    ]);
}

/// <summary>
/// The engine's TX EQ is one fixed filter drawn through ten points, joined
/// with straight lines in dB over linear frequency, and no Q or filter types.
/// So the parametric curve is worked out here and the ten points are placed
/// where they follow it best. The DSP load is that of the ordinary EQ. A
/// narrow band can lose some depth, and the engine stops at ±24 dB.
/// </summary>
public static class ParametricEq
{
    public const double MinHz = 20, MaxHz = 8000, LimitDb = 24;
    private const double Rate = 48000;
    private const int Points = 10, GridSize = 480;

    private static readonly double[] Grid = BuildGrid();

    private static double[] BuildGrid()
    {
        var g = new double[GridSize];
        for (int i = 0; i < GridSize; i++)
            g[i] = MinHz * Math.Pow(MaxHz / MinHz, i / (GridSize - 1.0));
        return g;
    }

    /// <summary>The whole curve at one frequency, limited as the engine limits it.</summary>
    public static double ResponseDb(IEnumerable<ParametricBand> bands, double hz)
    {
        double db = 0;
        foreach (var b in bands)
            if (b.On) db += BandDb(b, hz);
        return Math.Clamp(db, -LimitDb, LimitDb);
    }

    private static double BandDb(ParametricBand b, double hz)
    {
        double w0 = 2 * Math.PI * Math.Clamp(b.Hz, 10, Rate * 0.45) / Rate;
        double c = Math.Cos(w0), s = Math.Sin(w0);
        Complex z = Complex.FromPolarCoordinates(1, -2 * Math.PI * hz / Rate);

        switch (b.Type)
        {
            case EqBandType.HighPass or EqBandType.LowPass:
            {
                // Butterworth: one second-order stage for each 12 dB per octave.
                bool high = b.Type == EqBandType.HighPass;
                int stages = Math.Clamp((int)Math.Round(b.Shape / 12), 1, 4);
                double db = 0;
                for (int k = 0; k < stages; k++)
                {
                    double q = 1 / (2 * Math.Cos(Math.PI * (2 * k + 1) / (4 * stages)));
                    double al = s / (2 * q);
                    double b0 = high ? (1 + c) / 2 : (1 - c) / 2;
                    double b1 = high ? -(1 + c) : 1 - c;
                    db += Gain(z, b0, b1, b0, 1 + al, -2 * c, 1 - al);
                }
                return db;
            }
            case EqBandType.LowShelf or EqBandType.HighShelf:
            {
                double a = Math.Pow(10, b.GainDb / 40), al = s / (2 * Math.Max(b.Shape, 0.1)), r = 2 * Math.Sqrt(a) * al;
                double sign = b.Type == EqBandType.LowShelf ? 1 : -1;
                return Gain(z,
                    a * (a + 1 - sign * (a - 1) * c + r), 2 * sign * a * (a - 1 - sign * (a + 1) * c),
                    a * (a + 1 - sign * (a - 1) * c - r),
                    a + 1 + sign * (a - 1) * c + r, -2 * sign * (a - 1 + sign * (a + 1) * c),
                    a + 1 + sign * (a - 1) * c - r);
            }
            default:
            {
                double a = Math.Pow(10, b.GainDb / 40), al = s / (2 * Math.Max(b.Shape, 0.1));
                return Gain(z, 1 + al * a, -2 * c, 1 - al * a, 1 + al / a, -2 * c, 1 - al / a);
            }
        }
    }

    private static double Gain(Complex z, double b0, double b1, double b2, double a0, double a1, double a2) =>
        20 * Math.Log10(Math.Max(1e-9, Complex.Abs((b0 + b1 * z + b2 * z * z) / (a0 + a1 * z + a2 * z * z))));

    /// <summary>Ten rising points for the engine, and the largest error in dB.</summary>
    public static (double[] Hz, double[] Db, double ErrorDb) Fit(IReadOnlyList<ParametricBand> bands)
    {
        var want = new double[GridSize];
        for (int i = 0; i < GridSize; i++) want[i] = ResponseDb(bands, Grid[i]);

        // Add a point wherever the line is furthest out, then nudge them all.
        var at = new List<int> { 0, GridSize - 1 };
        for (int i = at.Count; i < Points; i++)
        {
            int worst = WorstIndex(at, want, out _);
            if (at.Contains(worst)) worst = FreeIndex(at);
            at.Add(worst);
            at.Sort();
        }
        WorstIndex(at, want, out double best);
        for (int pass = 0; pass < 4; pass++)
            for (int p = 1; p < at.Count - 1; p++)
                foreach (int step in new[] { -12, -4, -1, 1, 4, 12 })
                {
                    int was = at[p], now = was + step;
                    if (now <= at[p - 1] || now >= at[p + 1]) continue;
                    at[p] = now;
                    WorstIndex(at, want, out double err);
                    if (err < best) best = err;
                    else at[p] = was;
                }

        return ([.. at.Select(i => Math.Round(Grid[i], 1))], [.. at.Select(i => Math.Round(want[i], 1))], best);
    }

    // Where a straight-line path through the points is furthest from the curve.
    private static int WorstIndex(List<int> at, double[] want, out double error)
    {
        int worst = 0, seg = 0;
        error = -1;
        for (int i = 0; i < GridSize; i++)
        {
            while (at[seg + 1] < i) seg++;
            int a = at[seg], b = at[seg + 1];
            double t = (Grid[i] - Grid[a]) / (Grid[b] - Grid[a]);
            double e = Math.Abs(want[a] + (want[b] - want[a]) * t - want[i]);
            if (e > error) (error, worst) = (e, i);
        }
        return worst;
    }

    // A flat curve has no worst place, so spread the rest evenly.
    private static int FreeIndex(List<int> at)
    {
        int gapStart = 0, gap = 0;
        for (int i = 0; i < at.Count - 1; i++)
            if (at[i + 1] - at[i] > gap) (gap, gapStart) = (at[i + 1] - at[i], at[i]);
        return gapStart + gap / 2;
    }
}
