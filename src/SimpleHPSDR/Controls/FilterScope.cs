using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Controls;

/// <summary>
/// A close-up of the received signal as audio: 0 to 6000 Hz from the dial
/// (3000 Hz in CW), left to right, whatever the sideband (on lower sideband the RF below the
/// dial is shown mirrored). Cut from the active slice's panadapter frames,
/// with the receive filter shaded over it. Drag either edge of the shading
/// to set the filter's low or high cut (10 Hz steps). In AM and the other
/// double-sided modes only the upper side is shown, and its one edge sets both
/// sides together. The scale marks every 100 Hz, labelled every 500 Hz. The dB
/// range follows what's inside the span, so the signal fills the box.
/// A box along the top shows the low and high cuts in Hz, updated as you drag.
/// </summary>
public sealed class FilterScope : Control
{
    private const double VoiceSpanHz = 6000, CwSpanHz = 3000;
    private double SpanHz => _pitch > 0 ? CwSpanHz : VoiceSpanHz;
    private const double StepHz = 10, MinWidthHz = 50;
    private const double ScaleH = 13;         // tick and label strip at the bottom
    /// <summary>How close the pointer must be to take an edge (wider for fingers).</summary>
    public double GrabDistance { get; set; } = 6;
    private const float TraceAlpha = 0.5f;

    /// <summary>An edge was dragged: the new (low, high) offsets from the dial.</summary>
    public event Action<int, int>? FilterDragged;

    // Colours from Config > Colors, as on the spectrum.
    private static IBrush Background => SpectrumLook.Current.Background;
    private static IBrush TraceFill => SpectrumLook.Current.Fill;
    private static IPen TracePen => SpectrumLook.Current.LinePen;
    private static readonly IBrush PassBrush = new SolidColorBrush(Color.Parse("#2E3B82F6"));
    private static readonly IPen EdgePen = new Pen(new SolidColorBrush(Color.Parse("#3B82F6")), 1.5);
    private static readonly IBrush HandleBrush = new SolidColorBrush(Color.Parse("#E6E8EB"));
    private static readonly IPen TickPen = new Pen(new SolidColorBrush(Color.Parse("#3A404C")), 1);
    private static readonly IPen MajorTickPen = new Pen(new SolidColorBrush(Color.Parse("#7D8696")), 1);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#7D8696"));
    private static readonly IBrush ReadoutBg = new SolidColorBrush(Color.Parse("#D0101216"));
    private static readonly IPen ReadoutPen = new Pen(new SolidColorBrush(Color.Parse("#3B82F6")), 1);
    private static readonly IBrush ReadoutText = new SolidColorBrush(Color.Parse("#E6E8EB"));
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
    private int _pitch;   // CW: a signal on the dial is this audio tone; the scale stays audio
    private enum Edge { None, Low, High }
    private Edge _drag;

    public FilterScope()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    /// <summary>A display frame from the active slice's receiver, with that
    /// slice's dial and filter.</summary>
    public void PushFrame(DisplayFrame f, long dialHz, int low, int high, bool symmetric, int cwPitchHz = 0)
    {
        _rx = f.RxId;
        _dialHz = dialHz;
        if (_drag == Edge.None)
        {
            (_low, _high) = (low, high);
            _symmetric = symmetric;
            _pitch = cwPitchHz;
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
        // Too small to draw into (a panel that's closing or still opening).
        if (b.Width < 60 || b.Height <= ScaleH) return;
        var plot = new Rect(0, 0, b.Width, Math.Max(0, b.Height - ScaleH));

        DrawTrace(ctx, plot);

        // Passband and its draggable edges
        double xl = X(_low), xh = HighEdgeX();
        // Lower-sideband edges run right to left.
        ctx.FillRectangle(PassBrush, new Rect(Math.Min(xl, xh), 0, Math.Abs(xh - xl), plot.Height));
        foreach (var x in _symmetric ? [xh] : new[] { xl, xh })
        {
            ctx.DrawLine(EdgePen, new Point(x, 0), new Point(x, plot.Height));
            ctx.FillRectangle(HandleBrush, new Rect(x - 2.5, plot.Height / 2 - 8, 5, 16), 2.5f);
        }

        // Dial
        var dial = new Pen(new SolidColorBrush(DialColors[Math.Clamp(_rx, 0, 1)]), 1);
        double dialX = _pitch == 0 ? 1 : _pitch / SpanHz * b.Width;
        ctx.DrawLine(dial, new Point(dialX, 0), new Point(dialX, plot.Height));

        DrawScale(ctx, b, plot.Bottom);
        DrawReadout(ctx, b);
    }

    // The filter's low and high cuts as audio Hz (as on the scale), in a box
    // a third of the height, centred along the top. Redrawn on every drag.
    private void DrawReadout(DrawingContext ctx, Rect b)
    {
        double h = Math.Round(b.Height / 3);
        if (h < 10) return;
        int a = _side * _low, z = _side * _high;
        string text = _symmetric ? $"\u00B1{_high} Hz" : $"{Math.Min(a, z)} \u2013 {Math.Max(a, z)} Hz";
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, LabelFace, Math.Clamp(h * 0.62, 9, 16), ReadoutText);
        double w = Math.Min(b.Width - 4, ft.Width + h * 0.8);
        var box = new Rect((b.Width - w) / 2, 2, w, h);
        ctx.DrawRectangle(ReadoutBg, ReadoutPen, box, 4, 4);
        ctx.DrawText(ft, new Point(box.X + (w - ft.Width) / 2, box.Y + (h - ft.Height) / 2));
    }

    private void DrawTrace(DrawingContext ctx, Rect r)
    {
        if (_pan is not { Length: > 1 } pan || _hzPerBin <= 0 || r.Height <= 0) return;
        int cols = Math.Max(2, (int)r.Width);
        if (_cols.Length != cols) _cols = new double[cols];
        for (int i = 0; i < cols; i++)
        {
            double hz = _dialHz + Offset(i * r.Width / (cols - 1)) - _side * _pitch;
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
            double lx = Math.Clamp(x - ft.Width / 2, 2, Math.Max(2, b.Width - ft.Width - 2));
            ctx.DrawText(ft, new Point(lx, top + 3));
        }
    }

    // A double-sided filter wider than the box keeps its handle at the right end.
    private double HighEdgeX() => _symmetric ? Math.Min(X(_high), Bounds.Width - 3) : X(_high);

    private Edge EdgeAt(double x) =>
        Math.Abs(x - HighEdgeX()) <= GrabDistance ? Edge.High
        : !_symmetric && Math.Abs(x - X(_low)) <= GrabDistance ? Edge.Low
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
