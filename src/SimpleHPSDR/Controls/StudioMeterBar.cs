using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SimpleHPSDR.Controls;

public enum StudioMeterKind
{
    /// <summary>A level, Min to Max dB from the bottom: the peak light, the RMS solid.</summary>
    Level,
    /// <summary>Gain reduction, 0 at the top growing down to Scale dB.</summary>
    Reduction,
    /// <summary>Gain either way from the middle: boost up, cut down, ±Scale dB.</summary>
    Bipolar,
}

/// <summary>
/// A vertical meter for the SimpleStudio meter bridge, or a thin horizontal
/// one (<see cref="Horizontal"/>) for the chain tiles and band columns.
/// </summary>
public sealed class StudioMeterBar : Control
{
    public static readonly StyledProperty<StudioMeterKind> KindProperty =
        AvaloniaProperty.Register<StudioMeterBar, StudioMeterKind>(nameof(Kind));
    public static readonly StyledProperty<double> PeakProperty =
        AvaloniaProperty.Register<StudioMeterBar, double>(nameof(Peak), -120);
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<StudioMeterBar, double>(nameof(Value), -120);
    public static readonly StyledProperty<double> MinProperty =
        AvaloniaProperty.Register<StudioMeterBar, double>(nameof(Min), -60);
    public static readonly StyledProperty<double> MaxProperty =
        AvaloniaProperty.Register<StudioMeterBar, double>(nameof(Max));
    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<StudioMeterBar, double>(nameof(Scale), 24);
    public static readonly StyledProperty<double> ThresholdProperty =
        AvaloniaProperty.Register<StudioMeterBar, double>(nameof(Threshold), double.NaN);
    public static readonly StyledProperty<bool> HorizontalProperty =
        AvaloniaProperty.Register<StudioMeterBar, bool>(nameof(Horizontal));
    public static readonly StyledProperty<bool> ShowScaleProperty =
        AvaloniaProperty.Register<StudioMeterBar, bool>(nameof(ShowScale));

    public StudioMeterKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    /// <summary>Level kind: the peak (drawn light).</summary>
    public double Peak { get => GetValue(PeakProperty); set => SetValue(PeakProperty, value); }
    /// <summary>The RMS level, the reduction or the gain.</summary>
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Min { get => GetValue(MinProperty); set => SetValue(MinProperty, value); }
    public double Max { get => GetValue(MaxProperty); set => SetValue(MaxProperty, value); }
    public double Scale { get => GetValue(ScaleProperty); set => SetValue(ScaleProperty, value); }
    /// <summary>Level kind: a line across at this level; NaN for none.</summary>
    public double Threshold { get => GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
    public bool Horizontal { get => GetValue(HorizontalProperty); set => SetValue(HorizontalProperty, value); }
    /// <summary>Tick marks every 10 dB (or 6 dB for reduction) along the bar.</summary>
    public bool ShowScale { get => GetValue(ShowScaleProperty); set => SetValue(ShowScaleProperty, value); }

    static StudioMeterBar() => AffectsRender<StudioMeterBar>(KindProperty, PeakProperty, ValueProperty, MinProperty,
        MaxProperty, ScaleProperty, ThresholdProperty, HorizontalProperty, ShowScaleProperty);

    public StudioMeterBar() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    private static readonly IBrush LevelBrush = new SolidColorBrush(Color.Parse("#3DD68C"));
    private static readonly IBrush PeakBrush = new SolidColorBrush(Color.Parse("#3DD68C"), 0.35);
    private static readonly IBrush HotBrush = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush ReductionBrush = new SolidColorBrush(Color.Parse("#F5A524"));
    private static readonly IPen ThresholdPen = new Pen(new SolidColorBrush(Color.Parse("#7FB2FF")), 2);

    private IBrush Themed(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;

    // Fraction 0..1 along the bar, 0 at the bottom (or left).
    private Rect Span(Rect r, double from, double to)
    {
        from = Math.Clamp(from, 0, 1);
        to = Math.Clamp(to, 0, 1);
        if (to < from) (from, to) = (to, from);
        return Horizontal
            ? new Rect(r.X + r.Width * from, r.Y, r.Width * (to - from), r.Height)
            : new Rect(r.X, r.Bottom - r.Height * to, r.Width, r.Height * (to - from));
    }

    private double Frac(double db) => Max > Min ? (db - Min) / (Max - Min) : 0;

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        if (r.Width < 2 || r.Height < 2) return;
        double radius = Math.Min(3, Math.Min(r.Width, r.Height) / 2);
        ctx.DrawRectangle(Themed("MeterTrack"), null, r, radius, radius);
        switch (Kind)
        {
            case StudioMeterKind.Level:
            {
                double peak = Frac(Peak), rms = Frac(Value);
                if (peak > 0) ctx.DrawRectangle(PeakBrush, null, Span(r, 0, peak));
                if (rms > 0) ctx.DrawRectangle(LevelBrush, null, Span(r, 0, rms));
                // The top 3 dB in red, where the peak reached it.
                double hot = Frac(Max - 3);
                if (peak > hot) ctx.DrawRectangle(HotBrush, null, Span(r, hot, peak));
                if (ShowScale) Ticks(ctx, r, 10, (Max - Min) / 10, d => Frac(Max - d));
                if (double.IsFinite(Threshold) && Threshold >= Min && Threshold <= Max)
                {
                    double t = Frac(Threshold);
                    var a = Horizontal ? new Point(r.X + r.Width * t, r.Y) : new Point(r.X - 2, r.Bottom - r.Height * t);
                    var b = Horizontal ? new Point(r.X + r.Width * t, r.Bottom) : new Point(r.Right + 2, r.Bottom - r.Height * t);
                    ctx.DrawLine(ThresholdPen, a, b);
                }
                break;
            }
            case StudioMeterKind.Reduction:
            {
                double f = Scale > 0 ? Math.Clamp(Value / Scale, 0, 1) : 0;
                // Grows down from the top (horizontally: from the left).
                if (f > 0) ctx.DrawRectangle(ReductionBrush, null, Horizontal ? Span(r, 0, f) : Span(r, 1 - f, 1));
                if (ShowScale) Ticks(ctx, r, 6, Scale / 6, d => 1 - d / Scale);
                break;
            }
            case StudioMeterKind.Bipolar:
            {
                double f = Scale > 0 ? Math.Clamp(Value / Scale, -1, 1) : 0;
                if (f > 0) ctx.DrawRectangle(LevelBrush, null, Span(r, 0.5, 0.5 + f / 2));
                else if (f < 0) ctx.DrawRectangle(ReductionBrush, null, Span(r, 0.5 + f / 2, 0.5));
                var mid = Span(r, 0.5, 0.5);
                ctx.DrawLine(new Pen(Themed("TextMuted"), 1),
                    Horizontal ? mid.TopLeft : new Point(r.X, mid.Y), Horizontal ? mid.BottomLeft : new Point(r.Right, mid.Y));
                break;
            }
        }
    }

    // Small marks every step dB; at(d) is the fraction for d dB from the top.
    private void Ticks(DrawingContext ctx, Rect r, double stepDb, double count, Func<double, double> at)
    {
        var pen = new Pen(new SolidColorBrush(Themed("TextMuted") is ISolidColorBrush s ? s.Color : Colors.Gray, 0.5), 1);
        for (int i = 1; i < count; i++)
        {
            double f = at(i * stepDb);
            if (Horizontal) ctx.DrawLine(pen, new Point(r.X + r.Width * f, r.Bottom - 3), new Point(r.X + r.Width * f, r.Bottom));
            else ctx.DrawLine(pen, new Point(r.X, r.Bottom - r.Height * f), new Point(r.X + 4, r.Bottom - r.Height * f));
        }
    }
}
