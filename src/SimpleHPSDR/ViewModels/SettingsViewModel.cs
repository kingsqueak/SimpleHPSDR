using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>One band's PA calibration. Everything the window doesn't edit
/// (open-collector outputs) rides along in <see cref="Original"/>.</summary>
public sealed partial class PaBandRow(PaBandSettingsDto original) : ObservableObject
{
    [ObservableProperty] private double _paGainDb = Math.Round(original.PaGainDb, 1);
    [ObservableProperty] private bool _disablePa = original.DisablePa;

    public PaBandSettingsDto Original { get; } = original;
    public string Band => Original.Band;
    public PaBandSettingsDto ToDto() => Original with { PaGainDb = PaGainDb, DisablePa = DisablePa };
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly string[] EditableBands =
        ["160m", "80m", "60m", "40m", "30m", "20m", "17m", "15m", "12m", "10m", "6m"];

    private readonly StationClient _client;
    private PaSettingsDto? _loaded;

    [ObservableProperty] private bool _paEnabled;
    [ObservableProperty] private double _paMaxPowerWatts;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _loading = true;

    public ObservableCollection<PaBandRow> PaBands { get; } = [];
    public event Action<int>? PaMaxPowerSaved;

    public SettingsViewModel(StationClient client) => _client = client;

    [RelayCommand]
    public async Task LoadAsync()
    {
        Loading = true;
        try
        {
            _loaded = await _client.GetPaSettingsAsync();
            PaEnabled = _loaded.Global.PaEnabled;
            PaMaxPowerWatts = _loaded.Global.PaMaxPowerWatts;
            PaBands.Clear();
            foreach (var b in _loaded.Bands.Where(b => EditableBands.Contains(b.Band)))
                PaBands.Add(new PaBandRow(b));
            await LoadAudioAsync();
            await LoadPureSignalAsync();
            Status = "";
        }
        catch (Exception ex)
        {
            Status = $"Could not read settings: {ex.Message}";
        }
        finally
        {
            Loading = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_loaded is null) return;
        var edited = PaBands.ToDictionary(r => r.Band, r => r.ToDto());
        // Send every band back, including transverter entries we don't show.
        var bands = _loaded.Bands.Select(b => edited.GetValueOrDefault(b.Band, b)).ToList();
        var global = _loaded.Global with { PaEnabled = PaEnabled, PaMaxPowerWatts = (int)Math.Round(PaMaxPowerWatts) };
        try
        {
            await _client.SetPaSettingsAsync(new PaSettingsSetRequest(global, bands));
            await SaveAudioAsync();
            await SavePureSignalAsync();
            await LoadAsync();
            Status = "Saved.";
            PaMaxPowerSaved?.Invoke(global.PaMaxPowerWatts);
        }
        catch (Exception ex)
        {
            Status = $"Not saved: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task RevertAsync() => LoadAsync();
}
