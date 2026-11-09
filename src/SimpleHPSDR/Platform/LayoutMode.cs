using Avalonia.Controls;
using Avalonia.Threading;

namespace SimpleHPSDR.Platform;

public enum UiLayout { Desktop, Touch }

/// <summary>
/// Which main-window layout to use: the desktop one, or the large-target
/// touch one for small radio screens. Automatic picks touch when the screen's
/// work area is narrower than 1080 (the screen, not the window: narrowing a
/// window on a big monitor never swaps the layout). The setting can force
/// either, and --touch / --desktop (or SIMPLEHPSDR_LAYOUT) override it for
/// one run. The main window shows the layout this decides.
/// </summary>
public static class LayoutMode
{
    public const double TouchBelowWidth = 1080;
    public const string Automatic = "Automatic", Desktop = "Desktop", Touch = "Touch";
    public static readonly string[] Names = [Automatic, Desktop, Touch];

    /// <summary>Desktop or Touch from the command line or environment, for this run.</summary>
    public static string? Override { get; private set; }

    /// <summary>What was chosen, and the screen width it was chosen on.</summary>
    public static UiLayout Current { get; private set; } = UiLayout.Desktop;
    public static double ScreenWidth { get; private set; } = double.NaN;

    /// <summary>Raised when the layout to use changes.</summary>
    public static event Action<UiLayout>? Changed;

    /// <summary>Raised when the setting changes in Config, so the main window checks again.</summary>
    public static event Action? SettingChanged;

    public static void ReadCommandLine(string[] args)
    {
        foreach (var a in args)
            if (a is "--touch") Override = Touch;
            else if (a is "--desktop") Override = Desktop;
        if (Override is null && Environment.GetEnvironmentVariable("SIMPLEHPSDR_LAYOUT") is { } env)
            Override = Names.FirstOrDefault(n => n != Automatic && n.Equals(env, StringComparison.OrdinalIgnoreCase));
    }

    public static string Choice(Settings s) =>
        Override ?? (Names.Contains(s.Layout) ? s.Layout : Automatic);

    public static UiLayout Resolve(string choice, double screenWidth) => choice switch
    {
        Touch => UiLayout.Touch,
        Desktop => UiLayout.Desktop,
        _ => screenWidth < TouchBelowWidth ? UiLayout.Touch : UiLayout.Desktop,
    };

    public static void NotifySettingChanged() => SettingChanged?.Invoke();

    /// <summary>One line for Config: what's in use and why.</summary>
    public static string Describe(Settings s)
    {
        string now = Current == UiLayout.Touch ? "touch" : "desktop";
        if (Override is { } o) return $"The {o.ToLowerInvariant()} layout was chosen when the app was started.";
        return Choice(s) == Automatic ? $"Using the {now} layout on this screen." : "";
    }

    /// <summary>
    /// Follows the main window: decides when it opens, when it settles on
    /// another screen (after a second, so passing over a monitor mid-drag
    /// doesn't count), when screens change, and when the setting changes.
    /// </summary>
    public static void Watch(Window window, Settings settings)
    {
        var settle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void Check()
        {
            settle.Stop();
            ScreenWidth = ScreenFit.WorkArea(window)?.Width ?? double.NaN;
            var layout = Resolve(Choice(settings), double.IsFinite(ScreenWidth) ? ScreenWidth : double.PositiveInfinity);
            if (layout == Current) return;
            Current = layout;
            Changed?.Invoke(layout);
        }
        settle.Tick += (_, _) => Check();
        window.Opened += (_, _) => Check();
        window.PositionChanged += (_, _) => { settle.Stop(); settle.Start(); };
        if (window.Screens is { } screens) screens.Changed += (_, _) => Check();
        SettingChanged += Check;
        window.Closed += (_, _) => { SettingChanged -= Check; settle.Stop(); };
    }
}
