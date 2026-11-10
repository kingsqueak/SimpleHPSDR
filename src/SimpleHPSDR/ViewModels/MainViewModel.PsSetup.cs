using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>The guided PureSignal setup: opens by itself the first time
/// PureSignal is used, and from the PS popup or Config > PureSignal after that.</summary>
public sealed partial class MainViewModel
{
    public Func<PsSetupViewModel, Task>? ShowPsSetup { get; set; }
    private bool _psSetupOpen;
    private bool _psCalSucceeded;

    [RelayCommand]
    private async Task OpenPsSetupAsync()
    {
        if (_psSetupOpen || ShowPsSetup is null || !IsConnected) return;
        _psSetupOpen = true;
        var setup = new PsSetupViewModel(this, _psExternal);
        try { await ShowPsSetup(setup); }
        finally
        {
            setup.Detach();
            _psSetupOpen = false;
        }
    }

    /// <summary>First use of PureSignal (PS turned on, or PS CAL pressed) before
    /// the setup was ever finished: open the guided setup instead.</summary>
    private bool OfferPsSetup()
    {
        if (_settings.PsSetupDone || _psSetupOpen || ShowPsSetup is null || !IsConnected) return false;
        _ = OpenPsSetupAsync();
        return true;
    }

    public void MarkPsSetupDone()
    {
        if (_settings.PsSetupDone) return;
        _settings.PsSetupDone = true;
        _settings.Save();
    }

    /// <summary>Set the feedback source, start from the radio's default HW peak,
    /// and run PS CAL. True when it locked.</summary>
    public async Task<bool> RunGuidedPsCalAsync(bool external)
    {
        if (_client is not { } client || IsTransmitting) return false;
        try
        {
            if (external != _psExternal)
            {
                await client.SetPsFeedbackSourceAsync(external ? PsFeedbackSource.External : PsFeedbackSource.Internal);
                _psExternal = external;
            }
            _settings.PsExternalFeedback = external;
            _settings.Save();
            var s = await client.GetStateAsync();
            // An external amplifier's sample level moves with band and drive: always automatic.
            bool? autoAttenuate = external && !s.PsAutoAttenuate ? true : null;
            double? hwPeak = s.PsHwPeakDefault > 0 && Math.Abs(s.PsHwPeak - s.PsHwPeakDefault) > 1e-6 ? s.PsHwPeakDefault : null;
            if (autoAttenuate is not null || hwPeak is not null)
                await client.SetPsAdvancedAsync(new PsAdvancedSetRequest(AutoAttenuate: autoAttenuate, HwPeak: hwPeak));
            if (hwPeak is { } hw) _psHwPeak = hw;
            MeasuredTxPeak = 0;   // the old reading belonged to the old setup
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException)
        {
            Status = $"PureSignal setup: {ex.Message}";
            return false;
        }
        _psCalSucceeded = false;
        await CalibratePsCommand.ExecuteAsync(null);
        return _psCalSucceeded;
    }
}
