using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Zeus.Contracts;

namespace SimpleHPSDR.Controls;

/// <summary>One slice to draw: its receiver, dial, passband edges (offsets
/// from the dial), tag, and whether it's the active, transmitting or muted one.</summary>
public sealed record SliceMark(int Rx, long Hz, int LowHz, int HighHz, string Tag,
    bool Active, bool Transmit, bool Muted);

/// <summary>A manual notch to draw: its RF centre and width.</summary>
public sealed record NotchMark(long Hz, double WidthHz, bool Active);

/// <summary>
/// Panadapter (top) and scrolling waterfall (bottom) drawn from the engine's
/// display frames. Click to tune, drag to move the band under the display
/// (the dial stays where it is); the wheel tunes by the step via
/// <see cref="WheelTune"/>.
/// </summary>
public sealed class SpectrumView : Control
{
    private const int WaterfallRows = 512;
    // Split between spectrum (top) and waterfall, moved with the drag bar.
    public static readonly StyledProperty<double> PanFractionProperty =
        AvaloniaProperty.Register<SpectrumView, double>(nameof(PanFraction), 0.38,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public double PanFraction { get => GetValue(PanFractionProperty); set => SetValue(PanFractionProperty, value); }
    private const double MinPanFraction = 0.15, MaxPanFraction = 0.85;
    private const double GripHalfHeight = 4;   // pointer distance that grabs the bar
    private static readonly IBrush GripBrush = new SolidColorBrush(Color.Parse("#2A2F3A"));
    private static readonly IBrush GripDots = new SolidColorBrush(Color.Parse("#7D8696"));
    private bool _dragging;
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor PanCursor = new(StandardCursorType.SizeWestEast);

    public static readonly StyledProperty<double> SpecMinDbProperty =
        AvaloniaProperty.Register<SpectrumView, double>(nameof(SpecMinDb), -130);
    public static readonly StyledProperty<double> SpecMaxDbProperty =
        AvaloniaProperty.Register<SpectrumView, double>(nameof(SpecMaxDb), -60);

    /// <summary>dB at the bottom of the panadapter trace.</summary>
    public double SpecMinDb { get => GetValue(SpecMinDbProperty); set => SetValue(SpecMinDbProperty, value); }
    /// <summary>dB at the top of the panadapter trace.</summary>
    public double SpecMaxDb { get => GetValue(SpecMaxDbProperty); set => SetValue(SpecMaxDbProperty, value); }

    public static readonly StyledProperty<bool> PopModeProperty =
        AvaloniaProperty.Register<SpectrumView, bool>(nameof(PopMode));
    /// <summary>POP: new waterfall rows follow the noise floor instead of Spec
    /// Min/Max, with a curve that keeps noise dark and lifts weak signals.</summary>
    public bool PopMode { get => GetValue(PopModeProperty); set => SetValue(PopModeProperty, value); }
    private const double PopFloorDb = 3, PopSpanDb = 30;   // above the floor; gamma is 0.5

    static SpectrumView() => AffectsRender<SpectrumView>(SpecMinDbProperty, SpecMaxDbProperty, PanFractionProperty);
    // Trace averaging per frame (30 frames/s): 0.35 settles in about 0.2 s.
    private const float TraceAlpha = 0.35f;

    private static readonly uint[] Palette = BuildPalette();
    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#0B0D11"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#1F2430")), 1);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#7D8696"));
    private static readonly IBrush TraceFill = new SolidColorBrush(Color.Parse("#333B82F6"));
    private static readonly IPen TracePen = new Pen(new SolidColorBrush(Color.Parse("#7FB2FF")), 1.2);
    private static readonly IBrush PassbandBrush = new SolidColorBrush(Color.Parse("#26FFFFFF"));
    private static readonly IBrush PassbandInactive = new SolidColorBrush(Color.Parse("#14FFFFFF"));
    // Slice colours: A red, B amber.
    private static readonly Color[] SliceColors = [Color.Parse("#F25F5C"), Color.Parse("#F5A524")];
    private static readonly IBrush TxBrush = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly Typeface TagFace = new("Inter", FontStyle.Normal, FontWeight.SemiBold);
    private readonly List<(Rect Box, int Rx)> _tagBoxes = [];
    private static readonly Typeface LabelFace = new("Inter");

    private WriteableBitmap? _waterfall;
    private int _wfWidth;
    private int _wfRow;            // next row to write (ring buffer)
    private float[]? _pan;
    private long _centerHz;
    private long _avgCenterHz;
    private long _wfCenterHz;     // centre the stored waterfall rows are lined up to
    private float[]? _popSort;
    private double _popFloor = double.NaN;
    private double _hzPerPixel;
    private double _floorDb = -130;

    /// <summary>Smoothed noise floor estimate (dB), for automatic ranging.</summary>
    public double NoiseFloorDb => _floorDb;

    /// <summary>While transmitting: new waterfall rows span this many dB below
    /// the transmit signal's peak, so the audio's shape shows. 0 uses the
    /// receive range.</summary>
    public double TxRangeDb { get; set; }
    private double _txPeakDb = double.NaN;

    /// <summary>Keyed: the trace slides down so the transmit signal stays on
    /// screen, and the noise-floor estimate holds still.</summary>
    public bool Transmitting { get; set; }
    private double _txTracePeakDb = double.NaN;
    // While keyed, the transmit peak sits this far down from the top of the trace.
    private const double TxHeadroom = 0.15;

    /// <summary>The trace's dB range: the Spec Min/Max one, moved down by
    /// just enough while keyed that the transmit peak sits below the top.
    /// The span stays the same, so the shape compares with receive.</summary>
    private (double Lo, double Hi) TraceRange()
    {
        double lo = SpecMinDb, hi = SpecMaxDb, span = Math.Max(1, hi - lo);
        if (!Transmitting || double.IsNaN(_txTracePeakDb)) return (lo, hi);
        double lift = Math.Max(0, _txTracePeakDb - (hi - TxHeadroom * span));
        return (lo + lift, hi + lift);
    }

    /// <summary>Slices to draw (A, and B when on).</summary>
    public IReadOnlyList<SliceMark> Slices { get; set; } = [];

    /// <summary>The band strip along the top (empty: none).</summary>
    public IReadOnlyList<StripSpan> Strip { get; set; } = [];
    // A little taller than the frequency labels, which sit just under it.
    private const double StripHeight = 18;
    private double StripH => Strip.Count > 0 ? StripHeight : 0;
    private static readonly IBrush StripBase = new SolidColorBrush(Color.Parse("#14171E"));
    private static readonly IBrush StripAllowed = new SolidColorBrush(Color.Parse("#2E7D4F"));
    private static readonly IBrush StripNotAllowed = new SolidColorBrush(Color.Parse("#A33A3A"));
    private static readonly IBrush StripOther = new SolidColorBrush(Color.Parse("#555B66"));
    private static readonly IPen StripEdge = new Pen(Brushes.White, 1);

    /// <summary>Manual notches to draw.</summary>
    public IReadOnlyList<NotchMark> Notches { get; set; } = [];
    private static readonly IBrush NotchBrush = new SolidColorBrush(Color.Parse("#40E5484D"));

    /// <summary>Where to draw the yellow AGC line (dB), NaN for none.</summary>
    public double AgcLineDb { get; set; } = double.NaN;
    private static readonly IPen AgcPen = new Pen(new SolidColorBrush(Color.Parse("#F5C518")), 1);
    private static readonly IBrush AgcBrush = new SolidColorBrush(Color.Parse("#F5C518"));

    /// <summary>A slice's tag was clicked: make it active.</summary>
    public event Action<int>? SliceClicked;
    /// <summary>Right-click: the frequency under the pointer.</summary>
    public event Action<long>? MenuRequested;

    public event Action<long>? TuneRequested;
    /// <summary>The panadapter was dragged: the display centre it should move to.</summary>
    public event Action<long>? PanRequested;
    /// <summary>The button came up after a drag.</summary>
    public event Action? PanEnded;

    /// <summary>A quick drag released while still moving keeps the display
    /// gliding and slowing down, as a phone list does.</summary>
    public bool Fling { get; set; }
    public event Action<int>? WheelTune;
    /// <summary>A pinch (trackpad or two fingers): +1 zoom in, -1 zoom out.</summary>
    public event Action<int>? ZoomRequested;

    public SpectrumView()
    {
        ClipToBounds = true;
        // Smooth scaling: 2048 bins onto ~1200 pixels with nearest-neighbour
        // drops bins and looks gritty. Each ring slice is clipped to its own
        // rectangle so rows can't blend across the ring seam.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        Cursor = CrossCursor;
        // Pinch to zoom: the Mac trackpad's pinch, and two fingers on a touch
        // screen. Each step is a large enough change in scale.
        AddHandler(PointerTouchPadGestureMagnifyEvent, (_, e) =>
        {
            _magnify += e.Delta.X;
            if (Math.Abs(_magnify) < MagnifyStep) return;
            ZoomRequested?.Invoke(Math.Sign(_magnify));
            _magnify = 0;
            e.Handled = true;
        });
        GestureRecognizers.Add(new PinchGestureRecognizer());
        AddHandler(PinchEvent, (_, e) =>
        {
            CancelPress();
            double ratio = e.Scale / _pinchBase;
            if (ratio is > 1 / PinchStep and < PinchStep) return;
            ZoomRequested?.Invoke(ratio > 1 ? 1 : -1);
            _pinchBase = e.Scale;
            e.Handled = true;
        });
        AddHandler(PinchEndedEvent, (_, _) => _pinchBase = 1);
        // A long press (LongPress turns it into a context request) opens the
        // slice menu where it started, as a right-click does. A right-click
        // opens it on press already, and leaves no press pending here.
        ContextRequested += (_, e) =>
        {
            if (_pressAt is not { } at) return;
            _pressAt = null;
            _panning = false;
            _hover = null;
            MenuRequested?.Invoke(XToHz(at.X, Bounds.Width));
            e.Handled = true;
        };
    }

    public void PushFrame(DisplayFrame f)
    {
        if (f.Width == 0) return;
        // A new span (zoom) makes the old waterfall rows a different scale,
        // so start the waterfall afresh. Retuning keeps them.
        if (_hzPerPixel > 0 && Math.Abs(f.HzPerPixel / _hzPerPixel - 1) > 0.01)
        {
            ClearWaterfall();
            _wfCenterHz = f.CenterHz;
        }
        else if (_wfCenterHz != 0 && f.HzPerPixel > 0)
        {
            // The centre moved: slide the history with it so signals stay lined up.
            int shift = (int)Math.Round((f.CenterHz - _wfCenterHz) / f.HzPerPixel);
            if (shift != 0)
            {
                ShiftWaterfall(shift);
                _wfCenterHz += (long)Math.Round(shift * f.HzPerPixel);
            }
        }
        if (_wfCenterHz == 0) _wfCenterHz = f.CenterHz;
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
            if (Transmitting)
            {
                // Peak of the trace: up at once, down slowly, so the scale doesn't pump with speech.
                double peak = _pan.Max();
                _txTracePeakDb = double.IsNaN(_txTracePeakDb) || peak > _txTracePeakDb
                    ? peak : _txTracePeakDb + (peak - _txTracePeakDb) * 0.03;
            }
            else
            {
                _txTracePeakDb = double.NaN;
                TrackFloor(_pan);   // keyed frames would drag the receive floor up
            }
        }
        if (f.BodyFlags.HasFlag(DisplayBodyFlags.WfValid))
            AddWaterfallRow(f.WfDb.Span);

        InvalidateVisual();
    }

    // Move every stored row sideways by bins (positive: towards the left).
    private unsafe void ShiftWaterfall(int bins)
    {
        if (_waterfall is null || _wfWidth == 0) return;
        using var fb = _waterfall.Lock();
        int n = Math.Min(Math.Abs(bins), _wfWidth);
        for (int r = 0; r < WaterfallRows; r++)
        {
            var row = new Span<uint>((byte*)fb.Address + r * fb.RowBytes, _wfWidth);
            if (n < _wfWidth)
            {
                if (bins > 0) row[n..].CopyTo(row);
                else row[..^n].CopyTo(row[n..]);
            }
            (bins > 0 ? row[(_wfWidth - n)..] : row[..n]).Fill(Palette[0]);
        }
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
        _wfCenterHz = 0;
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
        double target = p20 - 3;   // Auto: the noise sits just above the bottom of the trace
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
        if (TxRangeDb > 0)
        {
            // Follow the row's peak: up at once, down slowly, so a pause in
            // speech doesn't stretch the noise floor to full brightness.
            float peak = float.MinValue;
            foreach (var v in row) if (v > peak) peak = v;
            _txPeakDb = double.IsNaN(_txPeakDb) || peak > _txPeakDb ? peak : _txPeakDb + (peak - _txPeakDb) * 0.05;
            double top = _txPeakDb + 1;
            floor = top - TxRangeDb;
            scale = 255.0 / TxRangeDb;
        }
        else _txPeakDb = double.NaN;
        if (PopMode && TxRangeDb <= 0)
        {
            // The floor comes from the waterfall data itself: the 30th
            // percentile of the row, eased so rows don't flicker.
            _popSort ??= new float[_wfWidth];
            if (_popSort.Length != _wfWidth) _popSort = new float[_wfWidth];
            row.CopyTo(_popSort);
            Array.Sort(_popSort);
            double level = _popSort[_wfWidth * 3 / 10];
            _popFloor = double.IsNaN(_popFloor) ? level : _popFloor + (level - _popFloor) * 0.1;
            double pop = _popFloor + PopFloorDb;
            for (int i = 0; i < _wfWidth; i++)
                dst[i] = Palette[(int)(Math.Sqrt(Math.Clamp((row[i] - pop) / PopSpanDb, 0, 1)) * 255)];
        }
        else
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
        DrawAgcLine(ctx, panRect);
        DrawWaterfall(ctx, wfRect);
        DrawStrip(ctx, panRect);
        DrawNotches(ctx, b);
        DrawTuning(ctx, b);
        DrawGrip(ctx, b, panH);
        DrawHover(ctx, b);
    }

    private void DrawNotches(DrawingContext ctx, Rect b)
    {
        foreach (var n in Notches)
        {
            double lo = HzToX((long)(n.Hz - n.WidthHz / 2), b.Width), hi = HzToX((long)(n.Hz + n.WidthHz / 2), b.Width);
            if (hi < 0 || lo > b.Width) continue;
            ctx.FillRectangle(NotchBrush, new Rect(lo, 0, Math.Max(2, hi - lo), b.Height));
        }
    }

    private void DrawAgcLine(DrawingContext ctx, Rect r)
    {
        if (double.IsNaN(AgcLineDb)) return;
        var (lo, hi) = TraceRange();
        double range = Math.Max(1, hi - lo);
        double y = Math.Floor(r.Top + (hi - AgcLineDb) / range * r.Height) + 0.5;
        if (y < r.Top || y > r.Bottom) return;
        ctx.DrawLine(AgcPen, new Point(0, y), new Point(r.Width, y));
        var ft = new FormattedText("AGC", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, LabelFace, 10, AgcBrush);
        ctx.DrawText(ft, new Point(r.Width - ft.Width - 4, y - ft.Height - 1 < r.Top ? y + 1 : y - ft.Height - 1));
    }

    // Hover: a 1 px line through the pointer, top to bottom, with the
    // frequency beside it (to the right, or left near the right edge).
    private Point? _hover;
    private static readonly IPen HoverPen = new Pen(new SolidColorBrush(Color.Parse("#B3E6E8EB")), 1);
    private static readonly IBrush HoverTagBrush = new SolidColorBrush(Color.Parse("#E61F232C"));
    private static readonly IBrush HoverTextBrush = new SolidColorBrush(Color.Parse("#E6E8EB"));

    private void DrawHover(DrawingContext ctx, Rect b)
    {
        if (_hover is not { } p || _dragging || _hzPerPixel <= 0) return;
        double x = Math.Floor(p.X) + 0.5;
        ctx.DrawLine(HoverPen, new Point(x, 0), new Point(x, b.Height));
        long hz = XToHz(p.X, b.Width);
        var ft = new FormattedText($"{hz / 1e6:0.000000} MHz", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, LabelFace, 11, HoverTextBrush);
        double w = ft.Width + 10, h = ft.Height + 4;
        double tx = x + 8 + w <= b.Width ? x + 8 : x - 8 - w;
        double ty = Math.Clamp(p.Y - h / 2, 0, b.Height - h);
        ctx.FillRectangle(HoverTagBrush, new Rect(tx, ty, w, h), 3);
        ctx.DrawText(ft, new Point(tx + 5, ty + 2));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    private double SplitY => Math.Round(Bounds.Height * PanFraction);

    // The drag bar between spectrum and waterfall: a thin band with a grip.
    private void DrawGrip(DrawingContext ctx, Rect b, double y)
    {
        ctx.FillRectangle(GripBrush, new Rect(0, y - 1.5, b.Width, 3));
        double cx = b.Width / 2;
        for (int i = -2; i <= 2; i++)
            ctx.FillRectangle(GripDots, new Rect(cx + i * 6 - 1, y - 1, 2, 2));
    }

    // Left button down on the display: a click tunes on release, unless the
    // pointer moves far enough to be a drag, which pans instead.
    // A finger wobbles more than a mouse before it means to drag.
    private const double PanThreshold = 5, TouchPanThreshold = 12;
    private const double MagnifyStep = 0.25, PinchStep = 1.4;
    private double _magnify, _pinchBase = 1;
    private bool _pressIsTouch;
    private int _touches;
    private Point? _pressAt;

    // Flick to glide: the last moves of a pan give its speed at release.
    private const double FlingMinSpeed = 600;     // px/s at release to glide at all
    private const double FlingStopSpeed = 40;     // px/s where it stops
    private const double FlingFriction = 4;       // slowing, 1/s: glides v/4 px in all
    private static readonly TimeSpan FlingWindow = TimeSpan.FromMilliseconds(80);
    private readonly List<(long Ticks, double X)> _moves = [];
    private DispatcherTimer? _flingTimer;
    private double _flingV, _flingX, _flingOriginX;
    private long _flingCenterHz, _flingLast;
    private long _pressCenterHz;
    private bool _panning;

    private bool OverGrip(Point p) => Math.Abs(p.Y - SplitY) <= GripHalfHeight;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_dragging)
        {
            PanFraction = Math.Clamp(p.Y / Math.Max(1, Bounds.Height), MinPanFraction, MaxPanFraction);
            e.Handled = true;
            return;
        }
        if (_pressAt is { } at && Bounds.Width > 0)
        {
            _panning |= Math.Abs(p.X - at.X) >= (_pressIsTouch ? TouchPanThreshold : PanThreshold);
            if (_panning)
            {
                // Follow the pointer: dragging right brings lower frequencies in.
                int bins = _pan?.Length ?? _wfWidth;
                double hz = (at.X - p.X) * bins / Bounds.Width * _hzPerPixel;
                PanRequested?.Invoke(_pressCenterHz + (long)Math.Round(hz));
                long now = Stopwatch.GetTimestamp();
                _moves.Add((now, p.X));
                while (_moves.Count > 2 && Stopwatch.GetElapsedTime(_moves[0].Ticks, now) > FlingWindow) _moves.RemoveAt(0);
                Cursor = PanCursor;
                _hover = null;
                e.Handled = true;
                return;
            }
        }
        Cursor = OverGrip(p) ? ResizeCursor : CrossCursor;
        _hover = OverGrip(p) ? null : p;
        InvalidateVisual();
    }

    // At release: glide on if the pan was still moving fast, measured over its
    // last moves (a drag that stopped before release doesn't glide).
    private bool StartFling(double originX)
    {
        if (!Fling || _moves.Count < 2 || Bounds.Width <= 0) return false;
        long now = Stopwatch.GetTimestamp();
        var (t0, x0) = _moves[0];
        var (t1, x1) = _moves[^1];
        double dt = Stopwatch.GetElapsedTime(t0, t1).TotalSeconds;
        if (dt <= 0 || Stopwatch.GetElapsedTime(t1, now) > FlingWindow) return false;
        double v = (x1 - x0) / dt;
        if (Math.Abs(v) < FlingMinSpeed) return false;
        _flingV = v;
        _flingX = x1;
        _flingOriginX = originX;
        _flingCenterHz = _pressCenterHz;
        _flingLast = now;
        if (_flingTimer is null)
        {
            _flingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _flingTimer.Tick += (_, _) => FlingStep();
        }
        _flingTimer.Start();
        return true;
    }

    private void FlingStep()
    {
        long now = Stopwatch.GetTimestamp();
        double dt = Stopwatch.GetElapsedTime(_flingLast, now).TotalSeconds;
        _flingLast = now;
        _flingX += _flingV * dt;
        _flingV *= Math.Exp(-FlingFriction * dt);
        int bins = _pan?.Length ?? _wfWidth;
        if (Bounds.Width > 0)
        {
            double hz = (_flingOriginX - _flingX) * bins / Bounds.Width * _hzPerPixel;
            PanRequested?.Invoke(_flingCenterHz + (long)Math.Round(hz));
        }
        if (Math.Abs(_flingV) < FlingStopSpeed) StopFling();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopFling();
    }

    // A glide ends by itself, or when the display is pressed again.
    private void StopFling()
    {
        if (_flingTimer is not { IsEnabled: true } t) return;
        t.Stop();
        PanEnded?.Invoke();
    }

    // A second finger turns the press into a pinch: no tune, no pan.
    private void CancelPress()
    {
        if (_pressAt is null) return;
        if (_panning) PanEnded?.Invoke();
        _pressAt = null;
        _panning = false;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer.Type == PointerType.Touch) _touches = Math.Max(0, _touches - 1);
        if (_pressAt is { } at)
        {
            if (_panning && !StartFling(at.X)) PanEnded?.Invoke();
            else TuneRequested?.Invoke(XToHz(at.X, Bounds.Width));
            _pressAt = null;
            _panning = false;
            Cursor = CrossCursor;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private double HzToX(long hz, double width)
    {
        if (_hzPerPixel <= 0 || _wfWidth == 0 && _pan is null) return -1;
        int bins = _pan?.Length ?? _wfWidth;
        double bin = (hz - _centerHz) / _hzPerPixel + bins / 2.0;
        return bin * width / bins;
    }

    /// <summary>Frequency of the strongest signal between loHz and hiHz on the
    /// averaged trace, interpolated between bins; null when nothing stands at
    /// least minAboveFloorDb over the noise floor.</summary>
    public double? PeakHz(long loHz, long hiHz, double minAboveFloorDb)
    {
        if (_pan is not { Length: > 2 } pan || _hzPerPixel <= 0) return null;
        int n = pan.Length;
        int a = (int)Math.Ceiling((loHz - _centerHz) / _hzPerPixel + n / 2.0);
        int b = (int)Math.Floor((hiHz - _centerHz) / _hzPerPixel + n / 2.0);
        a = Math.Clamp(a, 1, n - 2);
        b = Math.Clamp(b, 1, n - 2);
        if (b < a) return null;
        int best = a;
        for (int i = a; i <= b; i++) if (pan[i] > pan[best]) best = i;
        if (pan[best] < _floorDb + 3 + minAboveFloorDb) return null;   // the floor sits 3 dB under the noise
        // Parabola through the peak and its neighbours, in dB.
        double l = pan[best - 1], c = pan[best], r = pan[best + 1];
        double den = l - 2 * c + r;
        double off = den < 0 ? Math.Clamp(0.5 * (l - r) / den, -0.5, 0.5) : 0;
        return _centerHz + (best + off - n / 2.0) * _hzPerPixel;
    }

    private long XToHz(double x, double width)
    {
        int bins = _pan?.Length ?? _wfWidth;
        if (bins == 0) return _centerHz;
        double bin = x * bins / width;
        return _centerHz + (long)Math.Round((bin - bins / 2.0) * _hzPerPixel);
    }

    // The band strip: coloured spans with white edges and a white label in each,
    // the full one if it fits, else the band name, else none.
    private void DrawStrip(DrawingContext ctx, Rect r)
    {
        if (Strip.Count == 0 || _hzPerPixel <= 0) return;
        double h = StripHeight;
        ctx.FillRectangle(StripBase, new Rect(0, r.Top, r.Width, h));
        long viewLo = XToHz(0, r.Width), viewHi = XToHz(r.Width, r.Width);
        foreach (var s in Strip)
        {
            if (s.HighHz <= viewLo || s.LowHz >= viewHi) continue;
            double x0 = HzToX(s.LowHz, r.Width), x1 = HzToX(s.HighHz, r.Width);
            double a = Math.Max(0, x0), b = Math.Min(r.Width, x1);
            if (b - a < 0.5) continue;
            var brush = s.Kind switch
            {
                StripKind.Allowed => StripAllowed,
                StripKind.NotAllowed => StripNotAllowed,
                _ => StripOther,
            };
            ctx.FillRectangle(brush, new Rect(a, r.Top, b - a, h));
            if (x0 >= 0) ctx.DrawLine(StripEdge, new Point(Math.Floor(x0) + 0.5, r.Top), new Point(Math.Floor(x0) + 0.5, r.Top + h));
            if (x1 <= r.Width) ctx.DrawLine(StripEdge, new Point(Math.Floor(x1) + 0.5, r.Top), new Point(Math.Floor(x1) + 0.5, r.Top + h));
            foreach (var text in new[] { s.Label, s.Short })
            {
                var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, TagFace, 11, Brushes.White);
                if (ft.Width + 8 > b - a) continue;
                double tx = Math.Clamp((a + b - ft.Width) / 2, a + 4, b - 4 - ft.Width);
                ctx.DrawText(ft, new Point(tx, r.Top + (h - ft.Height) / 2));
                break;
            }
        }
    }

    private void DrawGrid(DrawingContext ctx, Rect r)
    {
        // Level lines every 10 dB (20 dB on a wide range), labelled at the left.
        var (lo, hi) = TraceRange();
        double range = Math.Max(1, hi - lo);
        double dbStep = range > 80 ? 20 : 10;
        for (double db = Math.Ceiling(lo / dbStep) * dbStep; db < hi; db += dbStep)
        {
            double y = r.Top + (hi - db) / range * r.Height;
            if (y <= r.Top + StripH + 14 || y >= r.Bottom - 1) continue;
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
            var text = new FormattedText((hz / 1e6).ToString("0.000"),
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 11, LabelBrush);
            ctx.DrawText(text, new Point(x + 3, r.Top + StripH + 2));
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
        var (bottom, top) = TraceRange();
        double span = Math.Max(1, top - bottom);
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
        using (ctx.PushClip(dst))
        using (ctx.PushTransform(Matrix.CreateTranslation(0, -dst.Center.Y)
                                 * Matrix.CreateScale(1, -1)
                                 * Matrix.CreateTranslation(0, dst.Center.Y)))
            ctx.DrawImage(_waterfall!, src, dst);
    }

    private void DrawTuning(DrawingContext ctx, Rect b)
    {
        _tagBoxes.Clear();
        if (_hzPerPixel <= 0) return;
        // Inactive slice first, so the active one draws on top.
        foreach (var s in Slices.OrderBy(s => s.Active))
        {
            var color = SliceColors[Math.Clamp(s.Rx, 0, SliceColors.Length - 1)];
            double vx = HzToX(s.Hz, b.Width);
            bool visible = vx >= 0 && vx <= b.Width;
            if (visible)
            {
                double lx = HzToX(s.Hz + s.LowHz, b.Width);
                double hx = HzToX(s.Hz + s.HighHz, b.Width);
                ctx.FillRectangle(s.Active ? PassbandBrush : PassbandInactive,
                    new Rect(Math.Min(lx, hx), 0, Math.Max(1, Math.Abs(hx - lx)), b.Height));
                var pen = new Pen(new SolidColorBrush(color, s.Active ? 1 : 0.7), s.Active ? 1.5 : 1,
                    s.Active ? null : new DashStyle([4, 3], 0));
                ctx.DrawLine(pen, new Point(vx, 0), new Point(vx, b.Height));
            }
            if (Slices.Count > 1) DrawTag(ctx, b, s, color, visible ? vx : -1);
        }
    }

    // A tag at the top of the slice's line: "A" / "B", "TX" on the
    // transmitting slice, filled when active, "M" when muted. Off-screen
    // slices get their tag at the edge with an arrow.
    private void DrawTag(DrawingContext ctx, Rect b, SliceMark s, Color color, double vx)
    {
        string text = s.Tag + (s.Transmit ? " TX" : "") + (s.Muted ? " M" : "");
        bool left = false, right = false;
        if (vx < 0)
        {
            right = s.Hz > _centerHz;
            left = !right;
            text = (left ? "◀ " : "") + text + (right ? " ▶" : "");
        }
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, TagFace, 11, s.Active ? Brushes.White : new SolidColorBrush(color));
        double w = ft.Width + 10, h = 16;
        double x = vx >= 0 ? vx - w / 2 : left ? 2 : b.Width - w - 2;
        x = Math.Clamp(x, 0, Math.Max(0, b.Width - w));
        double y = 16 + StripH;   // just below the frequency labels
        var box = new Rect(x, y, w, h);
        if (s.Active) ctx.FillRectangle(new SolidColorBrush(color), box, 4);
        else ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#CC0B0D11")), new Pen(new SolidColorBrush(color), 1), box, 4, 4);
        if (s.Transmit)
            ctx.FillRectangle(TxBrush, new Rect(box.Right - 3, box.Top + 3, 2, h - 6));
        ctx.DrawText(ft, new Point(x + 5, y + (h - ft.Height) / 2));
        _tagBoxes.Add((box, s.Rx));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var pos = e.GetPosition(this);
        if (point.Properties.IsLeftButtonPressed && OverGrip(pos))
        {
            _dragging = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        var hz = XToHz(pos.X, Bounds.Width);
        if (point.Properties.IsRightButtonPressed)
        {
            MenuRequested?.Invoke(hz);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        // A click on a slice tag selects the slice instead of tuning.
        foreach (var (box, rx) in _tagBoxes)
            if (box.Contains(pos))
            {
                SliceClicked?.Invoke(rx);
                e.Handled = true;
                return;
            }
        if (e.Pointer.Type == PointerType.Touch && ++_touches > 1)
        {
            CancelPress();
            e.Handled = true;
            return;
        }
        StopFling();
        _moves.Clear();
        _pressAt = pos;
        _pressIsTouch = e.Pointer.Type != PointerType.Mouse;
        _pressCenterHz = _centerHz;
        _panning = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        int steps = SliderWheel.Clicks(this, e);
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
