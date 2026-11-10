using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.Controls;

/// <summary>
/// SWR across a sweep, on the panadapter's dark background: grid at SWR 1.5,
/// 2, 3 and 5, the amateur band shaded, the lowest point marked, and the
/// frequency, SWR (and R+jX when the source measures it) under the pointer.
/// </summary>
public sealed class AnalyzerPlot : Control
{
    public static readonly StyledProperty<IReadOnlyList<StationClient.VnaPoint>?> PointsProperty =
        AvaloniaProperty.Register<AnalyzerPlot, IReadOnlyList<StationClient.VnaPoint>?>(nameof(Points));
    public static readonly StyledProperty<long> BandLowHzProperty =
        AvaloniaProperty.Register<AnalyzerPlot, long>(nameof(BandLowHz));
    public static readonly StyledProperty<long> BandHighHzProperty =
        AvaloniaProperty.Register<AnalyzerPlot, long>(nameof(BandHighHz));

    static AnalyzerPlot() =>
        AffectsRender<AnalyzerPlot>(PointsProperty, BandLowHzProperty, BandHighHzProperty);

    public IReadOnlyList<StationClient.VnaPoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public long BandLowHz { get => GetValue(BandLowHzProperty); set => SetValue(BandLowHzProperty, value); }
    public long BandHighHz { get => GetValue(BandHighHzProperty); set => SetValue(BandHighHzProperty, value); }

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#0B0D10"));
    private static readonly IBrush BandShade = new SolidColorBrush(Color.Parse("#1A3DD68C"));
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#8A93A3"));
    private static readonly IBrush Readout = new SolidColorBrush(Color.Parse("#E6E8EB"));
    private static readonly IPen Grid = new Pen(new SolidColorBrush(Color.Parse("#2A2F38")), 1);
    private static readonly IPen GoodLine = new Pen(new SolidColorBrush(Color.Parse("#3DD68C")), 1, new DashStyle([4, 4], 0));
    private static readonly IPen Curve = new Pen(new SolidColorBrush(Color.Parse("#5AA9FF")), 2);
    private static readonly IPen Marker = new Pen(new SolidColorBrush(Color.Parse("#F5D76E")), 1.5);
    private static readonly IPen CursorLine = new Pen(new SolidColorBrush(Color.Parse("#66FFFFFF")), 1);
    private static readonly Typeface Face = new("Inter");

    private Point? _pointer;
    private const double Left = 44, Right = 12, Top = 12, Bottom = 26;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointer = e.GetPosition(this);
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

    public override void Render(DrawingContext g)
    {
        var b = Bounds;
        g.FillRectangle(Background, new Rect(b.Size), 6);
        var plot = new Rect(Left, Top, Math.Max(10, b.Width - Left - Right), Math.Max(10, b.Height - Top - Bottom));
        var pts = Points?.Where(p => p.Swr is not null).ToList();
        if (pts is null || pts.Count < 2)
        {
            var t = Text("Run a sweep to see SWR here.", 13, Label);
            g.DrawText(t, new Point((b.Width - t.Width) / 2, (b.Height - t.Height) / 2));
            return;
        }

        long f0 = pts[0].FrequencyHz, f1 = pts[^1].FrequencyHz;
        double maxSwr = Math.Clamp(Math.Ceiling(pts.Max(p => p.Swr!.Value)), 3, 10);
        // SWR on a log scale reads better: 1 to 2 gets as much room as it deserves.
        double Y(double swr) => plot.Bottom - Math.Log(Math.Clamp(swr, 1, maxSwr)) / Math.Log(maxSwr) * plot.Height;
        double X(long hz) => plot.Left + (hz - f0) / (double)Math.Max(1, f1 - f0) * plot.Width;

        // The band, if it's in view.
        if (BandHighHz > BandLowHz && BandHighHz > f0 && BandLowHz < f1)
        {
            double x0 = X(Math.Max(BandLowHz, f0)), x1 = X(Math.Min(BandHighHz, f1));
            g.FillRectangle(BandShade, new Rect(x0, plot.Top, Math.Max(1, x1 - x0), plot.Height));
        }

        foreach (double swr in new[] { 1.5, 2, 3, 5, 10 }.Where(v => v <= maxSwr))
        {
            double y = Y(swr);
            g.DrawLine(swr == 2 ? GoodLine : Grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var t = Text(swr.ToString("0.#", CultureInfo.CurrentCulture), 11, Label);
            g.DrawText(t, new Point(plot.Left - t.Width - 6, y - t.Height / 2));
        }
        var one = Text("1.0", 11, Label);
        g.DrawText(one, new Point(plot.Left - one.Width - 6, plot.Bottom - one.Height / 2));

        // Frequency labels: about five across.
        for (int i = 0; i <= 4; i++)
        {
            long hz = f0 + (f1 - f0) * i / 4;
            double x = X(hz);
            g.DrawLine(Grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var t = Text((hz / 1e6).ToString("0.000", CultureInfo.CurrentCulture), 11, Label);
            g.DrawText(t, new Point(Math.Clamp(x - t.Width / 2, 0, b.Width - t.Width), plot.Bottom + 6));
        }

        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(new Point(X(pts[0].FrequencyHz), Y(pts[0].Swr!.Value)), false);
            foreach (var p in pts.Skip(1)) c.LineTo(new Point(X(p.FrequencyHz), Y(p.Swr!.Value)));
            c.EndFigure(false);
        }
        g.DrawGeometry(null, Curve, geo);

        var best = pts.MinBy(p => p.Swr!.Value)!;
        double bx = X(best.FrequencyHz), by = Y(best.Swr!.Value);
        g.DrawLine(Marker, new Point(bx, plot.Top), new Point(bx, plot.Bottom));
        g.DrawEllipse(null, Marker, new Point(bx, by), 4, 4);

        if (_pointer is { } at && plot.Contains(at))
        {
            var near = pts.MinBy(p => Math.Abs(X(p.FrequencyHz) - at.X))!;
            double nx = X(near.FrequencyHz);
            g.DrawLine(CursorLine, new Point(nx, plot.Top), new Point(nx, plot.Bottom));
            string s = $"{near.FrequencyHz / 1e6:0.0000} MHz   SWR {near.Swr:0.00}";
            if (near.ResistanceOhms is { } r && near.ReactanceOhms is { } x)
                s += $"   {r:0} {(x < 0 ? "-" : "+")} j{Math.Abs(x):0} Ω";
            var t = Text(s, 12, Readout);
            g.DrawText(t, new Point(Math.Clamp(nx + 8, plot.Left, plot.Right - t.Width), plot.Top + 4));
        }
    }
}
