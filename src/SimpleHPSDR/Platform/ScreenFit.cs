using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Platform;

namespace SimpleHPSDR.Platform;

/// <summary>
/// Keeps windows and popups inside a small screen (a 1024×600 radio panel):
/// a window that opens larger than the screen's work area shrinks to fit it,
/// and a popup taller than the screen scrolls instead of running off it. On a
/// normal monitor nothing changes, since nothing is too big.
/// </summary>
public static class ScreenFit
{
    private const double PopupMargin = 16;

    public static void Enable()
    {
        Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) => FitWindow(w));
        Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup, e) =>
        {
            if (e.NewValue is true) FitPopup(popup);
        });
    }

    /// <summary>The screen's work area (minus taskbar and dock) in the
    /// app's own units, after display scaling.</summary>
    public static Rect? WorkArea(TopLevel? top)
    {
        if (top?.Screens is not { } screens) return null;
        Screen? s = top is WindowBase w ? screens.ScreenFromWindow(w) : null;
        s ??= screens.Primary;
        if (s is null || s.Scaling <= 0) return null;
        var a = s.WorkingArea;
        return new Rect(a.X / s.Scaling, a.Y / s.Scaling, a.Width / s.Scaling, a.Height / s.Scaling);
    }

    private static void FitWindow(Window w)
    {
        if (w.WindowState != WindowState.Normal || WorkArea(w) is not { } area) return;
        if (w.Screens.ScreenFromWindow(w) is not { } screen) return;
        // The title bar sits outside the client size on most systems.
        double frame = Math.Max(0, w.FrameSize is { } f ? f.Height - w.ClientSize.Height : 0);
        double maxW = area.Width, maxH = area.Height - frame;
        if (w.Bounds.Width <= maxW && w.Bounds.Height <= maxH) return;
        w.MinWidth = Math.Min(w.MinWidth, maxW);
        w.MinHeight = Math.Min(w.MinHeight, maxH);
        w.Width = Math.Min(w.Bounds.Width, maxW);
        w.Height = Math.Min(w.Bounds.Height, maxH);
        w.Position = new PixelPoint(screen.WorkingArea.X, screen.WorkingArea.Y);
    }

    private static void FitPopup(Popup popup)
    {
        if (popup.Child is not Control presenter) return;
        presenter.ClearValue(Layoutable.MaxHeightProperty);
        var top = TopLevel.GetTopLevel(popup.PlacementTarget ?? popup);
        if (WorkArea(top) is not { } area) return;
        double limit = area.Height - PopupMargin;
        if (limit > 0 && limit < presenter.MaxHeight) presenter.MaxHeight = limit;
    }
}
