using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SimpleHPSDR.Controls;

/// <summary>
/// S-meter bar. The colour scale is fixed to the meter, not to the bar's
/// length: blue below S4, green S4-S9, orange S9 to S9+15, red from S9+20,
/// blending into each other at the boundaries. A thin marker shows the held
/// peak, and a yellow one where squelch sits while it's on.
/// </summary>
public sealed class SMeterBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<SMeterBar, double>(nameof(Value), -140);
    public static readonly StyledProperty<double> PeakProperty =
        AvaloniaProperty.Register<SMeterBar, double>(nameof(Peak), -140);

    public static readonly StyledProperty<double> SquelchProperty =
        AvaloniaProperty.Register<SMeterBar, double>(nameof(Squelch), double.NaN);
    /// <summary>Where to draw the squelch mark (dBm), NaN for none.</summary>
    public double Squelch { get => GetValue(SquelchProperty); set => SetValue(SquelchProperty, value); }

    // Scale: S1 (-121 dBm) area up to S9+40 (-33 dBm).
    public const double MinDbm = -127, MaxDbm = -33;
    private const double S4 = -103, S9 = -73, S9Plus15 = -58, S9Plus20 = -53;
    // Half-width of each colour blend, in dB.
    private const double Blend = 3;

    private static readonly Color Blue = Color.Parse("#3B82F6");
    private static readonly Color Green = Color.Parse("#3DD68C");
    private static readonly Color Orange = Color.Parse("#F5A524");
    private static readonly Color Red = Color.Parse("#E5484D");
    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#1F232C"));
    private static readonly IBrush PeakBrush = new SolidColorBrush(Color.Parse("#E6E8EB"));
    private static readonly IBrush SquelchBrush = new SolidColorBrush(Color.Parse("#F5C518"));

    static SMeterBar() => AffectsRender<SMeterBar>(ValueProperty, PeakProperty, SquelchProperty);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Peak { get => GetValue(PeakProperty); set => SetValue(PeakProperty, value); }

    private static double Pos(double dbm) => Math.Clamp((dbm - MinDbm) / (MaxDbm - MinDbm), 0, 1);

    private static readonly IBrush Gradient = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Blue, 0),
            new GradientStop(Blue, Pos(S4 - Blend)),
            new GradientStop(Green, Pos(S4 + Blend)),
            new GradientStop(Green, Pos(S9 - Blend)),
            new GradientStop(Orange, Pos(S9 + Blend)),
            new GradientStop(Orange, Pos(S9Plus15)),
            new GradientStop(Red, Pos(S9Plus20)),
            new GradientStop(Red, 1),
        },
    };

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        const double radius = 3;
        ctx.DrawRectangle(Track, null, r, radius, radius);

        // Paint the full-width gradient, clipped to the signal, so each colour
        // stays at its S-unit whatever the bar's length.
        double w = r.Width * Pos(Value);
        if (w > 0)
            using (ctx.PushClip(new Rect(0, 0, w, r.Height)))
                ctx.DrawRectangle(Gradient, null, r, radius, radius);

        if (!double.IsNaN(Squelch))
        {
            double x = Math.Round(r.Width * Pos(Squelch));
            ctx.FillRectangle(SquelchBrush, new Rect(Math.Clamp(x - 1.5, 0, r.Width - 3), 0, 3, r.Height));
        }

        if (Peak > MinDbm)
        {
            double x = Math.Round(r.Width * Pos(Peak));
            ctx.FillRectangle(PeakBrush, new Rect(Math.Clamp(x - 1, 0, r.Width - 2), 0, 2, r.Height));
        }
    }
}
