using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace SimpleHPSDR.Controls;

/// <summary>
/// <c>c:FlyoutClose.OnClick="True"</c> on a button closes the popup it's in
/// once the click has finished. Usable in shared templates, which have no
/// code-behind for a Click handler. A button's Click runs before its Command;
/// closing right away would detach the button and the command would never
/// run, so the close is posted.
/// </summary>
public static class FlyoutClose
{
    public static readonly AttachedProperty<bool> OnClickProperty =
        AvaloniaProperty.RegisterAttached<Button, bool>("OnClick", typeof(FlyoutClose));

    public static bool GetOnClick(Button b) => b.GetValue(OnClickProperty);
    public static void SetOnClick(Button b, bool value) => b.SetValue(OnClickProperty, value);

    static FlyoutClose()
    {
        OnClickProperty.Changed.AddClassHandler<Button>((b, e) =>
        {
            if (e.NewValue is true) b.Click += Close;
            else b.Click -= Close;
        });
    }

    public static void Close(object? sender, RoutedEventArgs e = null!)
    {
        var popup = (sender as Control)?.FindLogicalAncestorOfType<Popup>();
        Dispatcher.UIThread.Post(() => popup?.Close());
    }
}
