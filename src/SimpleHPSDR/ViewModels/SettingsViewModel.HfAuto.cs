using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// Configuration > Antennas: the Palstar HF-AUTO tuner. W1TR's HF-Auto program
/// drives the tuner; it reads the frequency from SimpleHPSDR over TCI (the
/// engine answers it) and can send the tuner's status back over UDP, which the
/// engine receives and this page shows. Nothing here drives the tuner.
/// </summary>
public sealed partial class SettingsViewModel
{
    private const string ThisComputer = "127.0.0.1";

    [ObservableProperty] private bool _hfAutoOn;
    [ObservableProperty] private decimal _hfAutoPort = 50001;
    [ObservableProperty] private bool _hfAutoRemote;          // the HF-Auto program runs on another computer
    [ObservableProperty] private string _hfAutoAddress = "";  // that computer's address
    [ObservableProperty] private string _hfAutoStatusText = "";
    [ObservableProperty] private string _hfAutoTunerText = "";
    [ObservableProperty] private bool _hfAutoHasTuner;
    private bool _hfAutoLoading;

    private async Task LoadHfAutoAsync()
    {
        var s = await _client.GetHfAutoAsync();
        _hfAutoLoading = true;
        HfAutoOn = s.Config.Enabled;
        HfAutoPort = s.Config.Port;
        HfAutoRemote = s.Config.ExpectedSenderAddress is not (ThisComputer or "localhost" or "::1");
        HfAutoAddress = HfAutoRemote ? s.Config.ExpectedSenderAddress : "";
        _hfAutoLoading = false;
        ShowHfAuto(s);
    }

    // The checkbox applies at once; the port and address with Apply, so a
    // half-typed address never goes to the engine.
    partial void OnHfAutoOnChanged(bool value)
    {
        if (!_hfAutoLoading) _ = ApplyHfAutoAsync();
    }

    [RelayCommand]
    private async Task ApplyHfAutoAsync()
    {
        string sender = HfAutoRemote ? HfAutoAddress.Trim() : ThisComputer;
        if (HfAutoRemote && !System.Net.IPAddress.TryParse(sender, out _))
        {
            HfAutoStatusText = "Enter the other computer's IP address, such as 192.168.1.20.";
            return;
        }
        // From another computer the engine has to listen on the network, not just this computer.
        var cfg = new StationClient.HfAutoConfig(HfAutoOn, HfAutoRemote ? "*" : ThisComputer,
            (int)Math.Clamp(HfAutoPort, 1024, 65535), sender);
        try { ShowHfAuto(await _client.SetHfAutoAsync(cfg)); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException)
        {
            HfAutoStatusText = $"Not applied: {ex.Message}";
        }
    }

    private async Task RefreshHfAutoAsync()
    {
        if (!HfAutoOn) return;
        try { ShowHfAuto(await _client.GetHfAutoAsync()); }
        catch (Exception) { }
    }

    private void ShowHfAuto(StationClient.HfAutoStatus s)
    {
        HfAutoStatusText = !s.Config.Enabled ? "Off."
            : s.Error is { } e ? $"Can't listen: {e}"
            : s.Connected ? $"Receiving from the HF-Auto program ({s.PacketsReceived} updates)."
            : s.ListenerActive ? $"Listening on port {s.Config.Port}; nothing from the HF-Auto program yet."
            : "Starting…";
        HfAutoHasTuner = s.Config.Enabled && s.Telemetry is not null;
        HfAutoTunerText = s.Telemetry is { } t ? Describe(t) : "";
    }

    private static string Describe(StationClient.HfAutoTelemetry t)
    {
        var parts = new List<string>();
        if (t.Mode is { Length: > 0 } mode) parts.Add($"Mode {mode}");
        if (t.DetectedFrequencyHz is { } hz && hz > 0) parts.Add($"{hz / 1e6:0.000} MHz");
        if (t.Antenna is { } ant) parts.Add($"antenna {ant}");
        if (t.Inductor is { } l) parts.Add($"L {l}");
        if (t.Capacitor is { } c) parts.Add($"C {c}");
        if (t.DisplayedPowerWatts is { } w && t.HasRf == true) parts.Add($"{w:0} W");
        if (t.Swr is { } swr && t.HasRf == true) parts.Add($"SWR {swr:0.0}");
        if (t.TuneComplete == true) parts.Add("tuned");
        if (t.Stored == true) parts.Add("stored");
        if (t.InvalidFrequency == true) parts.Add("frequency out of range");
        return parts.Count == 0 ? "No tuner details yet." : string.Join(" · ", parts);
    }
}
