using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.Views;

/// <summary>The About and Network help windows: a title, then headed paragraphs.</summary>
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
            if (head.Length > 0) w.Line(head, 14, FontWeight.SemiBold, 10);
            w.Line(text, 13, FontWeight.Normal, head.Length > 0 ? 0 : 10);
        }
        return w;
    }

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

    /// <summary>What Help > Check for Updates found.</summary>
    public static InfoWindow Update(string current, UpdateChecker.Release? latest)
    {
        if (latest is null)
        {
            var w = Build("Couldn't check for updates", $"You have version {current}.",
                [("", "GitHub didn't answer. Check the internet connection, or look at the Releases page yourself.")]);
            w.Link("Open the Releases page", UpdateChecker.ReleasesPage);
            return w;
        }
        if (!UpdateChecker.IsNewer(latest.Version, current))
            return Build("SimpleHPSDR is up to date", $"Version {current} is the latest.", []);
        var n = Build($"SimpleHPSDR {latest.Version} is available", $"You have version {current}.",
            [("", "The Releases page has the new version and what's changed. Download and install it from there when you're ready; your settings are kept.")]);
        n.Link($"Open the {latest.Version} release page", latest.Page);
        return n;
    }

    private void Link(string text, string url) => Body.Children.Add(new HyperlinkButton
    {
        Content = text, NavigateUri = new Uri(url), Padding = new Thickness(0), Margin = new Thickness(0, 8, 0, 0),
    });

    public static InfoWindow About(string version, string engine) => Build("SimpleHPSDR", $"Version {version}",
    [
        ("About", "A small, clean interface for OpenHPSDR radios. By Chris, KC2RGW."),
        ("License", "GNU General Public License, version 3 or later. It comes with no warranty."),
        ("Station guide", "The SWL guide uses EiBi's shortwave schedule by Eike Bierwirth (eibispace.de). Its code tables come from Dream and DecDRM."),
        ("Radio engine", $"SimpleHPSDR stands on Station Engine {engine}, the radio and DSP engine from the Zeus SDR project. Its principal developers are Douglas J. Cerrato, KB2UKA, and Christian Suarez, N9WAR, with thanks to them and its contributors. GPL-2.0-or-later. SimpleHPSDR is an independent project, not affiliated with or endorsed by Zeus SDR."),
        ("With thanks to", "WDSP by Warren Pratt, NR0V. The firmware loader follows TAPR's HPSDRProgrammer and deskHPSDR by Heiko Amft, DL1BZ. The DeepCW CW decoder model is by e04 (MIT License), run with Microsoft's ONNX Runtime. DRM decoding by DecDRM (GPL-2.0-or-later) with FDK-AAC, Opus and libxaac, through SimpleDream's bridge. Interface built with Avalonia."),
    ]);
}
