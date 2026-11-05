using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SimpleHPSDR.Dsp;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Controls;

/// <summary>
/// The parametric EQ curve. Drag a numbered point for frequency and gain; the
/// wheel over one sets its Q or slope. The blue line is the curve asked for,
/// the amber one what the engine's ten points actually give.
/// </summary>
public sealed class ParametricEqView : Control
{
    public static readonly StyledProperty<ObservableCollection<ParametricBandRow>?> BandsProperty =
        AvaloniaProperty.Register<ParametricEqView, ObservableCollection<ParametricBandRow>?>(nameof(Bands));
    public static readonly StyledProperty<double[]?> FittedHzProperty =
        AvaloniaProperty.Register<ParametricEqView, double[]?>(nameof(FittedHz));
    public static readonly StyledProperty<double[]?> FittedDbProperty =
        AvaloniaProperty.Register<ParametricEqView, double[]?>(nameof(FittedDb));

    public ObservableCollection<ParametricBandRow>? Bands
    {
        get => GetValue(BandsProperty);
        set => SetValue(BandsProperty, value);
    }
    public double[]? FittedHz { get => GetValue(FittedHzProperty); set => SetValue(FittedHzProperty, value); }
    public double[]? FittedDb { get => GetValue(FittedDbProperty); set => SetValue(FittedDbProperty, value); }

    static ParametricEqView() =>
        AffectsRender<ParametricEqView>(BandsProperty, FittedHzProperty, FittedDbProperty);

    private const double Pad = 18, Grab = 12, NodeRadius = 8, RangeDb = 24;
    private static readonly double[] GridHz = [50, 100, 200, 500, 1000, 2000, 5000];
    private static readonly double[] GridDb = [-18, -12, -6, 0, 6, 12, 18];

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#0B0D11"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#2A303A")), 1);
    private static readonly IPen ZeroPen = new Pen(new SolidColorBrush(Color.Parse("#4A5260")), 1);
    private static readonly IPen CurvePen = new Pen(new SolidColorBrush(Color.Parse("#7FB2FF")), 2);
    private static readonly IPen FittedPen = new Pen(new SolidColorBrush(Color.Parse("#F5A524")), 1, new DashStyle([3, 3], 0));
    private static readonly IBrush NodeBrush = new SolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush NodeOffBrush = new SolidColorBrush(Color.Parse("#3A404C"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#E6E8EB"));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#7D8696"));
    private static readonly Typeface Face = new("Inter");

    private ParametricBandRow? _drag;

    public ParametricEqView() => ClipToBounds = true;

    private double X(double hz) =>
        Pad + (Bounds.Width - 2 * Pad) * Math.Log(hz / ParametricEq.MinHz) / Math.Log(ParametricEq.MaxHz / ParametricEq.MinHz);
    private double HzAt(double x) => ParametricEq.MinHz *
        Math.Pow(ParametricEq.MaxHz / ParametricEq.MinHz, Math.Clamp((x - Pad) / (Bounds.Width - 2 * Pad), 0, 1));
    private double Y(double db) => Bounds.Height / 2 - db / RangeDb * (Bounds.Height / 2 - Pad / 2);
    private double DbAt(double y) => (Bounds.Height / 2 - y) / (Bounds.Height / 2 - Pad / 2) * RangeDb;

    private void Label(DrawingContext ctx, string text, double x, double y, IBrush brush, double size = 10)
    {
        var t = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Face, size, brush);
        ctx.DrawText(t, new Point(x - t.Width / 2, y - t.Height / 2));
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(Background, null, new Rect(Bounds.Size), 6, 6);
        foreach (var hz in GridHz)
        {
            ctx.DrawLine(GridPen, new Point(X(hz), 0), new Point(X(hz), Bounds.Height));
            Label(ctx, hz >= 1000 ? $"{hz / 1000:0}k" : $"{hz:0}", X(hz), Bounds.Height - 8, LabelBrush);
        }
        foreach (var db in GridDb)
        {
            ctx.DrawLine(db == 0 ? ZeroPen : GridPen, new Point(0, Y(db)), new Point(Bounds.Width, Y(db)));
            Label(ctx, $"{db:+0;-0;0}", 12, Y(db) - 6, LabelBrush);
        }
        if (Bands is not { Count: > 0 } rows) return;

        if (FittedHz is { Length: > 1 } fh && FittedDb is { Length: > 1 } fd && fh.Length == fd.Length)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(X(fh[0]), Y(fd[0])), false);
                // The engine's lines are straight in linear frequency: bend them for this axis.
                for (int i = 1; i < fh.Length; i++)
                    for (int s = 1; s <= 12; s++)
                    {
                        double t = s / 12.0, hz = fh[i - 1] + (fh[i] - fh[i - 1]) * t;
                        c.LineTo(new Point(X(hz), Y(fd[i - 1] + (fd[i] - fd[i - 1]) * t)));
                    }
                c.EndFigure(false);
            }
            ctx.DrawGeometry(null, FittedPen, g);
        }

        var bands = rows.Select(r => r.ToBand()).ToArray();
        var line = new StreamGeometry();
        using (var c = line.Open())
        {
            int width = Math.Max(2, (int)(Bounds.Width - 2 * Pad));
            for (int i = 0; i <= width; i++)
            {
                double x = Pad + i;
                var p = new Point(x, Y(ParametricEq.ResponseDb(bands, HzAt(x))));
                if (i == 0) c.BeginFigure(p, false); else c.LineTo(p);
            }
            c.EndFigure(false);
        }
        ctx.DrawGeometry(null, CurvePen, line);

        foreach (var r in rows)
        {
            var at = NodeAt(r);
            ctx.DrawEllipse(r.On ? NodeBrush : NodeOffBrush, null, at, NodeRadius, NodeRadius);
            Label(ctx, r.Number.ToString(), at.X, at.Y, TextBrush);
        }
    }

    private Point NodeAt(ParametricBandRow r) => new(X(Math.Clamp(r.Hz, ParametricEq.MinHz, ParametricEq.MaxHz)),
        Y(r.HasGain ? Math.Clamp(r.GainDb, -RangeDb, RangeDb) : 0));

    private ParametricBandRow? RowAt(Point p) => Bands?
        .Select(r => (Row: r, D: Point.Distance(NodeAt(r), p)))
        .Where(n => n.D <= Grab).OrderBy(n => n.D).Select(n => n.Row).FirstOrDefault();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _drag = RowAt(e.GetPosition(this));
        if (_drag is null) return;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_drag is null)
        {
            Cursor = new Cursor(RowAt(p) is null ? StandardCursorType.Arrow : StandardCursorType.Hand);
            return;
        }
        _drag.Hz = Math.Round(HzAt(p.X));
        if (_drag.HasGain) _drag.GainDb = Math.Round(Math.Clamp(DbAt(p.Y), -RangeDb, RangeDb), 1);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is null) return;
        _drag = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (RowAt(e.GetPosition(this)) is not { } r) return;
        double dir = Math.Sign(SliderWheel.Amount(e));
        r.Shape = Math.Round(Math.Clamp(r.Shape + dir * r.ShapeStep, r.ShapeMin, r.ShapeMax), 1);
        e.Handled = true;
    }
}
