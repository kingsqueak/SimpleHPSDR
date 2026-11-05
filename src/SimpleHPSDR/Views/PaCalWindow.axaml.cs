using Avalonia.Controls;
using Avalonia.Interactivity;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Views;

public partial class PaCalWindow : Window
{
    public PaCalWindow()
    {
        InitializeComponent();
        // Only Stop ends a run; held results need Save or Discard.
        Closing += (_, e) => e.Cancel = DataContext is PaCalViewModel { CanClose: false };
        Opened += (_, _) => _ = (DataContext as PaCalViewModel)?.OpenAsync();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
