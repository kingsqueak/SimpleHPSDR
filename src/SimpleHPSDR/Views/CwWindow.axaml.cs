using Avalonia.Controls;
using Avalonia.Input;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Views;

/// <summary>CW sending, macros and decoded text. A window rather than a popup,
/// so it stays open through a contact.</summary>
public partial class CwWindow : Window
{
    public CwWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || DataContext is not MainViewModel vm) return;
            vm.StopCwCommand.Execute(null);
            e.Handled = true;
        };
        SendBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || DataContext is not MainViewModel vm) return;
            if (vm.SendCwTextCommand.CanExecute(null)) vm.SendCwTextCommand.Execute(null);
            e.Handled = true;
        };
    }
}
