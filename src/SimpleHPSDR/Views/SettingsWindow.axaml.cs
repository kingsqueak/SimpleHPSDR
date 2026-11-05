using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

    private async void OnChooseIdSound(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a sound",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("WAV sound (*.wav)") { Patterns = ["*.wav"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path && DataContext is SettingsViewModel vm)
            vm.SetIdSound(path);
    }
}
