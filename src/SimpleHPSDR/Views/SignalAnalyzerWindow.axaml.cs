using Avalonia.Controls;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Views;

public partial class SignalAnalyzerWindow : Window
{
    public SignalAnalyzerWindow()
    {
        InitializeComponent();
        Opened += (_, _) => (DataContext as SignalAnalyzerViewModel)?.Start();
        Closed += (_, _) => (DataContext as SignalAnalyzerViewModel)?.Stop();
    }
}
