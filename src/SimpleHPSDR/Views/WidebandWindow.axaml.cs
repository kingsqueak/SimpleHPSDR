using Avalonia.Controls;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Views;

public partial class WidebandWindow : Window
{
    public WidebandWindow()
    {
        InitializeComponent();
        Opened += (_, _) => (DataContext as WidebandViewModel)?.Start();
        Closed += (_, _) => (DataContext as WidebandViewModel)?.Stop();
    }
}
