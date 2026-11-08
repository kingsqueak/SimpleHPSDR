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
/// A sideways scroll works too, more slowly: left is up, right is down.
/// </summary>
public static class SliderWheel
{
    private static readonly ConditionalWeakTable<Slider, StrongBox<double>> Pending = new();

    // The wheel delta Avalonia reports for one wheel click: one line is 0.2
    // on macOS (measured), 1.0 on Windows and Linux.
    internal static readonly double ClickDelta = OperatingSystem.IsMacOS() ? 0.2 : 1.0;

    // Sideways scrollers send many small events, so sideways movement has to
    // add up to a few wheel clicks to make one step, with a limit on how fast
    // steps can come.
    private const double SideClicksPerStep = 3;
    private static readonly TimeSpan SideStepGap = TimeSpan.FromMilliseconds(80);
    private static double _side;
    private static DateTime _lastSideStep;

    /// <summary>How far the wheel moved, up or left positive. A scroll that's
    /// a bit of both counts as whichever way it went further. Sideways, this
    /// is zero or one wheel click.</summary>
    public static double Amount(PointerWheelEventArgs e)
    {
        if (Math.Abs(e.Delta.X) <= Math.Abs(e.Delta.Y)) return e.Delta.Y;
        _side = Math.Clamp(_side + e.Delta.X / ClickDelta, -SideClicksPerStep, SideClicksPerStep);
        var now = DateTime.UtcNow;
        if (Math.Abs(_side) < SideClicksPerStep || now - _lastSideStep < SideStepGap) return 0;
        double step = Math.Sign(_side) * ClickDelta;
        _side = 0;
        _lastSideStep = now;
        return step;
    }

    private static readonly ConditionalWeakTable<object, StrongBox<double>> PendingClicks = new();

    // A fast trackpad flick can arrive as one big event; tuning moves at
    // most this many steps per event.
    private const int MaxClicksPerEvent = 5;

    // Trackpads (and other smooth scrollers) tune a little slower than their
    // raw movement. A mouse wheel moves in whole notches, multiples of
    // ClickDelta, and isn't scaled.
    private const double SmoothScrollScale = 0.75;

    private static bool IsWholeNotches(double amount)
    {
        double notches = Math.Abs(amount) / ClickDelta;
        return Math.Abs(notches - Math.Round(notches)) < 1e-3;
    }

    /// <summary>
    /// Whole wheel clicks for a control that steps (tuning, RIT, WPM, zoom):
    /// a mouse wheel notch is one click, and a trackpad's many small events
    /// add up per control until they make one, as they do on sliders.
    /// </summary>
    public static int Clicks(object control, PointerWheelEventArgs e)
    {
        double amount = Amount(e);
        if (amount == 0) return 0;
        var acc = PendingClicks.GetOrCreateValue(control);
        // A change of direction starts afresh rather than unwinding the rest.
        if (Math.Sign(acc.Value) != 0 && Math.Sign(acc.Value) != Math.Sign(amount)) acc.Value = 0;
        acc.Value += amount / ClickDelta * (IsWholeNotches(amount) ? 1 : SmoothScrollScale);
        int clicks = (int)Math.Truncate(acc.Value + Math.CopySign(1e-6, acc.Value));
        acc.Value -= clicks;
        return Math.Clamp(clicks, -MaxClicksPerEvent, MaxClicksPerEvent);
    }

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
