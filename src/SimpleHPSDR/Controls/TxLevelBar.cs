using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SimpleHPSDR.Controls;

/// <summary>
/// Transmit level bar for the ALC (or FM deviation) reading, drawn like the
/// S-meter: green up to <see cref="WarnAt"/>, orange to <see cref="OverAt"/>,
/// red beyond, the colours fixed to the scale rather than to the bar's length.
/// Value, Peak and the thresholds are fractions of the full scale (0 to 1).
/// </summary>
public sealed class TxLevelBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<TxLevelBar, double>(nameof(Value));
    public static readonly StyledProperty<double> PeakProperty =
        AvaloniaProperty.Register<TxLevelBar, double>(nameof(Peak));
    public static readonly StyledProperty<double> WarnAtProperty =
        AvaloniaProperty.Register<TxLevelBar, double>(nameof(WarnAt), 0.5);
    public static readonly StyledProperty<double> OverAtProperty =
        AvaloniaProperty.Register<TxLevelBar, double>(nameof(OverAt), 0.75);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Peak { get => GetValue(PeakProperty); set => SetValue(PeakProperty, value); }
    public double WarnAt { get => GetValue(WarnAtProperty); set => SetValue(WarnAtProperty, value); }
    public double OverAt { get => GetValue(OverAtProperty); set => SetValue(OverAtProperty, value); }

    // Half-width of each colour blend, as a fraction of the scale.
    private const double Blend = 0.03;

    private static readonly Color Green = Color.Parse("#3DD68C");
    private static readonly Color Orange = Color.Parse("#F5A524");
    private static readonly Color Red = Color.Parse("#E5484D");

    private IBrush? _gradient;

    static TxLevelBar()
    {
        AffectsRender<TxLevelBar>(ValueProperty, PeakProperty, WarnAtProperty, OverAtProperty);
    }

    public TxLevelBar() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WarnAtProperty || change.Property == OverAtProperty) _gradient = null;
    }

    private IBrush Themed(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;

    private IBrush Gradient()
    {
        double warn = Math.Clamp(WarnAt, Blend, 1 - Blend), over = Math.Clamp(OverAt, warn, 1 - Blend);
        return _gradient ??= new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Green, 0),
                new GradientStop(Green, warn - Blend),
                new GradientStop(Orange, warn + Blend),
                new GradientStop(Orange, over - Blend),
                new GradientStop(Red, over + Blend),
                new GradientStop(Red, 1),
            },
        };
    }

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        if (r.Width < 4) return;
        const double radius = 3;
        ctx.DrawRectangle(Themed("MeterTrack"), null, r, radius, radius);

        double w = r.Width * Math.Clamp(Value, 0, 1);
        if (w > 0)
            using (ctx.PushClip(new Rect(0, 0, w, r.Height)))
                ctx.DrawRectangle(Gradient(), null, r, radius, radius);

        if (Peak > 0)
        {
            double x = Math.Round(r.Width * Math.Clamp(Peak, 0, 1));
            ctx.FillRectangle(Themed("MeterPeak"), new Rect(Math.Clamp(x - 1, 0, r.Width - 2), 0, 2, r.Height));
        }
    }
}
