using Avalonia.Controls;

namespace SimpleHPSDR.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
    }
}
