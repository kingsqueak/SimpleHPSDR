using System.Numerics;

namespace SimpleHPSDR.Dsp;

/// <summary>One SimpleStudio EQ band as the curve needs it. Type indexes
/// <c>StudioCatalogue.EqBandTypes</c>: bell, low shelf, high shelf, low cut, high cut.</summary>
public readonly record struct StudioEqBand(bool On, int Type, double Hz, double GainDb, double Q);

/// <summary>
/// The SimpleStudio parametric EQ's response, for drawing: the RBJ cookbook
/// biquads the engine runs (SimpleHPSDR.Dsp/Studio/Biquad.cs in the engine,
/// which the app can't reach), at 48 kHz. A band that is off adds nothing;
/// the cuts are 12 dB/octave and ignore the gain.
/// </summary>
public static class StudioEq
{
    public const double Rate = 48000, MinHz = 20, MaxHz = 20000;
    public const int Bell = 0, LowShelf = 1, HighShelf = 2, LowCut = 3, HighCut = 4;

    /// <summary>Frequencies the curve is worked out at, evenly spaced on a log axis.</summary>
    public static double[] Grid(int points)
    {
        var g = new double[points];
        for (int i = 0; i < points; i++) g[i] = MinHz * Math.Pow(MaxHz / MinHz, i / (points - 1.0));
        return g;
    }

    /// <summary>The whole EQ in dB at each frequency.</summary>
    public static double[] ResponseDb(IReadOnlyList<StudioEqBand> bands, IReadOnlyList<double> hz)
    {
        var db = new double[hz.Count];
        foreach (var b in bands)
        {
            if (!b.On) continue;
            var c = Design(b);
            for (int i = 0; i < db.Length; i++) db[i] += BandDb(c, hz[i]);
        }
        return db;
    }

    private readonly record struct Coefficients(double B0, double B1, double B2, double A1, double A2);

    // As the engine's Biquad.Design: limits, then the cookbook formulas.
    private static Coefficients Design(StudioEqBand band)
    {
        double freq = Math.Clamp(band.Hz, MinHz, Math.Min(MaxHz, 0.45 * Rate));
        double q = Math.Clamp(band.Q, 0.1, 10);
        double gainDb = Math.Clamp(band.GainDb, -24, 24);
        double w0 = 2 * Math.PI * freq / Rate, cosw = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * q);
        double a = Math.Pow(10, gainDb / 40);
        double b0 = 1, b1 = 0, b2 = 0, a0 = 1, a1 = 0, a2 = 0;
        switch (band.Type)
        {
            case Bell:
                (b0, b1, b2) = (1 + alpha * a, -2 * cosw, 1 - alpha * a);
                (a0, a1, a2) = (1 + alpha / a, -2 * cosw, 1 - alpha / a);
                break;
            case LowShelf:
            {
                double s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) - (a - 1) * cosw + s);
                b1 = 2 * a * ((a - 1) - (a + 1) * cosw);
                b2 = a * ((a + 1) - (a - 1) * cosw - s);
                a0 = (a + 1) + (a - 1) * cosw + s;
                a1 = -2 * ((a - 1) + (a + 1) * cosw);
                a2 = (a + 1) + (a - 1) * cosw - s;
                break;
            }
            case HighShelf:
            {
                double s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) + (a - 1) * cosw + s);
                b1 = -2 * a * ((a - 1) + (a + 1) * cosw);
                b2 = a * ((a + 1) + (a - 1) * cosw - s);
                a0 = (a + 1) - (a - 1) * cosw + s;
                a1 = 2 * ((a - 1) - (a + 1) * cosw);
                a2 = (a + 1) - (a - 1) * cosw - s;
                break;
            }
            case LowCut:
                (b0, b1, b2) = ((1 + cosw) / 2, -(1 + cosw), (1 + cosw) / 2);
                (a0, a1, a2) = (1 + alpha, -2 * cosw, 1 - alpha);
                break;
            case HighCut:
                (b0, b1, b2) = ((1 - cosw) / 2, 1 - cosw, (1 - cosw) / 2);
                (a0, a1, a2) = (1 + alpha, -2 * cosw, 1 - alpha);
                break;
        }
        return new(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    private static double BandDb(in Coefficients c, double hz)
    {
        var z1 = Complex.FromPolarCoordinates(1, -2 * Math.PI * hz / Rate);
        var z2 = z1 * z1;
        var h = (c.B0 + c.B1 * z1 + c.B2 * z2) / (1 + c.A1 * z1 + c.A2 * z2);
        return 20 * Math.Log10(Math.Max(h.Magnitude, 1e-9));
    }
}
