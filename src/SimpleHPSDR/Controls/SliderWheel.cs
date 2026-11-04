using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SimpleHPSDR.Controls;

/// <summary>
/// Mouse wheel over any slider moves it: one wheel click is one unit of the
/// slider's value (or one tick when it snaps to ticks). Trackpads send
/// fractions of a click, which add up until they make a whole step. The
/// event is consumed so a scrolling page doesn't scroll at the same time.
/// A sideways scroll counts the same: right is up, left is down.
/// </summary>
public static class SliderWheel
{
    private static readonly ConditionalWeakTable<Slider, StrongBox<double>> Pending = new();

    // The wheel delta Avalonia reports for one wheel click: one line is 0.2
    // on macOS (measured), 1.0 on Windows and Linux.
    internal static readonly double ClickDelta = OperatingSystem.IsMacOS() ? 0.2 : 1.0;

    /// <summary>How far the wheel moved, up or right positive. A scroll that's
    /// a bit of both counts as whichever way it went further.</summary>
    public static double Amount(PointerWheelEventArgs e) =>
        Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? -e.Delta.X : e.Delta.Y;

    public static void Enable() =>
        InputElement.PointerWheelChangedEvent.AddClassHandler<Slider>(OnWheel, RoutingStrategies.Bubble);

    private static void OnWheel(Slider slider, PointerWheelEventArgs e)
    {
        double amount = Amount(e);
        if (!slider.IsEnabled || amount == 0) return;
        e.Handled = true;

        double step = slider.IsSnapToTickEnabled && slider.TickFrequency > 0 ? slider.TickFrequency : 1.0;
        var acc = Pending.GetOrCreateValue(slider);
        acc.Value += amount / ClickDelta;
        int clicks = (int)Math.Truncate(acc.Value + Math.CopySign(1e-6, acc.Value));
        if (clicks == 0) return;
        acc.Value -= clicks;

        double value = Math.Round((slider.Value + clicks * step) / step) * step;
        slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
    }
}
