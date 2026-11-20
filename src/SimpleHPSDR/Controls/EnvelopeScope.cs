using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.Controls;

/// <summary>
/// The signal's envelope as a modulation monitor shows it: mirrored about the centre line,
/// newest on the right. Each column is filled out to the highest level it held and, brighter,
/// to the lowest, so flat tops show as a level edge and AM's downward peaks as the bright
/// band pinching towards the centre. Lines mark the peak of the last few seconds and, in AM,
/// the carrier and 100% modulation.
/// </summary>
public sealed class EnvelopeScope : Control
{
    public static readonly StyledProperty<SignalReport?> ReportProperty =
        AvaloniaProperty.Register<EnvelopeScope, SignalReport?>(nameof(Report));

    public SignalReport? Report { get => GetValue(ReportProperty); set => SetValue(ReportProperty, value); }

    static EnvelopeScope() => AffectsRender<EnvelopeScope>(ReportProperty);

    private static readonly IBrush Outer = new SolidColorBrush(Color.Parse("#555AA9FF"));
    private static readonly IBrush Inner = new SolidColorBrush(Color.Parse("#5AA9FF"));
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#8A93A3"));
    private static readonly IBrush PeakText = new SolidColorBrush(Color.Parse("#F5D76E"));
    private static readonly IBrush CarrierText = new SolidColorBrush(Color.Parse("#3DD68C"));
    private static readonly IBrush LimitText = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IPen Grid = new Pen(new SolidColorBrush(Color.Parse("#2A2F38")), 1);
    private static readonly IPen PeakLine = new Pen(PeakText, 1, new DashStyle([4, 4], 0));
    private static readonly IPen CarrierLine = new Pen(CarrierText, 1, new DashStyle([4, 4], 0));
    private static readonly IPen LimitLine = new Pen(LimitText, 1, new DashStyle([2, 4], 0));
    private static readonly Typeface Face = new("Inter");

    private double _top = double.NaN;

    private static FormattedText Text(string s, double size, IBrush brush) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, brush);

    public override void Render(DrawingContext g)
    {
        var b = Bounds;
        g.FillRectangle(SpectrumLook.Current.Background, new Rect(b.Size));
        double mid = b.Height / 2;
        g.DrawLine(Grid, new Point(0, mid), new Point(b.Width, mid));
        g.DrawText(Text("Envelope", 11, Label), new Point(6, 4));
        if (Report is not { } r || r.EnvMax.Length < 2) return;

        // The scale follows the signal, eased so it doesn't pump with every syllable.
        double want = Math.Max(r.Peak, r.Carrier * 2.1);
        if (want <= 0) foreach (float v in r.EnvMax) want = Math.Max(want, v);
        want = Math.Max(want * 1.12, 1e-9);
        _top = double.IsNaN(_top) || want > _top * 4 || want < _top / 4 ? want : _top + (want - _top) * 0.15;
        double half = Math.Max(10, mid - 6);
        double Y(double v) => Math.Min(v / _top, 1.05) * half;

        var time = Text(r.ScopeSeconds >= 1 ? $"{r.ScopeSeconds:0} s" : $"{r.ScopeSeconds * 1000:0} ms", 11, Label);
        g.DrawText(time, new Point(b.Width - time.Width - 6, b.Height - time.Height - 4));

        int n = r.EnvMax.Length;
        void Band(float[] level, IBrush brush)
        {
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(new Point(0, mid - Y(level[0])), true);
                for (int i = 1; i < n; i++) c.LineTo(new Point(i * b.Width / (n - 1), mid - Y(level[i])));
                for (int i = n - 1; i >= 0; i--) c.LineTo(new Point(i * b.Width / (n - 1), mid + Y(level[i])));
                c.EndFigure(true);
            }
            g.DrawGeometry(brush, null, geo);
        }
        Band(r.EnvMax, Outer);
        Band(r.EnvMin, Inner);

        void Mark(double level, IPen pen, IBrush brush, string label)
        {
            if (level <= 0 || level > _top * 1.05) return;
            double y = Y(level);
            g.DrawLine(pen, new Point(0, mid - y), new Point(b.Width, mid - y));
            g.DrawLine(pen, new Point(0, mid + y), new Point(b.Width, mid + y));
            var t = Text(label, 10, brush);
            g.DrawText(t, new Point(b.Width - t.Width - 6, mid - y - t.Height - 1));
        }
        if (r.Carrier > 0)
        {
            Mark(r.Carrier, CarrierLine, CarrierText, "carrier");
            Mark(r.Carrier * 2, LimitLine, LimitText, "100%");
        }
        Mark(r.Peak, PeakLine, PeakText, "peak");
    }
}
