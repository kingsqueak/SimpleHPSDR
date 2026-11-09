using Avalonia;
using Avalonia.Media;

namespace SimpleHPSDR;

/// <summary>A colour in a Config list: what it's called and its value.</summary>
public sealed record NamedColor(string Name, Color Color)
{
    public IBrush Swatch { get; } = new SolidColorBrush(Color);
}

/// <summary>A waterfall palette in Config: its name, preview strip and colours.</summary>
public sealed record WaterfallPalette(string Name, (double T, byte R, byte G, byte B)[] Stops)
{
    public IBrush Preview { get; } = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = [.. Stops.Select(s => new GradientStop(Color.FromRgb(s.R, s.G, s.B), s.T))],
    };

    public uint[] Build()
    {
        var lut = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            int k = 0;
            while (k < Stops.Length - 2 && t > Stops[k + 1].T) k++;
            var (t0, r0, g0, b0) = Stops[k];
            var (t1, r1, g1, b1) = Stops[k + 1];
            double f = t1 > t0 ? Math.Clamp((t - t0) / (t1 - t0), 0, 1) : 0;
            byte R = (byte)(r0 + (r1 - r0) * f), G = (byte)(g0 + (g1 - g0) * f), B = (byte)(b0 + (b1 - b0) * f);
            lut[i] = 0xFF000000u | (uint)(R << 16) | (uint)(G << 8) | B;
        }
        return lut;
    }
}

/// <summary>
/// The colours of the spectrum and waterfall, chosen in Config > Colors. The
/// spectrum, the filter close-up and the waterfall read <see cref="Current"/>
/// each time they draw, so a change shows at once.
/// </summary>
public sealed class SpectrumLook
{
    public static readonly NamedColor[] LineColors =
    [
        new("Blue", Color.Parse("#7FB2FF")), new("Cyan", Color.Parse("#5EE3F0")),
        new("Green", Color.Parse("#6BE38A")), new("Yellow", Color.Parse("#F5D547")),
        new("Orange", Color.Parse("#FF9F43")), new("Red", Color.Parse("#FF6B6B")),
        new("Pink", Color.Parse("#FF8AD8")), new("Purple", Color.Parse("#B49BFF")),
        new("White", Color.Parse("#F2F4F7")),
    ];

    /// <summary>"None" leaves the area under the line empty.</summary>
    public static readonly NamedColor[] FillColors =
    [
        new("None", Colors.Transparent), .. LineColors,
    ];

    public static readonly NamedColor[] BackgroundColors =
    [
        new("Black", Color.Parse("#0B0D11")), new("Charcoal", Color.Parse("#1C1F26")),
        new("Navy", Color.Parse("#0A1530")), new("Dark blue", Color.Parse("#0F2A52")),
        new("Dark green", Color.Parse("#0B2416")), new("Dark purple", Color.Parse("#1F1033")),
        new("Dark red", Color.Parse("#2A0D0D")), new("Grey", Color.Parse("#3A3F4A")),
    ];

    /// <summary>The colour the background fades to at the bottom; "None" is a plain background.</summary>
    public static readonly NamedColor[] FadeColors =
    [
        new("None", Colors.Transparent), .. BackgroundColors,
    ];

    public static readonly WaterfallPalette[] Palettes =
    [
        new("Classic", [(0.00, 0x0B, 0x0D, 0x11), (0.20, 0x0A, 0x1E, 0x5A), (0.40, 0x10, 0x6B, 0xD8),
            (0.60, 0x2E, 0xD3, 0xC8), (0.75, 0xF5, 0xD0, 0x3B), (0.90, 0xF2, 0x5F, 0x2C), (1.00, 0xFF, 0xFF, 0xFF)]),
        new("Grey", [(0.00, 0x00, 0x00, 0x00), (1.00, 0xFF, 0xFF, 0xFF)]),
        new("Fire", [(0.00, 0x00, 0x00, 0x00), (0.35, 0x80, 0x00, 0x00), (0.60, 0xE0, 0x40, 0x00),
            (0.80, 0xFF, 0xC0, 0x00), (1.00, 0xFF, 0xFF, 0xE0)]),
        new("Ocean", [(0.00, 0x00, 0x05, 0x10), (0.35, 0x00, 0x30, 0x70), (0.65, 0x10, 0x90, 0xD0),
            (0.85, 0x80, 0xE0, 0xFF), (1.00, 0xFF, 0xFF, 0xFF)]),
        new("Green screen", [(0.00, 0x00, 0x05, 0x00), (0.50, 0x00, 0x80, 0x20), (0.85, 0x40, 0xFF, 0x60),
            (1.00, 0xD0, 0xFF, 0xD0)]),
        new("Rainbow", [(0.00, 0x00, 0x00, 0x20), (0.20, 0x00, 0x00, 0xFF), (0.40, 0x00, 0xFF, 0xFF),
            (0.55, 0x00, 0xFF, 0x00), (0.70, 0xFF, 0xFF, 0x00), (0.85, 0xFF, 0x80, 0x00), (1.00, 0xFF, 0x00, 0x00)]),
        new("Viridis", [(0.00, 0x44, 0x01, 0x54), (0.25, 0x3B, 0x52, 0x8B), (0.50, 0x21, 0x91, 0x8C),
            (0.75, 0x5E, 0xC9, 0x62), (1.00, 0xFD, 0xE7, 0x25)]),
    ];

    public static SpectrumLook Current { get; private set; } = new(null);

    public IPen LinePen { get; }
    public IBrush Fill { get; }
    public IBrush Background { get; }
    public uint[] Palette { get; }

    private SpectrumLook(Settings? s)
    {
        var line = Find(LineColors, s?.SpecLineColor, "Blue").Color;
        var fill = Find(FillColors, s?.SpecFillColor, "Blue").Color;
        var back = Find(BackgroundColors, s?.SpecBackground, "Black").Color;
        var fade = Find(FadeColors, s?.SpecBackgroundFade, "None").Color;
        LinePen = new Pen(new SolidColorBrush(line), 1.2);
        Fill = new SolidColorBrush(fill, fill.A == 0 ? 0 : 0.22);
        Background = fade.A == 0
            ? new SolidColorBrush(back)
            : new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = [new GradientStop(back, 0), new GradientStop(fade, 1)],
            };
        Palette = Array.Find(Palettes, p => p.Name == s?.WaterfallPalette)?.Build() ?? Palettes[0].Build();
    }

    private static NamedColor Find(NamedColor[] list, string? name, string fallback) =>
        Array.Find(list, c => c.Name == name) ?? Array.Find(list, c => c.Name == fallback)!;

    /// <summary>Take the colours from the settings.</summary>
    public static void Apply(Settings s) => Current = new SpectrumLook(s);
}
