using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// The quiet note under the status line when a newer release is out: a link to it and
/// a ✕ that hides it until the next version. Checked once per start.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] private string _updateNote = "";
    [ObservableProperty] private Uri _updatePage = new(UpdateChecker.ReleasesPage);
    private string _updateVersion = "";
    private bool _updateChecked;

    public async Task CheckForUpdatesAtStartAsync()
    {
        if (_updateChecked) return;
        _updateChecked = true;
        if (await UpdateChecker.FetchLatestAsync() is { } latest) ShowUpdate(latest);
    }

    /// <summary>Note a newer release, unless it's the one the ✕ hid.</summary>
    public void ShowUpdate(UpdateChecker.Release latest)
    {
        if (!UpdateChecker.IsNewer(latest.Version, App.Version)) return;
        _updateVersion = latest.Version;
        UpdatePage = new Uri(latest.Page);
        UpdateNote = _settings.UpdateDismissedVersion == latest.Version ? "" : $"Version {latest.Version} is available";
    }

    [RelayCommand]
    private void DismissUpdate()
    {
        _settings.UpdateDismissedVersion = _updateVersion;
        _settings.Save();
        UpdateNote = "";
    }
}
