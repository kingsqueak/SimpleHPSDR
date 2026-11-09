using Avalonia.Controls;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Views;

public partial class PsSetupWindow : Window
{
    public PsSetupWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PsSetupViewModel vm) vm.CloseRequested += Close;
        };
        // Closing mid-calibration stops it first, so the radio is never left keyed.
        Closing += (_, _) =>
        {
            if (DataContext is PsSetupViewModel { Running: true } vm) vm.StopCommand.Execute(null);
        };
    }
}
