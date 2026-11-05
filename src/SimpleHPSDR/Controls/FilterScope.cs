using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Zeus.Contracts;

namespace SimpleHPSDR.Controls;

/// <summary>
/// A close-up of the received signal as audio: 0 to 6000 Hz from the dial,
/// left to right, whatever the sideband (on lower sideband the RF below the
/// dial is shown mirrored). Cut from the active slice's panadapter frames,
/// with the receive filter shaded over it. Drag either edge of the shading
/// to set the filter's low or high cut (10 Hz steps). In AM and the other
/// double-sided modes only the upper side is shown, and its one edge sets both
/// sides together. The scale marks every 100 Hz, labelled every 500 Hz. The dB
/// range follows what's inside the 6 kHz, so the signal fills the box.
/// </summary>
public sealed class FilterScope : Control
{
    public const double SpanHz = 6000;
    private const double StepHz = 10, MinWidthHz = 50;
    private const double ScaleH = 13;         // tick and label strip at the bottom
    private const double Grab = 6;            // pointer distance that takes an edge
    private const float TraceAlpha = 0.5f;

    /// <summary>An edge was dragged: the new (low, high) offsets from the dial.</summary>
    public event Action<int, int>? FilterDragged;

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#0B0D11"));
    private static readonly IBrush TraceFill = new SolidColorBrush(Color.Parse("#333B82F6"));
    private static readonly IPen TracePen = new Pen(new SolidColorBrush(Color.Parse("#7FB2FF")), 1.2);
    private static readonly IBrush PassBrush = new SolidColorBrush(Color.Parse("#2E3B82F6"));
    private static readonly IPen EdgePen = new Pen(new SolidColorBrush(Color.Parse("#3B82F6")), 1.5);
    private static readonly IBrush HandleBrush = new SolidColorBrush(Color.Parse("#E6E8EB"));
    private static readonly IPen TickPen = new Pen(new SolidColorBrush(Color.Parse("#3A404C")), 1);
    private static readonly IPen MajorTickPen = new Pen(new SolidColorBrush(Color.Parse("#7D8696")), 1);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#7D8696"));
    private static readonly Color[] DialColors = [Color.Parse("#F25F5C"), Color.Parse("#F5A524")];
    private static readonly Typeface LabelFace = new("Inter");

    private float[]? _pan;
    private double[] _cols = [];
    private double _floorDb = -130, _topDb = -80;   // smoothed auto range
    private long _centerHz, _dialHz;
    private double _hzPerBin;
    private int _rx;
    private int _low, _high;
    private int _side = 1;   // +1 upper sideband (RF above the dial), -1 lower
    private bool _symmetric;
    private enum Edge { None, Low, High }
    private Edge _drag;

    public FilterScope()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    /// <summary>A display frame from the active slice's receiver, with that
    /// slice's dial and filter.</summary>
    public void PushFrame(DisplayFrame f, long dialHz, int low, int high, bool symmetric)
    {
        _rx = f.RxId;
        _dialHz = dialHz;
        if (_drag == Edge.None)
        {
            (_low, _high) = (low, high);
            _symmetric = symmetric;
            // Otherwise the filter says which side of the dial is heard.
            _side = symmetric || low + high >= 0 ? 1 : -1;
        }
        if (f.Width == 0 || !f.BodyFlags.HasFlag(DisplayBodyFlags.PanValid)) return;
        var raw = f.PanDb.Span;
        if (_pan is null || _pan.Length != raw.Length || f.CenterHz != _centerHz)
            _pan = raw.ToArray();
        else
            for (int i = 0; i < _pan.Length; i++)
                _pan[i] += (raw[i] - _pan[i]) * TraceAlpha;
        _centerHz = f.CenterHz;
        _hzPerBin = f.HzPerPixel;
        InvalidateVisual();
    }

    public void Clear()
    {
        _pan = null;
        InvalidateVisual();
    }

    // x runs 0..6000 Hz of audio; RF offset from the dial = side x audio.
    private double X(double offsetHz) => _side * offsetHz / SpanHz * Bounds.Width;
    private double Offset(double x) => _side * x / Bounds.Width * SpanHz;

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        ctx.DrawRectangle(Background, null, b, 6, 6);
        var plot = new Rect(0, 0, b.Width, Math.Max(0, b.Height - ScaleH));

        DrawTrace(ctx, plot);

        // Passband and its draggable edges
        double xl = X(_low), xh = HighEdgeX();
        ctx.FillRectangle(PassBrush, new Rect(xl, 0, Math.Max(0, xh - xl), plot.Height));
        foreach (var x in _symmetric ? [xh] : new[] { xl, xh })
        {
            ctx.DrawLine(EdgePen, new Point(x, 0), new Point(x, plot.Height));
            ctx.FillRectangle(HandleBrush, new Rect(x - 2.5, plot.Height / 2 - 8, 5, 16), 2.5f);
        }

        // Dial
        var dial = new Pen(new SolidColorBrush(DialColors[Math.Clamp(_rx, 0, 1)]), 1);
        ctx.DrawLine(dial, new Point(1, 0), new Point(1, plot.Height));

        DrawScale(ctx, b, plot.Bottom);
    }

    private void DrawTrace(DrawingContext ctx, Rect r)
    {
        if (_pan is not { Length: > 1 } pan || _hzPerBin <= 0 || r.Height <= 0) return;
        int cols = Math.Max(2, (int)r.Width);
        if (_cols.Length != cols) _cols = new double[cols];
        for (int i = 0; i < cols; i++)
        {
            double hz = _dialHz + Offset(i * r.Width / (cols - 1));
            double bin = (hz - _centerHz) / _hzPerBin + pan.Length / 2.0;
            int i0 = Math.Clamp((int)Math.Floor(bin), 0, pan.Length - 1);
            int i1 = Math.Min(i0 + 1, pan.Length - 1);
            double t = Math.Clamp(bin - i0, 0, 1);
            _cols[i] = pan[i0] + (pan[i1] - pan[i0]) * t;
        }

        // Auto range: noise floor (20th percentile) near the bottom, the
        // strongest signal near the top, at least 30 dB; eased so it doesn't jump.
        var sorted = (double[])_cols.Clone();
        Array.Sort(sorted);
        double floor = sorted[sorted.Length / 5] - 3;
        double top = Math.Max(sorted[^1] + 3, floor + 30);
        _floorDb += (floor - _floorDb) * 0.1;
        _topDb += (top - _topDb) * 0.1;
        double range = Math.Max(1, _topDb - _floorDb);

        var g = new StreamGeometry();
        var line = new StreamGeometry();
        using (var c = g.Open())
        using (var l = line.Open())
        {
            c.BeginFigure(new Point(0, r.Bottom), true);
            for (int i = 0; i < cols; i++)
            {
                double x = i * r.Width / (cols - 1);
                double y = r.Bottom - Math.Clamp((_cols[i] - _floorDb) / range, 0, 1) * r.Height;
                c.LineTo(new Point(x, y));
                if (i == 0) l.BeginFigure(new Point(x, y), false); else l.LineTo(new Point(x, y));
            }
            c.LineTo(new Point(r.Width, r.Bottom));
            c.EndFigure(true);
            l.EndFigure(false);
        }
        ctx.DrawGeometry(TraceFill, null, g);
        ctx.DrawGeometry(null, TracePen, line);
    }

    // Audio frequency, 0 at the dial (left) to 6000 Hz (right).
    private void DrawScale(DrawingContext ctx, Rect b, double top)
    {
        // Label every 500 Hz when there's room, else every 1000.
        int labelEvery = 500 / SpanHz * b.Width >= 32 ? 500 : 1000;
        for (int hz = 0; hz <= SpanHz; hz += 100)
        {
            double x = hz / SpanHz * b.Width;
            bool major = hz % 500 == 0;
            ctx.DrawLine(major ? MajorTickPen : TickPen, new Point(x, top), new Point(x, top + (major ? 4 : 2)));
            if (hz % labelEvery != 0) continue;
            var ft = new FormattedText(hz.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, LabelFace, 9, LabelBrush);
            double lx = Math.Clamp(x - ft.Width / 2, 2, b.Width - ft.Width - 2);
            ctx.DrawText(ft, new Point(lx, top + 3));
        }
    }

    // A double-sided filter wider than the box keeps its handle at the right end.
    private double HighEdgeX() => _symmetric ? Math.Min(X(_high), Bounds.Width - 3) : X(_high);

    private Edge EdgeAt(double x) =>
        Math.Abs(x - HighEdgeX()) <= Grab ? Edge.High
        : !_symmetric && Math.Abs(x - X(_low)) <= Grab ? Edge.Low
        : Edge.None;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _drag = EdgeAt(e.GetPosition(this).X);
        if (_drag == Edge.None) return;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        double x = e.GetPosition(this).X;
        if (_drag == Edge.None)
        {
            Cursor = new Cursor(EdgeAt(x) == Edge.None ? StandardCursorType.Arrow : StandardCursorType.SizeWestEast);
            return;
        }
        int hz = (int)(Math.Round(Offset(Math.Clamp(x, 0, Bounds.Width)) / StepHz) * StepHz);
        if (_symmetric) (_low, _high) = (-(int)Math.Max(hz, MinWidthHz / 2), (int)Math.Max(hz, MinWidthHz / 2));
        else if (_drag == Edge.Low) _low = (int)Math.Min(hz, _high - MinWidthHz);
        else _high = (int)Math.Max(hz, _low + MinWidthHz);
        InvalidateVisual();
        FilterDragged?.Invoke(_low, _high);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Edge.None) return;
        _drag = Edge.None;
        e.Pointer.Capture(null);
    }
}
