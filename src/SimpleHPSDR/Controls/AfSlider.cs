using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace SimpleHPSDR.Controls;

/// <summary>
/// The AF slider: the ball is the main (overall) AF. With slice B on, two
/// flags ride the same track for each slice's own level: A (red) above the
/// track, B (amber) below. Drag the ball or a flag, or use the wheel over
/// either; clicking the bare track moves the ball there.
/// </summary>
public sealed class AfSlider : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<AfSlider, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> TrimAProperty =
        AvaloniaProperty.Register<AfSlider, double>(nameof(TrimA), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> TrimBProperty =
        AvaloniaProperty.Register<AfSlider, double>(nameof(TrimB), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> ShowTrimsProperty =
        AvaloniaProperty.Register<AfSlider, bool>(nameof(ShowTrims));

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double TrimA { get => GetValue(TrimAProperty); set => SetValue(TrimAProperty, value); }
    public double TrimB { get => GetValue(TrimBProperty); set => SetValue(TrimBProperty, value); }
    public bool ShowTrims { get => GetValue(ShowTrimsProperty); set => SetValue(ShowTrimsProperty, value); }

    public double Minimum { get; set; } = -50;
    public double Maximum { get; set; } = 20;

    static AfSlider()
    {
        AffectsRender<AfSlider>(ValueProperty, TrimAProperty, TrimBProperty, ShowTrimsProperty);
        AffectsMeasure<AfSlider>(ShowTrimsProperty);
    }

    private const double Ball = 9;          // ball radius
    private const double FlagW = 15, FlagH = 12, Pole = 5;
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#5A606B"));
    private static readonly IBrush FlagA = new SolidColorBrush(Color.Parse("#F25F5C"));
    private static readonly IBrush FlagB = new SolidColorBrush(Color.Parse("#F5A524"));
    private static readonly IBrush FlagText = new SolidColorBrush(Color.Parse("#0B0D11"));
    private static readonly Typeface FlagFace = new("Inter", FontStyle.Normal, FontWeight.Bold);

    private enum Part { None, Main, A, B }
    private Part _drag;
    private readonly Dictionary<Part, double> _wheel = [];

    private double Left => Ball;
    private double Right => Bounds.Width - Ball;
    private double TrackY => Bounds.Height / 2;
    private double X(double v) => Left + (Math.Clamp(v, Minimum, Maximum) - Minimum) / (Maximum - Minimum) * (Right - Left);
    private double V(double x) => Math.Round(Minimum + Math.Clamp((x - Left) / (Right - Left), 0, 1) * (Maximum - Minimum));

    private IBrush AccentBrush =>
        this.TryFindResource("SystemAccentColor", ActualThemeVariant, out var c) && c is Color col
            ? new SolidColorBrush(col) : new SolidColorBrush(Color.Parse("#3B82F6"));

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 170 : available.Width,
            ShowTrims ? 2 * (FlagH + Pole) + 6 : 2 * Ball + 4);

    // A flag: a pole from the track out to a tag with the letter.
    private Rect FlagRect(double v, bool above)
    {
        double x = X(v);
        double y = above ? TrackY - Pole - FlagH : TrackY + Pole;
        return new Rect(x - 1, y, FlagW, FlagH);
    }

    public override void Render(DrawingContext ctx)
    {
        double y = TrackY, xv = X(Value);
        var accent = AccentBrush;
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));   // whole area takes the pointer
        ctx.FillRectangle(TrackBrush, new Rect(Left, y - 2, Right - Left, 4), 2);
        ctx.FillRectangle(accent, new Rect(Left, y - 2, Math.Max(0, xv - Left), 4), 2);

        if (ShowTrims)
        {
            DrawFlag(ctx, TrimB, false, FlagB, "B");
            DrawFlag(ctx, TrimA, true, FlagA, "A");
        }
        ctx.DrawEllipse(accent, null, new Point(xv, y), Ball, Ball);
    }

    private void DrawFlag(DrawingContext ctx, double v, bool above, IBrush brush, string tag)
    {
        double x = X(v);
        var r = FlagRect(v, above);
        double poleEnd = above ? r.Bottom : r.Top;
        ctx.DrawLine(new Pen(brush, 2), new Point(x, TrackY), new Point(x, poleEnd));
        ctx.FillRectangle(brush, r, 2);
        var ft = new FormattedText(tag, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, FlagFace, 9, FlagText);
        ctx.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
    }

    private Part HitTest(Point p)
    {
        if (ShowTrims)
        {
            if (FlagRect(TrimA, true).Inflate(3).Contains(p) || (p.Y < TrackY - 3 && Math.Abs(p.X - X(TrimA)) < 5)) return Part.A;
            if (FlagRect(TrimB, false).Inflate(3).Contains(p) || (p.Y > TrackY + 3 && Math.Abs(p.X - X(TrimB)) < 5)) return Part.B;
        }
        return Part.Main;
    }

    private void Set(Part part, double v)
    {
        v = Math.Clamp(v, Minimum, Maximum);
        switch (part)
        {
            case Part.A: TrimA = v; break;
            case Part.B: TrimB = v; break;
            case Part.Main: Value = v; break;
        }
    }

    private double Get(Part part) => part switch { Part.A => TrimA, Part.B => TrimB, _ => Value };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        _drag = HitTest(p);
        Set(_drag, V(p.X));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_drag != Part.None) Set(_drag, V(p.X));
        else Cursor = new Cursor(HitTest(p) == Part.Main ? StandardCursorType.Arrow : StandardCursorType.Hand);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = Part.None;
        e.Pointer.Capture(null);
    }

    // One wheel click is 1 dB on whatever is under the pointer.
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0) return;
        e.Handled = true;
        var part = HitTest(e.GetPosition(this));
        double acc = _wheel.GetValueOrDefault(part) + e.Delta.Y / SliderWheel.ClickDelta;
        int clicks = (int)Math.Truncate(acc + Math.CopySign(1e-6, acc));
        _wheel[part] = acc - clicks;
        if (clicks != 0) Set(part, Math.Round(Get(part)) + clicks);
    }
}
