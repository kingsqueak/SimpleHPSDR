using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>The 6 m LNA button left of RFG. On the G2 the 6 m receive path
/// has a low-noise preamp with its own filter; turning it off sends 6 m
/// through the bypass path instead (no preamp, no 6 m filter). The engine
/// keeps one setting per ADC; the button sets the one the active slice
/// hears, like ATT: the Rx2 jack's while slice B is active on it.</summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowLna))]
    private bool _lnaSupported;
    private bool _lna0Off, _lna1Off;

    /// <summary>Only on 6 m, and only where the engine can switch the LNA.</summary>
    public bool ShowLna => LnaSupported && SimpleHPSDR.Band.Find(VfoHz)?.Name == "6";
    public bool LnaOn => !(AttOnRx2Jack ? _lna1Off : _lna0Off);
    public string LnaTip => (LnaOn ? "6 m LNA on" : "6 m LNA off")
        + (AttOnRx2Jack ? " (Rx2 jack)" : "")
        + ": the radio's low-noise preamp and 6 m filter, for weak signals. Turn it off if a strong nearby"
        + " signal overloads the receiver; 6 m then goes through the bypass path, without the preamp or the 6 m filter."
        + " Blue = on.";

    private void NotifyLna()
    {
        OnPropertyChanged(nameof(ShowLna));
        OnPropertyChanged(nameof(LnaOn));
        OnPropertyChanged(nameof(LnaTip));
    }

    private async Task LoadLnaAsync()
    {
        try
        {
            var rf = await _client!.GetRfFiltersAsync();
            (_lna0Off, _lna1Off) = (rf.Adc0Rx6mLnaDisabled == true, rf.Adc1Rx6mLnaDisabled == true);
            LnaSupported = rf.Rx6mLnaControlSupported;
        }
        catch (Exception) { LnaSupported = false; }
        NotifyLna();
    }

    [RelayCommand]
    private async Task ToggleLnaAsync()
    {
        if (!IsConnected) return;
        bool rx2 = AttOnRx2Jack;
        await Try(async () =>
        {
            // The engine replaces the whole filter setup: send it back as read,
            // with the one LNA flag changed.
            var rf = await _client!.GetRfFiltersAsync();
            bool off0 = rf.Adc0Rx6mLnaDisabled == true, off1 = rf.Adc1Rx6mLnaDisabled == true;
            if (rx2) off1 = !off1; else off0 = !off0;
            await _client.SetRfFiltersAsync(new RfFilterSettingsSetRequest(
                rf.CustomMatrixEnabled, rf.RxBypassAll, rf.RxBypassOnTx, rf.RxBypassOnPureSignal, rf.Profiles,
                off0, off1));
            (_lna0Off, _lna1Off) = (off0, off1);
            Status = LnaOn ? "6 m LNA on." : "6 m LNA off: 6 m goes through the bypass path.";
        });
        NotifyLna();
    }
}
