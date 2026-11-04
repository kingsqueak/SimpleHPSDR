using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Views;

public partial class FirmwareWindow : Window
{
    public FirmwareWindow()
    {
        InitializeComponent();
        // Closing mid-flash would leave the radio half written.
        Closing += (_, e) => e.Cancel = DataContext is FirmwareViewModel { Busy: true };
    }

    private async void OnChooseFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose firmware",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Radio firmware (*.rbf)") { Patterns = ["*.rbf"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path && DataContext is FirmwareViewModel vm)
            vm.LoadFile(path);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
