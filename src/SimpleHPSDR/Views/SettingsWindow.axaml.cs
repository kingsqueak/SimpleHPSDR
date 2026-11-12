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

    // Config shows PureSignal values from when it opened, so it closes before
    // the guided setup changes them.
    private void OnPsSetup(object? sender, RoutedEventArgs e)
    {
        var open = (DataContext as SettingsViewModel)?.OpenPsSetup;
        Close();
        open?.Invoke();
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

    private async void OnBackup(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Back up SimpleHPSDR settings",
            SuggestedFileName = Backup.SuggestedName(),
            DefaultExtension = Backup.Extension,
            FileTypeChoices = [BackupFileType],
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await vm.SaveBackupAsync(stream, file.Name);
    }

    private async void OnRestore(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Restore SimpleHPSDR settings",
            AllowMultiple = false,
            FileTypeFilter = [BackupFileType],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) vm.RestorePath = path;
    }

    private async void OnExportTxProfile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel { SelectedTxProfile: { } name } vm) return;
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export TX profile",
            SuggestedFileName = $"{safe}.{TxProfileStore.ShareExtension}",
            DefaultExtension = TxProfileStore.ShareExtension,
            FileTypeChoices = [TxProfileFileType],
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await vm.ExportTxProfileAsync(stream, file.Name);
    }

    private async void OnImportTxProfile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import TX profile",
            AllowMultiple = false,
            FileTypeFilter = [TxProfileFileType],
        });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        await vm.ImportTxProfileAsync(stream);
    }

    private static readonly FilePickerFileType TxProfileFileType =
        new("SimpleHPSDR TX profile (*.shtx)") { Patterns = ["*." + TxProfileStore.ShareExtension] };

    private static readonly FilePickerFileType BackupFileType =
        new("SimpleHPSDR backup (*.shbackup)") { Patterns = ["*." + Backup.Extension] };

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
