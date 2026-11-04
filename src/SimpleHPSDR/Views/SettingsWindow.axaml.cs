using Avalonia.Controls;
using Avalonia.Threading;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        var supply = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        supply.Tick += (_, _) => _ = (DataContext as SettingsViewModel)?.RefreshSupplyAsync();
        Opened += (_, _) => supply.Start();
        Closed += (_, _) => supply.Stop();
    }
}
