using Avalonia.Controls;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Views;

public partial class AnalyzerWindow : Window
{
    public AnalyzerWindow()
    {
        InitializeComponent();
        Opened += (_, _) => _ = (DataContext as AnalyzerViewModel)?.LoadAsync();
        Closed += (_, _) => (DataContext as AnalyzerViewModel)?.Closed();
    }
}
