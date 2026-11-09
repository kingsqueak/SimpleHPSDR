using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SimpleHPSDR.ViewModels;

/// <summary>Config > Station > Backup and restore.</summary>
public sealed partial class SettingsViewModel
{
    /// <summary>A backup picked for restoring, waiting for the Restore confirmation.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RestorePending))] [NotifyPropertyChangedFor(nameof(RestoreQuestion))]
    private string? _restorePath;
    public bool RestorePending => RestorePath is not null;
    public string RestoreQuestion => RestorePath is null ? ""
        : $"Restore from {Path.GetFileName(RestorePath)}? It replaces all your settings with the ones in the backup, and SimpleHPSDR restarts.";
    [ObservableProperty] private string _backupStatus = "";

    public async Task SaveBackupAsync(Stream stream, string name)
    {
        try
        {
            if (AppSettings is null) return;
            await Backup.CreateAsync(_client, AppSettings, stream);
            BackupStatus = $"Saved {name}.";
        }
        catch (Exception ex) { BackupStatus = $"Backup failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void CancelRestore() => RestorePath = null;

    [RelayCommand]
    private async Task ConfirmRestoreAsync()
    {
        if (RestorePath is not { } path) return;
        try
        {
            BackupStatus = "Restoring…";
            await Backup.StageRestoreAsync(_client, path);
            RestorePath = null;
            BackupStatus = "Restored. Restarting…";
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                Backup.Restart(() => desktop.Shutdown());
        }
        catch (Exception ex)
        {
            RestorePath = null;
            BackupStatus = $"Restore failed, nothing was changed: {ex.Message}";
        }
    }
}
