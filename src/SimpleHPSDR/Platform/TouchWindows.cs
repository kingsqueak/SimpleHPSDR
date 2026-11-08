using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using SimpleHPSDR.Views;

namespace SimpleHPSDR.Platform;

/// <summary>
/// While the main window shows the touch layout, the other windows (Config,
/// the station guide, CW, Help and the dialogs) open with the touch layout's
/// finger-sized controls too, and on a small screen they fill it. The main
/// window has its own layouts and the mini window scales itself, so both are
/// left alone. Windows already open keep their look until they're reopened.
/// </summary>
public static class TouchWindows
{
    private static readonly Uri TouchStyles = new("avares://SimpleHPSDR/Views/TouchStyles.axaml");

    public static void Enable() =>
        Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) => Apply(w));

    private static void Apply(Window w)
    {
        if (LayoutMode.Current != UiLayout.Touch || w is MainWindow or MiniWindow) return;
        if (!w.Styles.OfType<StyleInclude>().Any(s => s.Source == TouchStyles))
            w.Styles.Add(new StyleInclude(new Uri("avares://SimpleHPSDR/")) { Source = TouchStyles });
        // On a radio's small screen a window gets all of it; on a big one
        // (touch chosen by hand) it keeps its size.
        if (w.CanResize && ScreenFit.WorkArea(w) is { Width: < LayoutMode.TouchBelowWidth })
            w.WindowState = WindowState.Maximized;
    }
}
