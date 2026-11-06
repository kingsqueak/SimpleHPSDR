using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Views;

/// <summary>CW sending, macros and decoded text. A window rather than a popup,
/// so it stays open through a contact. With Keyboard keying on, space and the
/// arrows key the radio, but only while this window has focus and no text
/// box is being typed in.</summary>
public partial class CwWindow : Window
{
    public CwWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDownFirst, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUpFirst, RoutingStrategies.Tunnel);
        AddHandler(GotFocusEvent, (_, _) => { if (Typing) Vm?.ReleaseCwKeys(); }, RoutingStrategies.Bubble);
        Deactivated += (_, _) => Vm?.ReleaseCwKeys();
        Closed += (_, _) => Vm?.ReleaseCwKeys();
        SendBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Vm is not { } vm) return;
            if (vm.SendCwTextCommand.CanExecute(null)) vm.SendCwTextCommand.Execute(null);
            e.Handled = true;
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private bool Typing => FocusManager?.GetFocusedElement() is TextBox { IsReadOnly: false };

    private static CwKey? KeyFor(Key key) => key switch
    {
        Key.Space => CwKey.Straight,
        Key.Left => CwKey.Dit,
        Key.Right => CwKey.Dah,
        _ => null,
    };

    private void OnKeyDownFirst(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.Key == Key.Escape)
        {
            vm.ReleaseCwKeys();
            vm.StopCwCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (KeyFor(e.Key) is { } key && !Typing && vm.CwKeyDown(key)) e.Handled = true;
    }

    private void OnKeyUpFirst(object? sender, KeyEventArgs e)
    {
        if (KeyFor(e.Key) is { } key) Vm?.CwKeyUp(key);
    }
}
