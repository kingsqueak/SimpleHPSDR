using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>Named TX audio profiles: choosing one loads it at once, "Save"
/// overwrites the chosen one, "Save as" makes a new one, "Default" is the
/// built-in starting point (see <see cref="TxAudioProfile.Default"/>).</summary>
public sealed partial class SettingsViewModel
{
    private bool _choosingProfile;

    [ObservableProperty] private string? _selectedTxProfile;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TxProfileHint))]
    private bool _txProfileModified;
    [ObservableProperty] private string _newTxProfileName = "";

    public ObservableCollection<string> TxProfiles { get; } = [];
    public bool CanOverwriteProfile => SelectedTxProfile is { } n && !TxProfileStore.IsDefault(n);
    public string TxProfileHint => TxProfileModified ? "modified since loaded" : "";

    private void RefreshProfileList(string? select)
    {
        _choosingProfile = true;
        TxProfiles.Clear();
        foreach (var n in TxProfileStore.Names()) TxProfiles.Add(n);
        SelectedTxProfile = select is not null && TxProfiles.Contains(select) ? select : null;
        _choosingProfile = false;
        OnPropertyChanged(nameof(CanOverwriteProfile));
        OnPropertyChanged(nameof(CanExportProfile));
    }

    private void PopulateTxProfiles() =>
        RefreshProfileList(AppSettings?.LastTxProfile);

    partial void OnSelectedTxProfileChanged(string? value)
    {
        OnPropertyChanged(nameof(CanOverwriteProfile));
        OnPropertyChanged(nameof(CanExportProfile));
        if (_choosingProfile || value is null) return;
        _ = LoadTxProfileAsync(value);
    }

    /// <summary>Write the profile's full records to the engine, then show
    /// what the engine now has.</summary>
    private async Task LoadTxProfileAsync(string name)
    {
        if (TxProfileStore.Get(name) is not { } p || _txLoaded is not { } was)
        {
            Status = $"Profile \"{name}\" could not be read.";
            return;
        }
        if (_pending.Remove(Group.TxAudio, out var cts)) cts.Cancel();
        await _applyGate.WaitAsync();
        try
        {
            if (AppSettings is { } cuts)
            {
                // The main window carries these into modes the engine keeps apart.
                (cuts.TxLowCutHz, cuts.TxHighCutHz) = (p.LowCutHz, p.HighCutHz);
                if (p.TxFilterFollowsRx is { } follows) cuts.TxFilterFollowsRx = follows;
                cuts.Save();
                OnPropertyChanged(nameof(TxFilterFollowsRx));
            }
            await _client.SetTxFilterAsync((await _client.GetStateAsync()).Mode, p.LowCutHz, p.HighCutHz);
            await _client.SetTxLevelingAsync(p.Leveling);
            await _client.SetLevelerMaxGainAsync(p.LevelerMaxGainDb);
            await _client.SetTxEqAsync(p.Equalizer);
            await _client.SetCfcAsync(p.Cfc);
            await _client.SetDexpAsync(p.NoiseGate);
            await _client.SetPhaseRotatorAsync(p.PhaseRotator);
            if (p.ReleaseDelayMs is int tail)
                await _client.SetTxTailDelayAsync(tail);

            if (AppSettings is { } settings)
                settings.ParametricEq = p.ParametricEq
                    ?? settings.ParametricEq with { Enabled = false };
            var state = await _client.GetStateAsync();
            _populating = true;
            try
            {
                PopulateTxAudio(state);
            }
            finally { _populating = false; }
            TxProfileModified = false;
            if (AppSettings is { } app)
            {
                app.LastTxProfile = name;
                app.Save();
            }
            Status = $"Loaded TX profile \"{name}\".";
        }
        catch (Exception ex)
        {
            Status = $"Profile not loaded: {ex.Message}";
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private TxAudioProfile SnapshotProfile(string name)
    {
        var was = _txLoaded!;
        return new TxAudioProfile(name,
            (int)Math.Round(TxLowCutHz), (int)Math.Round(TxHighCutHz),
            BuildLeveling(was), Math.Round(LevelerMaxGainDb, 1), BuildEq(),
            BuildCfc(was), BuildDexp(was), BuildPhaseRotator(was), (int)Math.Round(ReleaseDelayMs),
            ParametricEqOn ? SnapshotParametric() : null, TxFilterFollowsRx);
    }

    [RelayCommand]
    private void SaveTxProfile()
    {
        if (SelectedTxProfile is not { } name || TxProfileStore.IsDefault(name)) return;
        SaveProfile(name);
    }

    [RelayCommand]
    private void SaveTxProfileAs()
    {
        var name = NewTxProfileName.Trim();
        if (name.Length == 0)
        {
            Status = "Type a name for the new profile first.";
            return;
        }
        if (TxProfileStore.IsDefault(name))
        {
            Status = "\"Default\" is built in; choose another name.";
            return;
        }
        SaveProfile(name);
        NewTxProfileName = "";
    }

    private void SaveProfile(string name)
    {
        if (_txLoaded is null) return;
        try
        {
            TxProfileStore.Save(SnapshotProfile(name));
            RefreshProfileList(name);
            TxProfileModified = false;
            if (AppSettings is { } app)
            {
                app.LastTxProfile = name;
                app.Save();
            }
            Status = $"Saved TX profile \"{name}\".";
        }
        catch (Exception ex)
        {
            Status = $"Profile not saved: {ex.Message}";
        }
    }

    public bool CanExportProfile => SelectedTxProfile is not null;

    /// <summary>Export the chosen profile to a file (the window picks where).</summary>
    public async Task ExportTxProfileAsync(Stream stream, string fileName)
    {
        if (SelectedTxProfile is not { } name) return;
        try
        {
            await TxProfileStore.ExportAsync(name, stream);
            Status = TxProfileModified
                ? $"Exported \"{name}\" to {fileName} as it was last saved, without your changes since."
                : $"Exported \"{name}\" to {fileName}.";
        }
        catch (Exception ex) { Status = $"Profile not exported: {ex.Message}"; }
    }

    /// <summary>Import a profile file, add it to the list and load it.</summary>
    public async Task ImportTxProfileAsync(Stream stream)
    {
        try
        {
            var name = await TxProfileStore.ImportAsync(stream);
            RefreshProfileList(null);
            SelectedTxProfile = name;   // loads it, as choosing it from the list does
            Status = $"Imported TX profile \"{name}\" and loaded it.";
        }
        catch (Exception ex) { Status = $"Profile not imported: {ex.Message}"; }
    }

    [RelayCommand]
    private void DeleteTxProfile()
    {
        if (SelectedTxProfile is not { } name || TxProfileStore.IsDefault(name)) return;
        TxProfileStore.Delete(name);
        RefreshProfileList(null);
        Status = $"Deleted TX profile \"{name}\". Current settings are unchanged.";
    }
}
