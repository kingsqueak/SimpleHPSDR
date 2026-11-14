using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Contracts;
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
        try
        {
            var cw = (await _client.GetCwIdAsync()).Settings;
            (CwIdWpm, CwIdToneHz, CwIdLevelDb, CwIdIntervalMin) = (cw.Wpm, cw.ToneHz, cw.LevelDb, cw.IntervalMinutes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException) { }
    }

    // ---- CW ID: on/off is an app setting; speed, tone, level and interval
    // are the engine's, saved as they change ----

    public bool CwIdAuto
    {
        get => AppSettings?.CwIdAuto ?? false;
        set
        {
            if (AppSettings is null || AppSettings.CwIdAuto == value) return;
            AppSettings.CwIdAuto = value;
            AppSettings.Save();
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private decimal _cwIdWpm = 20;
    [ObservableProperty] private decimal _cwIdToneHz = 800;
    [ObservableProperty] private double _cwIdLevelDb = -18;
    [ObservableProperty] private decimal _cwIdIntervalMin = 10;
    public string CwIdLevelText => $"{CwIdLevelDb:0} dB";

    partial void OnCwIdWpmChanged(decimal value) => SendCwId();
    partial void OnCwIdToneHzChanged(decimal value) => SendCwId();
    partial void OnCwIdLevelDbChanged(double value)
    {
        OnPropertyChanged(nameof(CwIdLevelText));
        SendCwId();
    }
    partial void OnCwIdIntervalMinChanged(decimal value) => SendCwId();

    // All four together, one request at a time and the newest last, so a
    // dragged slider can't leave the engine on a value from part-way along.
    private Latest<(int Wpm, int ToneHz, double LevelDb, int IntervalMin)>? _cwIdSender;

    private void SendCwId()
    {
        if (_populating) return;
        (_cwIdSender ??= new(async v =>
        {
            try
            {
                await _client.SetCwIdSettingsAsync(new CwIdSettingsSetRequest(
                    Wpm: v.Wpm, ToneHz: v.ToneHz, LevelDb: v.LevelDb, IntervalMinutes: v.IntervalMin));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException)
            {
                Status = $"CW ID settings weren't saved: {ex.Message}";
            }
        })).Send(((int)CwIdWpm, (int)CwIdToneHz, Math.Round(CwIdLevelDb), (int)CwIdIntervalMin));
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
