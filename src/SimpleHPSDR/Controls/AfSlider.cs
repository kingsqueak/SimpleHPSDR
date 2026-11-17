using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace SimpleHPSDR.Controls;

/// <summary>
/// The AF slider: a ball on a track. Drag it, click the track, or use the
/// wheel (1 dB a click). With slice B on, the main view swaps the single
/// main-AF slider for two compact ones, A over B, each in its slice colour.
/// </summary>
public sealed class AfSlider : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<AfSlider, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<IBrush?> BallBrushProperty =
        AvaloniaProperty.Register<AfSlider, IBrush?>(nameof(BallBrush));
    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<AfSlider, bool>(nameof(Compact));

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    /// <summary>Ball and fill colour; the theme accent when not set.</summary>
    public IBrush? BallBrush { get => GetValue(BallBrushProperty); set => SetValue(BallBrushProperty, value); }
    /// <summary>A smaller ball and a thinner track, for two sliders stacked in one row.</summary>
    public bool Compact { get => GetValue(CompactProperty); set => SetValue(CompactProperty, value); }

    public double Minimum { get; set; } = -50;
    public double Maximum { get; set; } = 20;
    /// <summary>What one wheel click adds.</summary>
    public double Step { get; set; } = 1;
    /// <summary>A pan control: the fill runs from the middle (0) to the ball,
    /// the middle is marked, and a drag catches at the middle.</summary>
    public bool Centered { get; set; }

    static AfSlider()
    {
        AffectsRender<AfSlider>(ValueProperty, BallBrushProperty, CompactProperty);
        AffectsMeasure<AfSlider>(CompactProperty);
    }

    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#5A606B"));

    private bool _drag;
    private double _wheel;

    private double Ball => Compact ? 6 : 9;          // ball radius
    private double Left => Ball;
    private double Right => Bounds.Width - Ball;
    private double TrackY => Bounds.Height / 2;
    private double X(double v) => Left + (Math.Clamp(v, Minimum, Maximum) - Minimum) / (Maximum - Minimum) * (Right - Left);
    private double V(double x)
    {
        double v = Math.Round(Minimum + Math.Clamp((x - Left) / (Right - Left), 0, 1) * (Maximum - Minimum));
        return Centered && Math.Abs(X(v) - X(0)) <= 4 ? 0 : v;
    }

    private IBrush AccentBrush =>
        this.TryFindResource("SystemAccentColor", ActualThemeVariant, out var c) && c is Color col
            ? new SolidColorBrush(col) : new SolidColorBrush(Color.Parse("#3B82F6"));

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 170 : available.Width, 2 * Ball + (Compact ? 2 : 4));

    public override void Render(DrawingContext ctx)
    {
        double y = TrackY, xv = X(Value);
        var fill = BallBrush ?? AccentBrush;
        float half = Compact ? 1.5f : 2f;
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));   // whole area takes the pointer
        ctx.FillRectangle(TrackBrush, new Rect(Left, y - half, Right - Left, 2 * half), half);
        if (Centered)
        {
            double mid = X(0);
            ctx.FillRectangle(fill, new Rect(Math.Min(mid, xv), y - half, Math.Abs(xv - mid), 2 * half));
            ctx.FillRectangle(TrackBrush, new Rect(mid - 0.75, y - Ball, 1.5, 2 * Ball));
        }
        else
            ctx.FillRectangle(fill, new Rect(Left, y - half, Math.Max(0, xv - Left), 2 * half), half);
        ctx.DrawEllipse(fill, null, new Point(xv, y), Ball, Ball);
    }

    private void Set(double v) => Value = Math.Clamp(v, Minimum, Maximum);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _drag = true;
        Set(V(e.GetPosition(this).X));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag) Set(V(e.GetPosition(this).X));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = false;
        e.Pointer.Capture(null);
    }

    // Capture can be lost without a release (focus change, window deactivated);
    // end the drag so hover moves don't keep setting the value.
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = false;
    }

    // One wheel click is 1 dB.
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double amount = SliderWheel.Amount(e);
        if (amount == 0) return;
        e.Handled = true;
        double acc = _wheel + amount / SliderWheel.ClickDelta;
        int clicks = (int)Math.Truncate(acc + Math.CopySign(1e-6, acc));
        _wheel = acc - clicks;
        if (clicks != 0) Set(Math.Round(Value) + clicks * Step);
    }
}
