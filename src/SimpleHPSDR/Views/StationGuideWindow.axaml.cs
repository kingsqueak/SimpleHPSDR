using Avalonia.Controls;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Views;

public partial class StationGuideWindow : Window
{
    public StationGuideWindow()
    {
        InitializeComponent();
        Opened += (_, _) => (DataContext as StationGuideViewModel)?.Start();
        Closed += (_, _) => (DataContext as StationGuideViewModel)?.Stop();
    }
}
