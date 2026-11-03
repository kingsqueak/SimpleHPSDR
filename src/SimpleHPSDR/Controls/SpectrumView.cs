using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Zeus.Contracts;

namespace SimpleHPSDR.Controls;

/// <summary>
/// Panadapter (top) and scrolling waterfall (bottom) drawn from the engine's
/// display frames. Click to tune; the wheel tunes by the step via
/// <see cref="WheelTune"/>.
/// </summary>
public sealed class SpectrumView : Control
{
    private const int WaterfallRows = 512;
    private const double PanFraction = 0.38;

    public static readonly StyledProperty<double> SpecMinDbProperty =
        AvaloniaProperty.Register<SpectrumView, double>(nameof(SpecMinDb), -130);
    public static readonly StyledProperty<double> SpecMaxDbProperty =
        AvaloniaProperty.Register<SpectrumView, double>(nameof(SpecMaxDb), -60);

    /// <summary>dB at the bottom of the panadapter trace.</summary>
    public double SpecMinDb { get => GetValue(SpecMinDbProperty); set => SetValue(SpecMinDbProperty, value); }
    /// <summary>dB at the top of the panadapter trace.</summary>
    public double SpecMaxDb { get => GetValue(SpecMaxDbProperty); set => SetValue(SpecMaxDbProperty, value); }

    static SpectrumView() => AffectsRender<SpectrumView>(SpecMinDbProperty, SpecMaxDbProperty);
    // Trace averaging per frame (30 frames/s): 0.35 settles in about 0.2 s.
    private const float TraceAlpha = 0.35f;

    private static readonly uint[] Palette = BuildPalette();
    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#0B0D11"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#1F2430")), 1);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#7D8696"));
    private static readonly IBrush TraceFill = new SolidColorBrush(Color.Parse("#333B82F6"));
    private static readonly IPen TracePen = new Pen(new SolidColorBrush(Color.Parse("#7FB2FF")), 1.2);
    private static readonly IBrush PassbandBrush = new SolidColorBrush(Color.Parse("#26FFFFFF"));
    private static readonly IPen VfoPen = new Pen(new SolidColorBrush(Color.Parse("#F25F5C")), 1.5);
    private static readonly Typeface LabelFace = new("Inter");

    private WriteableBitmap? _waterfall;
    private int _wfWidth;
    private int _wfRow;            // next row to write (ring buffer)
    private float[]? _pan;
    private long _centerHz;
    private long _avgCenterHz;
    private double _hzPerPixel;
    private double _floorDb = -130;

    /// <summary>Smoothed noise floor estimate (dB), for automatic ranging.</summary>
    public double NoiseFloorDb => _floorDb;

    public long VfoHz { get; set; }
    public int FilterLowHz { get; set; }
    public int FilterHighHz { get; set; }

    public event Action<long>? TuneRequested;
    public event Action<int>? WheelTune;

    public SpectrumView()
    {
        ClipToBounds = true;
        // Nearest-neighbour keeps waterfall rows from bleeding across the ring seam.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public void PushFrame(DisplayFrame f)
    {
        if (f.Width == 0) return;
        // A new span (zoom) makes the old waterfall rows a different scale,
        // so start the waterfall afresh. Retuning keeps them.
        if (_hzPerPixel > 0 && Math.Abs(f.HzPerPixel / _hzPerPixel - 1) > 0.01)
            ClearWaterfall();
        _centerHz = f.CenterHz;
        _hzPerPixel = f.HzPerPixel;

        if (f.BodyFlags.HasFlag(DisplayBodyFlags.PanValid))
        {
            var raw = f.PanDb.Span;
            // Restart the average after a retune or width change, otherwise
            // the old and new spectra smear together.
            if (_pan is null || _pan.Length != raw.Length || f.CenterHz != _avgCenterHz)
                _pan = raw.ToArray();
            else
                for (int i = 0; i < _pan.Length; i++)
                    _pan[i] += (raw[i] - _pan[i]) * TraceAlpha;
            _avgCenterHz = f.CenterHz;
            TrackFloor(_pan);
        }
        if (f.BodyFlags.HasFlag(DisplayBodyFlags.WfValid))
            AddWaterfallRow(f.WfDb.Span);

        InvalidateVisual();
    }

    private unsafe void ClearWaterfall()
    {
        if (_waterfall is null) return;
        using var fb = _waterfall.Lock();
        new Span<uint>((void*)fb.Address, fb.RowBytes / 4 * WaterfallRows).Fill(Palette[0]);
        _wfRow = 0;
    }

    public void Clear()
    {
        _pan = null;
        _waterfall?.Dispose();
        _waterfall = null;
        InvalidateVisual();
    }

    // Noise floor: 20th percentile, smoothed so the picture doesn't pump.
    private void TrackFloor(float[] db)
    {
        var sorted = (float[])db.Clone();
        Array.Sort(sorted);
        double p20 = sorted[sorted.Length / 5];
        double target = p20 - 10;
        _floorDb = _floorDb + (target - _floorDb) * 0.05;
    }

    private unsafe void AddWaterfallRow(ReadOnlySpan<float> row)
    {
        if (_waterfall is null || _wfWidth != row.Length)
        {
            _waterfall?.Dispose();
            _wfWidth = row.Length;
            _waterfall = new WriteableBitmap(new PixelSize(_wfWidth, WaterfallRows), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using var clear = _waterfall.Lock();
            new Span<uint>((void*)clear.Address, clear.RowBytes / 4 * WaterfallRows).Fill(Palette[0]);
            _wfRow = 0;
        }

        using var fb = _waterfall.Lock();
        var dst = new Span<uint>((byte*)fb.Address + _wfRow * fb.RowBytes, _wfWidth);
        // Same range as the trace: Spec Min is the darkest colour, Spec Max the brightest.
        double floor = SpecMinDb, scale = 255.0 / Math.Max(1, SpecMaxDb - SpecMinDb);
        for (int i = 0; i < _wfWidth; i++)
            dst[i] = Palette[Math.Clamp((int)((row[i] - floor) * scale), 0, 255)];
        _wfRow = (_wfRow + 1) % WaterfallRows;
    }

    public override void Render(DrawingContext ctx)
    {
        SimpleHPSDR.UiStats.Rendered();
        var b = Bounds;
        ctx.FillRectangle(Background, new Rect(b.Size));
        double panH = Math.Round(b.Height * PanFraction);
        var panRect = new Rect(0, 0, b.Width, panH);
        var wfRect = new Rect(0, panH, b.Width, b.Height - panH);

        DrawGrid(ctx, panRect);
        DrawTrace(ctx, panRect);
        DrawWaterfall(ctx, wfRect);
        DrawTuning(ctx, b);
    }

    private double HzToX(long hz, double width)
    {
        if (_hzPerPixel <= 0 || _wfWidth == 0 && _pan is null) return -1;
        int bins = _pan?.Length ?? _wfWidth;
        double bin = (hz - _centerHz) / _hzPerPixel + bins / 2.0;
        return bin * width / bins;
    }

    private long XToHz(double x, double width)
    {
        int bins = _pan?.Length ?? _wfWidth;
        if (bins == 0) return VfoHz;
        double bin = x * bins / width;
        return _centerHz + (long)Math.Round((bin - bins / 2.0) * _hzPerPixel);
    }

    private void DrawGrid(DrawingContext ctx, Rect r)
    {
        // Level lines every 10 dB (20 dB on a wide range), labelled at the left.
        double lo = SpecMinDb, hi = SpecMaxDb, range = Math.Max(1, hi - lo);
        double dbStep = range > 80 ? 20 : 10;
        for (double db = Math.Ceiling(lo / dbStep) * dbStep; db < hi; db += dbStep)
        {
            double y = r.Top + (hi - db) / range * r.Height;
            if (y <= r.Top + 14 || y >= r.Bottom - 1) continue;
            ctx.DrawLine(GridPen, new Point(0, y), new Point(r.Width, y));
            var label = new FormattedText($"{db:0}", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, LabelFace, 10, LabelBrush);
            ctx.DrawText(label, new Point(4, y - label.Height - 1));
        }

        int bins = _pan?.Length ?? _wfWidth;
        if (bins == 0 || _hzPerPixel <= 0) return;
        double spanHz = bins * _hzPerPixel;
        long step = NiceStep(spanHz / 8);
        long start = (_centerHz - (long)(spanHz / 2)) / step * step;
        for (long hz = start; hz <= _centerHz + spanHz / 2; hz += step)
        {
            double x = HzToX(hz, r.Width);
            if (x < 0 || x > r.Width) continue;
            ctx.DrawLine(GridPen, new Point(x, r.Top), new Point(x, r.Bottom));
            var text = new FormattedText((hz / 1e6).ToString(step >= 1000 ? "0.000" : "0.0000"),
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 11, LabelBrush);
            ctx.DrawText(text, new Point(x + 3, r.Top + 2));
        }
    }

    private static long NiceStep(double raw)
    {
        foreach (var s in new long[] { 1_000, 2_000, 2_500, 5_000, 10_000, 20_000, 25_000, 50_000, 100_000, 200_000, 500_000 })
            if (s >= raw) return s;
        return 1_000_000;
    }

    private void DrawTrace(DrawingContext ctx, Rect r)
    {
        if (_pan is not { Length: > 1 } pan) return;
        double top = SpecMaxDb, span = Math.Max(1, SpecMaxDb - SpecMinDb);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(0, r.Bottom), true);
            for (int i = 0; i < pan.Length; i++)
            {
                double x = i * r.Width / (pan.Length - 1);
                double y = r.Top + (top - pan[i]) / span * r.Height;
                g.LineTo(new Point(x, Math.Clamp(y, r.Top, r.Bottom)));
            }
            g.LineTo(new Point(r.Width, r.Bottom));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(TraceFill, null, geo);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            for (int i = 0; i < pan.Length; i++)
            {
                double x = i * r.Width / (pan.Length - 1);
                double y = Math.Clamp(r.Top + (top - pan[i]) / span * r.Height, r.Top, r.Bottom);
                if (i == 0) g.BeginFigure(new Point(x, y), false); else g.LineTo(new Point(x, y));
            }
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, TracePen, line);
    }

    private void DrawWaterfall(DrawingContext ctx, Rect r)
    {
        if (_waterfall is null) return;
        // Newest row at the top: rows [_wfRow-1 .. 0] then [N-1 .. _wfRow].
        // Drawn as two slices of the ring, flipped by drawing order.
        double rowH = r.Height / WaterfallRows;
        int newest = (_wfRow - 1 + WaterfallRows) % WaterfallRows;
        // Slice A: rows 0..newest, shown bottom-up starting at the top edge.
        DrawSlice(ctx, r, 0, newest + 1, 0, rowH);
        // Slice B: rows newest+1..N-1 (older), below slice A.
        DrawSlice(ctx, r, newest + 1, WaterfallRows - newest - 1, newest + 1, rowH);
    }

    private void DrawSlice(DrawingContext ctx, Rect r, int firstRow, int count, int offsetRows, double rowH)
    {
        if (count <= 0) return;
        // Bitmap rows are stored oldest-first in each slice; flip vertically so
        // the newest row lands at the top of the waterfall.
        var src = new Rect(0, firstRow, _wfWidth, count);
        var dst = new Rect(0, r.Top + offsetRows * rowH, r.Width, count * rowH);
        using (ctx.PushTransform(Matrix.CreateTranslation(0, -dst.Center.Y)
                                 * Matrix.CreateScale(1, -1)
                                 * Matrix.CreateTranslation(0, dst.Center.Y)))
            ctx.DrawImage(_waterfall!, src, dst);
    }

    private void DrawTuning(DrawingContext ctx, Rect b)
    {
        double vx = HzToX(VfoHz, b.Width);
        if (vx < 0) return;
        double lx = HzToX(VfoHz + FilterLowHz, b.Width);
        double hx = HzToX(VfoHz + FilterHighHz, b.Width);
        ctx.FillRectangle(PassbandBrush, new Rect(Math.Min(lx, hx), 0, Math.Max(1, Math.Abs(hx - lx)), b.Height));
        ctx.DrawLine(VfoPen, new Point(vx, 0), new Point(vx, b.Height));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var hz = XToHz(e.GetPosition(this).X, Bounds.Width);
        TuneRequested?.Invoke(hz);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        int steps = Math.Sign(e.Delta.Y);
        if (steps != 0) WheelTune?.Invoke(steps);
        e.Handled = true;
    }

    // Dark blue → blue → cyan → yellow → red → white.
    private static uint[] BuildPalette()
    {
        (double t, byte r, byte g, byte b)[] stops =
        [
            (0.00, 0x0B, 0x0D, 0x11), (0.20, 0x0A, 0x1E, 0x5A), (0.40, 0x10, 0x6B, 0xD8),
            (0.60, 0x2E, 0xD3, 0xC8), (0.75, 0xF5, 0xD0, 0x3B), (0.90, 0xF2, 0x5F, 0x2C), (1.00, 0xFF, 0xFF, 0xFF),
        ];
        var lut = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            int k = 0;
            while (k < stops.Length - 2 && t > stops[k + 1].t) k++;
            var (t0, r0, g0, b0) = stops[k];
            var (t1, r1, g1, b1) = stops[k + 1];
            double f = (t - t0) / (t1 - t0);
            byte R = (byte)(r0 + (r1 - r0) * f), G = (byte)(g0 + (g1 - g0) * f), B = (byte)(b0 + (b1 - b0) * f);
            lut[i] = 0xFF000000u | (uint)(R << 16) | (uint)(G << 8) | B;
        }
        return lut;
    }
}
