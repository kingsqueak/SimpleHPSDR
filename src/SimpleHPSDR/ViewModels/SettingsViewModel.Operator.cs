using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>Configuration > Station: your callsign, kept by the engine. RADE
/// (FreeDV) sends it at the end of each over; others may use it later.</summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty] private string _callsign = "";
    [ObservableProperty] private string _callsignStatus = "";
    private string _grid = "", _callsignLoaded = "";

    private async Task LoadOperatorAsync()
    {
        var op = await _client.GetOperatorAsync();
        Callsign = _callsignLoaded = op.Callsign ?? "";
        _grid = op.Grid ?? "";
    }

    [RelayCommand]
    private async Task SaveCallsignAsync()
    {
        string call = Callsign.Trim().ToUpperInvariant();
        if (call.Length > 0 && (call.Length > 10 || !call.All(c => char.IsAsciiLetterOrDigit(c) || c == '/')))
        {
            CallsignStatus = "Letters, digits and / only, up to 10.";
            return;
        }
        if (call == _callsignLoaded) { Callsign = call; return; }
        try
        {
            await _client.SetOperatorAsync(call, _grid);
            Callsign = _callsignLoaded = call;
            CallsignStatus = call.Length == 0 ? "Callsign cleared." : "Saved.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException)
        {
            CallsignStatus = $"Not saved: {ex.Message}";
        }
    }
}
