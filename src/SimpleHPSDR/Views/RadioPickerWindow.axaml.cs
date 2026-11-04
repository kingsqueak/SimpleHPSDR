using Avalonia.Controls;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Views;

public partial class RadioPickerWindow : Window
{
    public RadioPickerWindow() => InitializeComponent();

    public Task<RadioChoice?> PickAsync(Window owner)
    {
        var vm = (RadioPickerViewModel)DataContext!;
        vm.Done += choice => Close(choice);
        vm.ShowFirmware = firmware => new FirmwareWindow { DataContext = firmware }.ShowDialog(this);
        Opened += (_, _) => vm.ShowNetworkHelp = () => InfoWindow.Network().ShowDialog(this);
        return ShowDialog<RadioChoice?>(owner);
    }
}
