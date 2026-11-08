using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SimpleHPSDR.Controls;

/// <summary>
/// A long press does what a right-click does: it opens the control's context
/// menu or popup. Avalonia's own hold gesture is cancelled as soon as a button
/// grabs the pointer, which every button does on press, so the hold is timed
/// here: a press that stays within a few pixels for <see cref="HoldTime"/>
/// becomes a context request on what was pressed.
///
/// A finger or pen can long-press anything (on the spectrum it opens the slice
/// menu). A mouse can long-press buttons only, so holding still before a drag
/// on the spectrum or a slider, or selecting text, behaves as before; the
/// right button is there for the rest.
///
/// When a menu opens, the release that ends the press is swallowed so the
/// button underneath doesn't also click. When nothing opens (a button with no
/// menu, such as MOX), the release goes through and a slow click is still a
/// click.
/// </summary>
public static class LongPress
{
    public static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(500);
    private const double MoveSlop = 10;

    private static readonly DispatcherTimer Timer = new() { Interval = HoldTime };
    private static IPointer? _pressed, _held;
    private static Interactive? _source;
    private static Point _at;
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        Timer.Tick += (_, _) => Fire();

        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((top, e) =>
        {
            Cancel();
            if (e.Pointer.Type == PointerType.Mouse && !MouseCanHold(top, e)) return;
            _pressed = e.Pointer;
            _source = e.Source as Interactive;
            _at = e.GetPosition(top);
            Timer.Start();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Moving away is a drag (tuning, panning, a slider), not a hold.
        InputElement.PointerMovedEvent.AddClassHandler<TopLevel>((top, e) =>
        {
            if (!ReferenceEquals(e.Pointer, _pressed)) return;
            var p = e.GetPosition(top);
            if (Math.Abs(p.X - _at.X) > MoveSlop || Math.Abs(p.Y - _at.Y) > MoveSlop) Cancel();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        InputElement.PointerReleasedEvent.AddClassHandler<TopLevel>((_, e) =>
        {
            if (ReferenceEquals(e.Pointer, _pressed)) Cancel();
            if (_held is null || !ReferenceEquals(e.Pointer, _held)) return;
            // A menu opened: no click, and let go of the pointer so the
            // button doesn't stay pressed.
            _held = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    // The left button, on a button.
    private static bool MouseCanHold(TopLevel top, PointerPressedEventArgs e) =>
        e.GetCurrentPoint(top).Properties.IsLeftButtonPressed
        && (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null;

    private static void Cancel()
    {
        Timer.Stop();
        _pressed = null;
        _source = null;
    }

    private static void Fire()
    {
        Timer.Stop();
        if (_pressed is not { } pointer || _source is not { } source) return;
        _pressed = null;
        _source = null;
        var request = new ContextRequestedEventArgs { RoutedEvent = InputElement.ContextRequestedEvent, Source = source };
        source.RaiseEvent(request);
        if (request.Handled) _held = pointer;
    }
}
