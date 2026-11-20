using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.Controls;

/// <summary>
/// The spectrum round the carrier for the signal analyser: the average while the signal
/// is there, its peak hold, the channel the signal belongs in shaded, and the dial marked.
/// What lies outside the shading is what the splatter reading measures.
/// </summary>
public sealed class SignalSpectrum : Control
{
    public static readonly StyledProperty<SignalReport?> ReportProperty =
        AvaloniaProperty.Register<SignalSpectrum, SignalReport?>(nameof(Report));

    public SignalReport? Report { get => GetValue(ReportProperty); set => SetValue(ReportProperty, value); }

    static SignalSpectrum() => AffectsRender<SignalSpectrum>(ReportProperty);

    private const double Axis = 20, LeftScale = 40;
    private static readonly IBrush ChannelShade = new SolidColorBrush(Color.Parse("#1F3DD68C"));
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#8A93A3"));
    private static readonly IBrush Readout = new SolidColorBrush(Color.Parse("#E6E8EB"));
    private static readonly IBrush ReadoutBack = new SolidColorBrush(Color.Parse("#C0101318"));
    private static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#305AA9FF"));
    private static readonly IPen Grid = new Pen(new SolidColorBrush(Color.Parse("#2A2F38")), 1);
    private static readonly IPen Dial = new Pen(new SolidColorBrush(Color.Parse("#E5484D")), 1);
    private static readonly IPen Hold = new Pen(new SolidColorBrush(Color.Parse("#808A93A3")), 1);
    private static readonly IPen CursorLine = new Pen(new SolidColorBrush(Color.Parse("#66FFFFFF")), 1);
    private static readonly Typeface Face = new("Inter");

    private double _floor = double.NaN, _top = double.NaN;
    private float[]? _sort;
    private Point? _pointer;

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

    private static FormattedText Text(string s, double size, IBrush brush) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, brush);

    /// <summary>What to show: the channel with room either side for what shouldn't be there.</summary>
    private static (double Low, double High) View(SignalReport r)
    {
        const double Beside = 4500, Limit = 10000;
        return (Math.Max(-Limit, r.ChannelLowHz - Beside), Math.Min(Limit, r.ChannelHighHz + Beside));
    }

    private void Rescale(SignalReport r, int i0, int i1)
    {
        int n = i1 - i0 + 1;
        if (n < 8) return;
        if (_sort is null || _sort.Length != n) _sort = new float[n];
        Array.Copy(r.SpectrumDb, i0, _sort, 0, n);
        Array.Sort(_sort);
        float peak = float.MinValue;
        for (int i = i0; i <= i1; i++) if (r.SpectrumPeakDb[i] > peak) peak = r.SpectrumPeakDb[i];
        if (peak < -190) return;                    // nothing yet
        double floor = Math.Max(_sort[n / 10] - 8, peak - 110), top = peak + 6;
        if (top - floor < 60) floor = top - 60;
        bool first = double.IsNaN(_floor);
        _floor = first ? floor : _floor + (floor - _floor) * 0.1;
        _top = first ? top : _top + (top - _top) * 0.1;
    }

    public override void Render(DrawingContext g)
    {
        var b = Bounds;
        g.FillRectangle(SpectrumLook.Current.Background, new Rect(b.Size));
        g.DrawText(Text("Spectrum", 11, Label), new Point(LeftScale + 6, 4));
        if (Report is not { } r || r.SpectrumDb.Length < 16 || r.BinHz <= 0) return;

        var (lo, hi) = View(r);
        int Index(double hz) => Math.Clamp((int)Math.Round((hz - r.SpectrumLowHz) / r.BinHz), 0, r.SpectrumDb.Length - 1);
        int i0 = Index(lo), i1 = Index(hi);
        Rescale(r, i0, i1);
        if (double.IsNaN(_floor)) return;

        double w = Math.Max(10, b.Width - LeftScale), h = Math.Max(20, b.Height - Axis);
        double X(double hz) => LeftScale + (hz - lo) / (hi - lo) * w;
        double floor = _floor, top = _top;
        double Y(double db) => h - Math.Clamp((db - floor) / Math.Max(1, top - floor), 0, 1) * h;

        // Where the signal belongs.
        g.FillRectangle(ChannelShade, new Rect(X(r.ChannelLowHz), 0, Math.Max(2, X(r.ChannelHighHz) - X(r.ChannelLowHz)), h));

        for (double db = Math.Ceiling(floor / 10) * 10; db <= top; db += 10)
        {
            double y = Y(db);
            g.DrawLine(Grid, new Point(LeftScale, y), new Point(b.Width, y));
            var t = Text(db.ToString("0", CultureInfo.CurrentCulture), 10, Label);
            g.DrawText(t, new Point(LeftScale - t.Width - 4, y - t.Height / 2));
        }
        double stepKhz = w < 500 ? 2 : 1;
        for (double khz = Math.Ceiling(lo / 1000 / stepKhz) * stepKhz; khz * 1000 <= hi; khz += stepKhz)
        {
            double x = X(khz * 1000);
            g.DrawLine(Grid, new Point(x, 0), new Point(x, h));
            var t = Text(khz == 0 ? "dial" : khz.ToString("+0;−0", CultureInfo.CurrentCulture), 10, Label);
            g.DrawText(t, new Point(Math.Clamp(x - t.Width / 2, LeftScale, b.Width - t.Width), h + 3));
        }
        var unit = Text("kHz", 10, Label);
        g.DrawText(unit, new Point(2, h + 3));

        StreamGeometry Trace(float[] data, bool closed)
        {
            var geo = new StreamGeometry();
            using var c = geo.Open();
            if (closed) c.BeginFigure(new Point(LeftScale, h), true);
            for (int i = i0; i <= i1; i++)
            {
                var p = new Point(X(r.SpectrumLowHz + i * r.BinHz), Y(data[i]));
                if (i == i0 && !closed) c.BeginFigure(p, false); else c.LineTo(p);
            }
            if (closed) c.LineTo(new Point(b.Width, h));
            c.EndFigure(closed);
            return geo;
        }
        g.DrawGeometry(null, Hold, Trace(r.SpectrumPeakDb, false));
        g.DrawGeometry(Fill, null, Trace(r.SpectrumDb, true));
        g.DrawGeometry(null, SpectrumLook.Current.LinePen, Trace(r.SpectrumDb, false));

        g.DrawLine(Dial, new Point(X(0), 0), new Point(X(0), h));

        if (_pointer is { } at && at.X >= LeftScale && at.Y <= h)
        {
            double hz = lo + (at.X - LeftScale) / w * (hi - lo);
            g.DrawLine(CursorLine, new Point(at.X, 0), new Point(at.X, h));
            var t = Text($"{hz:+0;−0} Hz   {r.SpectrumDb[Index(hz)]:0} dBFS", 12, Readout);
            var box = new Rect(Math.Clamp(at.X + 8, LeftScale, Math.Max(LeftScale, b.Width - t.Width - 8)), 6, t.Width + 8, t.Height + 4);
            g.FillRectangle(ReadoutBack, box, 3);
            g.DrawText(t, new Point(box.X + 4, box.Y + 2));
        }
    }
}
