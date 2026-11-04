using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SimpleHPSDR.Views;

/// <summary>The Help and About windows: a title, then headed paragraphs.</summary>
public partial class InfoWindow : Window
{
    public InfoWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
    }

    private void Line(string text, double size, FontWeight weight, double top, bool muted = false)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, top, 0, 0), VerticalAlignment = VerticalAlignment.Top,
        };
        if (muted) t.Classes.Add("label");
        if (muted) t.FontSize = size;
        Body.Children.Add(t);
    }

    private static InfoWindow Build(string title, string subtitle, (string Head, string Text)[] sections)
    {
        var w = new InfoWindow { Title = title };
        w.Line(title, 20, FontWeight.SemiBold, 0);
        w.Line(subtitle, 12, FontWeight.Normal, 0, muted: true);
        foreach (var (head, text) in sections)
        {
            w.Line(head, 14, FontWeight.SemiBold, 10);
            w.Line(text, 13, FontWeight.Normal, 0);
        }
        return w;
    }

    public static InfoWindow Help() => Build("SimpleHPSDR Help", "Most controls have more behind a right-click.",
    [
        ("Tuning", "Click the panadapter to tune, or use the wheel; a sideways scroller works too. Drag the panadapter to move the band under the display. Click the frequency to type one. The highlighted digit is the one your tuning step moves; use Step beside it to change it."),
        ("Right-click for more", "Panadapter: slices and notches. Band: save and recall frequencies. Mode: AM and FM options. Filter buttons: edit a filter. NB and NR: their settings. Zoom: span. SQL: level. PS: two-tone, calibration, saved correction. Mon: level. Signal meter: S-meter trim."),
        ("Slices", "Right-click the panadapter to add slice B, on the same panadapter or in its own. Click a slice's tag to make it active. The AF slider has an A and a B flag for each slice's level, on top of the blue main AF."),
        ("Filters", "Drag the edges in the close-up at the top right to set the cuts. Config > Filters holds the filter shapes; TX filter follows the RX filter is in Config > TX Audio."),
        ("Monitor", "Mon steps through Mon (your mic through the TX chain, without transmitting), TX Mon (while transmitting), Mon+TX and off. It starts at a low level so it won't feed back through speakers."),
        ("PureSignal", "Into a dummy load or a matched antenna, right-click PS and press PS CAL. It keys the radio in short two-tone bursts until PureSignal calibrates."),
        ("Radio model", "Several radios report the same ID, so choose yours in the radio picker or Config > Radio. It sets the S-meter, PA and PureSignal defaults."),
        ("Firmware", "Click the radio's name, then Firmware. A failed flash can leave the radio needing recovery, so use only the file made for your exact radio."),
    ]);

    public static InfoWindow About(string version, string engine) => Build("SimpleHPSDR", $"Version {version}",
    [
        ("About", "A small, clean interface for OpenHPSDR radios. By Chris, KC2RGW."),
        ("License", "GNU General Public License, version 3 or later. It comes with no warranty."),
        ("Radio engine", $"SimpleHPSDR stands on Station Engine {engine}, the radio and DSP engine from the Zeus SDR project. Its principal developers are Douglas J. Cerrato, KB2UKA, and Christian Suarez, N9WAR, with thanks to them and its contributors. GPL-2.0-or-later. SimpleHPSDR is an independent project, not affiliated with or endorsed by Zeus SDR."),
        ("With thanks to", "WDSP by Warren Pratt, NR0V. The firmware loader follows TAPR's HPSDRProgrammer and deskHPSDR by Heiko Amft, DL1BZ. Interface built with Avalonia."),
    ]);
}
