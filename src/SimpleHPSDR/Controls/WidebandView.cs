using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.Controls;

/// <summary>
/// 0-60 MHz from the radio's ADC: a trace on top, a waterfall below, the
/// amateur bands shaded, the receiver's frequency marked, and the frequency
/// and level under the pointer. The level scale follows the signal by itself.
/// </summary>
public sealed class WidebandView : Control
{
    public static readonly StyledProperty<StationClient.WidebandRow?> RowProperty =
        AvaloniaProperty.Register<WidebandView, StationClient.WidebandRow?>(nameof(Row));
    public static readonly StyledProperty<long> VfoHzProperty =
        AvaloniaProperty.Register<WidebandView, long>(nameof(VfoHz));

    public StationClient.WidebandRow? Row { get => GetValue(RowProperty); set => SetValue(RowProperty, value); }
    public long VfoHz { get => GetValue(VfoHzProperty); set => SetValue(VfoHzProperty, value); }

    static WidebandView() => AffectsRender<WidebandView>(VfoHzProperty);

    private const int WaterfallWidth = 1024, WaterfallRows = 300;
    private const double Axis = 22, LeftScale = 40;
    private static readonly IBrush BandShade = new SolidColorBrush(Color.Parse("#263DD68C"));
    private static readonly IBrush BandText = new SolidColorBrush(Color.Parse("#B03DD68C"));
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#8A93A3"));
    private static readonly IBrush Readout = new SolidColorBrush(Color.Parse("#E6E8EB"));
    private static readonly IBrush ReadoutBack = new SolidColorBrush(Color.Parse("#C0101318"));
    private static readonly IPen Grid = new Pen(new SolidColorBrush(Color.Parse("#2A2F38")), 1);
    private static readonly IPen Vfo = new Pen(new SolidColorBrush(Color.Parse("#E5484D")), 1);
    private static readonly IPen CursorLine = new Pen(new SolidColorBrush(Color.Parse("#66FFFFFF")), 1);
    private static readonly Typeface Face = new("Inter");

    private WriteableBitmap? _waterfall;
    private uint[] _palette = SpectrumLook.Current.Palette;
    private double _floor = double.NaN, _top = double.NaN;
    private float[]? _sort;
    private Point? _pointer;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowProperty && Row is { } row)
        {
            Rescale(row.Trace);
            AddWaterfallRow(row.Waterfall);
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        Point at = new(Math.Round(p.X), Math.Round(p.Y));
        if (_pointer == at) return;
        _pointer = at;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _waterfall?.Dispose();
        _waterfall = null;
    }

    // The scale follows the band noise and the strongest signal, eased so it doesn't pump.
    private void Rescale(float[] trace)
    {
        if (trace.Length == 0) return;
        if (_sort is null || _sort.Length != trace.Length) _sort = new float[trace.Length];
        trace.CopyTo(_sort, 0);
        Array.Sort(_sort);
        double floor = _sort[_sort.Length / 10] - 8, top = _sort[^1] + 6;
        if (top - floor < 30) top = floor + 30;
        bool first = double.IsNaN(_floor);
        _floor = first ? floor : _floor + (floor - _floor) * 0.15;
        _top = first ? top : _top + (top - _top) * 0.15;
    }

    private unsafe void AddWaterfallRow(float[] row)
    {
        if (!ReferenceEquals(_palette, SpectrumLook.Current.Palette))
        {
            _palette = SpectrumLook.Current.Palette;
            _waterfall?.Dispose();
            _waterfall = null;
        }
        if (_waterfall is null)
        {
            _waterfall = new WriteableBitmap(new PixelSize(WaterfallWidth, WaterfallRows), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using var clear = _waterfall.Lock();
            new Span<uint>((void*)clear.Address, clear.RowBytes / 4 * WaterfallRows).Fill(_palette[0]);
        }
        using var fb = _waterfall.Lock();
        int stride = fb.RowBytes;
        // Newest row on top: move the rest down one.
        Buffer.MemoryCopy((void*)fb.Address, (byte*)fb.Address + stride, (long)stride * (WaterfallRows - 1),
            (long)stride * (WaterfallRows - 1));
        var dst = new Span<uint>((void*)fb.Address, WaterfallWidth);
        double floor = double.IsNaN(_floor) ? -120 : _floor, scale = 255.0 / Math.Max(1, (double.IsNaN(_top) ? -40 : _top) - floor);
        int per = Math.Max(1, row.Length / WaterfallWidth);
        for (int x = 0; x < WaterfallWidth; x++)
        {
            float peak = float.MinValue;
            int i0 = x * row.Length / WaterfallWidth;
            for (int i = i0; i < Math.Min(row.Length, i0 + per); i++) if (row[i] > peak) peak = row[i];
            dst[x] = _palette[Math.Clamp((int)((peak - floor) * scale), 0, 255)];
        }
    }

    private static FormattedText Text(string s, double size, IBrush brush) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, brush);

    public override void Render(DrawingContext g)
    {
        var b = Bounds;
        g.FillRectangle(SpectrumLook.Current.Background, new Rect(b.Size));
        if (Row is not { } row || row.Trace.Length < 2)
            return;

        double w = Math.Max(10, b.Width - LeftScale);
        double traceH = Math.Max(40, (b.Height - Axis) * 0.4);
        var trace = new Rect(LeftScale, 0, w, traceH);
        var fall = new Rect(LeftScale, traceH, w, Math.Max(10, b.Height - Axis - traceH));
        double lo = row.LowHz, span = row.SpanHz;
        double X(double hz) => LeftScale + (hz - lo) / span * w;
        double floor = _floor, top = _top;
        double Y(double db) => trace.Bottom - Math.Clamp((db - floor) / Math.Max(1, top - floor), 0, 1) * trace.Height;

        // Amateur bands.
        foreach (var band in SimpleHPSDR.Band.All)
        {
            double x0 = X(band.LowHz), x1 = X(band.HighHz);
            if (x1 < LeftScale || x0 > b.Width) continue;
            g.FillRectangle(BandShade, new Rect(x0, 0, Math.Max(2, x1 - x0), fall.Bottom));
            var t = Text(band.Name, 10, BandText);
            g.DrawText(t, new Point(Math.Clamp((x0 + x1 - t.Width) / 2, LeftScale, b.Width - t.Width), 2));
        }

        // Level grid every 10 dB.
        for (double db = Math.Ceiling(floor / 10) * 10; db <= top; db += 10)
        {
            double y = Y(db);
            g.DrawLine(Grid, new Point(LeftScale, y), new Point(b.Width, y));
            var t = Text(db.ToString("0", CultureInfo.CurrentCulture), 10, Label);
            g.DrawText(t, new Point(LeftScale - t.Width - 4, y - t.Height / 2));
        }

        // Frequency axis: every 5 MHz, or 2 when the window is narrow.
        double stepMhz = w < 700 ? 10 : w < 1100 ? 5 : 2;
        for (double mhz = 0; mhz <= 60; mhz += stepMhz)
        {
            double x = X(mhz * 1e6);
            if (x < LeftScale - 1 || x > b.Width + 1) continue;
            g.DrawLine(Grid, new Point(x, 0), new Point(x, trace.Bottom));
            var t = Text(mhz.ToString("0", CultureInfo.CurrentCulture), 11, Label);
            g.DrawText(t, new Point(Math.Clamp(x - t.Width / 2, LeftScale, b.Width - t.Width), fall.Bottom + 4));
        }

        // Trace: the strongest point in each pixel column, so narrow carriers keep their height.
        var data = row.Trace;
        int cols = Math.Max(2, (int)w);
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            for (int x = 0; x < cols; x++)
            {
                int i0 = (int)((long)x * data.Length / cols), i1 = Math.Max(i0 + 1, (int)((long)(x + 1) * data.Length / cols));
                float peak = float.MinValue;
                for (int i = i0; i < Math.Min(i1, data.Length); i++) if (data[i] > peak) peak = data[i];
                var p = new Point(LeftScale + x * w / cols, Y(peak));
                if (x == 0) c.BeginFigure(p, false); else c.LineTo(p);
            }
            c.EndFigure(false);
        }
        g.DrawGeometry(null, SpectrumLook.Current.LinePen, geo);

        if (_waterfall is { } bmp)
            g.DrawImage(bmp, new Rect(0, 0, WaterfallWidth, WaterfallRows), fall);

        // Where the receiver is tuned.
        if (VfoHz > 0)
        {
            double vx = X(VfoHz);
            if (vx >= LeftScale && vx <= b.Width) g.DrawLine(Vfo, new Point(vx, 0), new Point(vx, fall.Bottom));
        }

        if (_pointer is { } at && at.X >= LeftScale && at.Y <= fall.Bottom)
        {
            double hz = lo + (at.X - LeftScale) / w * span;
            int idx = Math.Clamp((int)((at.X - LeftScale) / w * data.Length), 0, data.Length - 1);
            g.DrawLine(CursorLine, new Point(at.X, 0), new Point(at.X, fall.Bottom));
            var t = Text($"{hz / 1e6:0.000} MHz   {data[idx]:0} dB", 12, Readout);
            var box = new Rect(Math.Clamp(at.X + 8, LeftScale, b.Width - t.Width - 8), traceH - t.Height - 10, t.Width + 8, t.Height + 4);
            g.FillRectangle(ReadoutBack, box, 3);
            g.DrawText(t, new Point(box.X + 4, box.Y + 2));
        }
    }
}
