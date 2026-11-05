using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SimpleHPSDR.Engine;

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
        ("Right-click for more", "Panadapter: slices. ANF: manual notches. Band: save and recall frequencies. Mode: AM and FM options. Filter buttons: edit a filter. NB and NR: their settings. Zoom: span. SQL: level. PS: two-tone, calibration, saved correction. Mon: level. Signal meter: S-meter trim."),
        ("Slices", "Right-click the panadapter to add slice B, on the same panadapter or in its own. Click a slice's tag to make it active. The AF slider has an A and a B flag for each slice's level, on top of the blue main AF."),
        ("Filters", "Drag the edges in the close-up at the top right to set the cuts. In AM, SAM, DSB and FM both sides move together. Config > Filters holds the filter shapes; TX filter follows the RX filter is in Config > TX Audio."),
        ("Notches", "ANF finds and removes steady tones by itself. Right-click it for manual notches: press Add on panadapter, then click the signal to notch. Each notch has its own width and Remove button."),
        ("Monitor", "Mon steps through Mon (your mic through the TX chain, without transmitting), TX Mon (while transmitting), Mon+TX and off. It starts at a low level so it won't feed back through speakers."),
        ("PureSignal", "1. Set the source in Config > PureSignal.\n2. Set the drive level you'll be using.\n3. Turn on PS.\n4. Switch to a dummy load, right-click PS and press PS CAL. It keys the radio in short two-tone bursts. Run it again until PS turns green.\n\nThat's it until you change the drive level.\n\nThe PS button is green when on and locked, yellow when on but not locked, and grey when off."),
        ("Network", "The radio uses UDP, on radio ports 1024 to 1044. If no radio appears, the radio picker's Network help button shows what a firewall must allow on this computer."),
        ("ID timer", "Click ID 00:00 above the clock to turn on a reminder on the even ten minutes, while connected to a radio. Config > Station chooses beeps or a .wav of your own. If you're transmitting at the mark, it plays when you unkey. It plays on this computer's speakers, never over the air."),
        ("Radio model", "Several radios report the same ID, so choose yours in the radio picker or Config > Radio. It sets the S-meter, PA and PureSignal defaults."),
        ("Firmware", "Click the radio's name, then Firmware. A failed flash can leave the radio needing recovery, so use only the file made for your exact radio."),
    ]);

    /// <summary>What a firewall has to let through, for this computer.</summary>
    public static InfoWindow Network()
    {
        var engine = EngineHost.FindEngine() ?? "StationEngine";
        var w = Build("Network help", "If no radio appears, a firewall may be dropping its packets.",
        [
            ("What the radio needs", "Protocol: UDP only.\nRadio ports: 1024 to 1044, in both directions.\nThis computer: port 1025 with Protocol 2, a random port with Protocol 1, so allow the program rather than one port.\nProgram: StationEngine. Allow it on the network the radio is on."),
        ]);
        if (OperatingSystem.IsWindows())
        {
            w.Line("Windows", 14, FontWeight.SemiBold, 10);
            w.Line("Run this in a terminal opened as administrator:", 13, FontWeight.Normal, 0);
            w.Code($"netsh advfirewall firewall add rule name=\"SimpleHPSDR\" dir=in action=allow protocol=UDP remoteport=1024-1044 program=\"{engine}\" enable=yes");
            w.Line("If Zeus is on this computer, use this command and answer No when Windows asks to let StationEngine make changes. Answering Yes moves Zeus's own firewall rule to SimpleHPSDR, and Zeus may then stop finding the radio.", 13, FontWeight.Normal, 6);
        }
        else if (OperatingSystem.IsMacOS())
        {
            w.Line("macOS", 14, FontWeight.SemiBold, 10);
            w.Line("Turn on SimpleHPSDR in System Settings > Privacy & Security > Local Network. If the firewall is on, allow this program under Network > Firewall > Options:", 13, FontWeight.Normal, 0);
            w.Code(engine);
        }
        else
        {
            w.Line("Linux", 14, FontWeight.SemiBold, 10);
            w.Line("Linux firewalls usually can't allow by program, so allow the radio's address. With ufw, using your radio's address:", 13, FontWeight.Normal, 0);
            w.Code("sudo ufw allow proto udp from 192.168.1.20");
        }
        w.Line("TCI and CAT", 14, FontWeight.SemiBold, 10);
        w.Line("These are TCP, on the ports set in Config > CAT & TCI, and only need opening if another computer uses them.", 13, FontWeight.Normal, 0);
        return w;
    }

    /// <summary>A line to copy: selectable, in a fixed-width face.</summary>
    private void Code(string text) => Body.Children.Add(new SelectableTextBlock
    {
        Text = text, FontSize = 12, FontFamily = new FontFamily("Menlo, Consolas, monospace"),
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
    });

    public static InfoWindow About(string version, string engine) => Build("SimpleHPSDR", $"Version {version}",
    [
        ("About", "A small, clean interface for OpenHPSDR radios. By Chris, KC2RGW."),
        ("License", "GNU General Public License, version 3 or later. It comes with no warranty."),
        ("Radio engine", $"SimpleHPSDR stands on Station Engine {engine}, the radio and DSP engine from the Zeus SDR project. Its principal developers are Douglas J. Cerrato, KB2UKA, and Christian Suarez, N9WAR, with thanks to them and its contributors. GPL-2.0-or-later. SimpleHPSDR is an independent project, not affiliated with or endorsed by Zeus SDR."),
        ("With thanks to", "WDSP by Warren Pratt, NR0V. The firmware loader follows TAPR's HPSDRProgrammer and deskHPSDR by Heiko Amft, DL1BZ. Interface built with Avalonia."),
    ]);
}
