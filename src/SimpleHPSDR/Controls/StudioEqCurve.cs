using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SimpleHPSDR.Dsp;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Controls;

/// <summary>
/// The SimpleStudio EQ's response: 20 Hz to 20 kHz on a log axis, ±18 dB,
/// with each band's numbered point. The points are handles: drag one for the
/// band's frequency and gain (a cut has no gain, so it only moves sideways),
/// turn the wheel over it for Q, double-click it to put its gain back to 0.
/// The band grid below shows and edits the same values. Colours and gestures
/// as <see cref="ParametricEqView"/>.
/// </summary>
public sealed class StudioEqCurve : Control
{
    public static readonly StyledProperty<double[]?> CurveDbProperty =
        AvaloniaProperty.Register<StudioEqCurve, double[]?>(nameof(CurveDb));
    public static readonly StyledProperty<IReadOnlyList<StudioEqMarker>?> MarkersProperty =
        AvaloniaProperty.Register<StudioEqCurve, IReadOnlyList<StudioEqMarker>?>(nameof(Markers));

    public static readonly StyledProperty<IReadOnlyList<StudioEqBandColumn>?> BandsProperty =
        AvaloniaProperty.Register<StudioEqCurve, IReadOnlyList<StudioEqBandColumn>?>(nameof(Bands));

    /// <summary>The bands the handles move, in marker order.</summary>
    public IReadOnlyList<StudioEqBandColumn>? Bands { get => GetValue(BandsProperty); set => SetValue(BandsProperty, value); }

    /// <summary>dB at each of <see cref="StudioEffectViewModel.CurveHz"/>.</summary>
    public double[]? CurveDb { get => GetValue(CurveDbProperty); set => SetValue(CurveDbProperty, value); }
    public IReadOnlyList<StudioEqMarker>? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }

    static StudioEqCurve() => AffectsRender<StudioEqCurve>(CurveDbProperty, MarkersProperty);

    private const double PadX = 26, PadTop = 8, PadBottom = 18, RangeDb = 18, NodeRadius = 8, Grab = 14;
    private static readonly double[] GridHz = [50, 100, 200, 500, 1000, 2000, 5000, 10000];
    private static readonly double[] GridDb = [-12, -6, 0, 6, 12];

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#0B0D11"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#2A303A")), 1);
    private static readonly IPen ZeroPen = new Pen(new SolidColorBrush(Color.Parse("#4A5260")), 1);
    private static readonly IPen CurvePen = new Pen(new SolidColorBrush(Color.Parse("#7FB2FF")), 2);
    private static readonly IBrush FillBrush = new SolidColorBrush(Color.Parse("#7FB2FF"), 0.12);
    private static readonly IBrush NodeBrush = new SolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush NodeHotBrush = new SolidColorBrush(Color.Parse("#60A5FA"));
    private static readonly IPen NodeHotPen = new Pen(new SolidColorBrush(Color.Parse("#E6E8EB")), 1.5);
    private static readonly IBrush NodeOffBrush = new SolidColorBrush(Color.Parse("#3A404C"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#E6E8EB"));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#7D8696"));
    private static readonly Typeface Face = new("Inter");

    private int _drag = -1;  // marker index being dragged
    private int _hot = -1;   // marker index under the pointer

    public StudioEqCurve() => ClipToBounds = true;

    private double HzAt(double x) => StudioEq.MinHz * Math.Pow(StudioEq.MaxHz / StudioEq.MinHz,
        Math.Clamp((x - PadX) / Math.Max(1, Bounds.Width - PadX - 8), 0, 1));
    private double DbAt(double y)
    {
        double h = Bounds.Height - PadTop - PadBottom;
        return Math.Clamp((PadTop + h / 2 - y) / (h / 2) * RangeDb, -RangeDb, RangeDb);
    }

    private Point NodeAt(StudioEqMarker m) => new(X(m.Hz), Y(Math.Clamp(m.Db, -RangeDb, RangeDb)));

    /// <summary>The nearest handle within reach of p, or -1.</summary>
    private int MarkerAt(Point p)
    {
        int best = -1;
        double bestDistance = Grab;
        var markers = Markers;
        for (int i = 0; markers is not null && i < markers.Count; i++)
        {
            double d = Point.Distance(NodeAt(markers[i]), p);
            if (d <= bestDistance) (best, bestDistance) = (i, d);
        }
        return best;
    }

    private StudioEqBandColumn? Band(int index) =>
        Bands is { } bands && index >= 0 && index < bands.Count ? bands[index] : null;

    private void SetHot(int index)
    {
        if (index == _hot) return;
        _hot = index;
        Cursor = new Cursor(index < 0 ? StandardCursorType.Arrow : StandardCursorType.Hand);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        int at = MarkerAt(e.GetPosition(this));
        if (Band(at) is not { } band) return;
        if (e.ClickCount == 2)
        {
            if (band.HasGain) band.Gain.Value = 0;
            e.Handled = true;
            return;
        }
        _drag = at;
        SetHot(at);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (Band(_drag) is not { } band)
        {
            SetHot(MarkerAt(p));
            return;
        }
        band.Freq.Value = HzAt(p.X);
        if (band.HasGain) band.Gain.Value = Math.Round(DbAt(p.Y) * 2) / 2; // half-dB steps
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag < 0) return;
        _drag = -1;
        e.Pointer.Capture(null);
        SetHot(MarkerAt(e.GetPosition(this)));
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = -1;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_drag < 0) SetHot(-1);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Band(MarkerAt(e.GetPosition(this))) is not { } band) return;
        double clicks = SliderWheel.Clicks(this, e);
        // Q is on a log scale: each click is about a tenth of the way to double.
        band.Q.Value = band.Q.Value * Math.Pow(2, clicks * 0.1);
        e.Handled = true;
    }

    private double X(double hz) => PadX + (Bounds.Width - PadX - 8) *
        Math.Log(Math.Clamp(hz, StudioEq.MinHz, StudioEq.MaxHz) / StudioEq.MinHz) / Math.Log(StudioEq.MaxHz / StudioEq.MinHz);
    private double Y(double db)
    {
        double h = Bounds.Height - PadTop - PadBottom;
        return PadTop + h / 2 - Math.Clamp(db, -RangeDb - 1, RangeDb + 1) / RangeDb * (h / 2);
    }

    private static void Text(DrawingContext ctx, string text, double x, double y, IBrush brush, double size = 10, bool centre = true)
    {
        var t = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Face, size, brush);
        ctx.DrawText(t, new Point(centre ? x - t.Width / 2 : x - t.Width, y - t.Height / 2));
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(Background, null, new Rect(Bounds.Size), 6, 6);
        double bottom = Bounds.Height - PadBottom;
        foreach (var hz in GridHz)
        {
            ctx.DrawLine(GridPen, new Point(X(hz), PadTop), new Point(X(hz), bottom));
            Text(ctx, hz >= 1000 ? $"{hz / 1000:0}k" : $"{hz:0}", X(hz), bottom + 9, LabelBrush);
        }
        foreach (var db in GridDb)
        {
            ctx.DrawLine(db == 0 ? ZeroPen : GridPen, new Point(PadX, Y(db)), new Point(Bounds.Width - 8, Y(db)));
            Text(ctx, $"{db:+0;-0;0}", PadX - 4, Y(db), LabelBrush, 10, centre: false);
        }

        var hzs = StudioEffectViewModel.CurveHz;
        if (CurveDb is { } db0 && db0.Length == hzs.Length && db0.Length > 1)
        {
            var line = new StreamGeometry();
            var fill = new StreamGeometry();
            using (var c = line.Open())
            using (var f = fill.Open())
            {
                f.BeginFigure(new Point(X(hzs[0]), Y(0)), true);
                for (int i = 0; i < hzs.Length; i++)
                {
                    var p = new Point(X(hzs[i]), Y(db0[i]));
                    if (i == 0) c.BeginFigure(p, false); else c.LineTo(p);
                    f.LineTo(p);
                }
                f.LineTo(new Point(X(hzs[^1]), Y(0)));
                c.EndFigure(false);
                f.EndFigure(true);
            }
            ctx.DrawGeometry(FillBrush, null, fill);
            ctx.DrawGeometry(null, CurvePen, line);
        }

        var markers = Markers;
        for (int i = 0; markers is not null && i < markers.Count; i++)
        {
            var m = markers[i];
            var at = NodeAt(m);
            bool hot = i == _hot || i == _drag;
            double r = hot ? NodeRadius + 2 : NodeRadius;
            ctx.DrawEllipse(!m.On ? NodeOffBrush : hot ? NodeHotBrush : NodeBrush, hot ? NodeHotPen : null, at, r, r);
            Text(ctx, m.Number.ToString(), at.X, at.Y, TextBrush);
        }
    }
}
