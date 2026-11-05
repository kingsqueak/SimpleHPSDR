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
        // The radio can be used while this is open: catch up on coming back.
        Activated += (_, _) => _ = (DataContext as SettingsViewModel)?.RefreshLiveAsync();
    }

    private async void OnCalibratePa(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        try
        {
            var calibration = await vm.BeginPaCalibrationAsync();
            // It transmits, so the radio's controls stay out of reach until it's done.
            if (Owner is Window main) main.IsEnabled = false;
            await new PaCalWindow { DataContext = calibration }.ShowDialog(this);
            await vm.AfterPaCalibrationAsync();
        }
        catch (Exception ex) { vm.Status = $"PA calibration: {ex.Message}"; }
        finally
        {
            if (Owner is Window main) main.IsEnabled = true;
        }
    }

    private async void OnChooseIdSound(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a sound",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("WAV sound (*.wav)") { Patterns = ["*.wav", "*.WAV"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path && DataContext is SettingsViewModel vm)
            vm.SetIdSound(path);
    }
}
