using Avalonia;
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
        // The mic expander's level, read only while the TX Audio page is showing.
        var expanderMeter = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        expanderMeter.Tick += (_, _) =>
        {
            if (WindowState != WindowState.Minimized)
                _ = (DataContext as SettingsViewModel)?.RefreshExpanderMeterAsync();
        };
        Opened += (_, _) => { expanderMeter.Start(); NoteTxAudioPage(); };
        Closed += (_, _) =>
        {
            expanderMeter.Stop();
            if (_vm is { } vm)
            {
                vm.TxAudioPageShown = false;
                vm.SimpleStudioWindow -= OnSimpleStudioWindow;
            }
            _studio?.Close();
        };
        TxAudioTab.PropertyChanged += (_, e) =>
        {
            if (e.Property == TabItem.IsSelectedProperty) NoteTxAudioPage();
        };
        // The radio can be used while this is open: catch up on coming back.
        Activated += (_, _) => _ = (DataContext as SettingsViewModel)?.RefreshLiveAsync();
    }

    private SettingsViewModel? _vm;
    private SimpleStudioWindow? _studio;

    private void NoteTxAudioPage()
    {
        if (_vm is { } vm) vm.TxAudioPageShown = IsVisible && TxAudioTab.IsSelected;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is { } old) old.SimpleStudioWindow -= OnSimpleStudioWindow;
        _vm = DataContext as SettingsViewModel;
        if (_vm is { } vm) vm.SimpleStudioWindow += OnSimpleStudioWindow;
        NoteTxAudioPage();
    }

    // SimpleStudio: opened by its tick box or Show, closed by unticking it
    // (or a load that turns the chain off) and with this window. Owned by
    // the main window, like this one; it keeps its size and place.
    private void OnSimpleStudioWindow(bool show)
    {
        if (!show)
        {
            _studio?.Close();
            return;
        }
        if (_studio is { IsVisible: true })
        {
            _studio.Activate();
            return;
        }
        if (_vm is not { } vm) return;
        var app = vm.AppSettings;
        var window = new SimpleStudioWindow { DataContext = vm.Studio };
        if (app is { StudioWidth: double w, StudioHeight: double h })
            (window.Width, window.Height) = (w, h);
        if (app is { StudioX: int x, StudioY: int y } && Screens.ScreenFromPoint(new PixelPoint(x, y)) is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(x, y);
        }
        window.Closing += (_, _) =>
        {
            if (app is null) return;
            (app.StudioWidth, app.StudioHeight) = (window.Width, window.Height);
            (app.StudioX, app.StudioY) = (window.Position.X, window.Position.Y);
            app.Save();
        };
        window.Closed += (_, _) => { if (_studio == window) _studio = null; };
        _studio = window;
        window.Show(Owner as Window ?? this);
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
