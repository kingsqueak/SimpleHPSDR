using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

public sealed partial class SettingsViewModel
{
    private StateDto? _psLoaded;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PsInternalFeedback))]
    private bool _psExternalFeedback;

    // A real property for the other radio button: a negated binding ("!x")
    // only works one way, so the pair would fight and flip the source.
    public bool PsInternalFeedback
    {
        get => !PsExternalFeedback;
        set { if (value) PsExternalFeedback = false; }
    }
    [ObservableProperty] private bool _psAutoAttenuate;
    [ObservableProperty] private double _psFeedbackAttenDb;
    [ObservableProperty] private double _psMoxDelaySec;
    [ObservableProperty] private double _psLoopDelaySec;
    [ObservableProperty] private double _psAmpDelayNs;
    [ObservableProperty] private double _psHwPeak;
    [ObservableProperty] private double _psHwPeakDefault;
    [ObservableProperty] private bool _psMonitor;

    private async Task LoadPureSignalAsync()
    {
        var s = _psLoaded = await _client.GetStateAsync();
        PsExternalFeedback = s.PsFeedbackSource == PsFeedbackSource.External;
        PsAutoAttenuate = s.PsAutoAttenuate;
        PsFeedbackAttenDb = s.PsTxFeedbackAttenuationDb;
        PsMoxDelaySec = s.PsMoxDelaySec;
        PsLoopDelaySec = s.PsLoopDelaySec;
        PsAmpDelayNs = s.PsAmpDelayNs;
        PsHwPeak = s.PsHwPeak;
        PsHwPeakDefault = s.PsHwPeakDefault;
        PsMonitor = s.PsMonitorEnabled;
    }

    private async Task SavePureSignalAsync()
    {
        if (_psLoaded is not { } was) return;
        var source = PsExternalFeedback ? PsFeedbackSource.External : PsFeedbackSource.Internal;
        if (source != was.PsFeedbackSource)
            await _client.SetPsFeedbackSourceAsync(source);
        await _client.SetPsAdvancedAsync(new PsAdvancedSetRequest(
            PsAutoAttenuate, PsMoxDelaySec, PsLoopDelaySec, PsAmpDelayNs, PsHwPeak));
        // With auto-attenuate on, the engine manages the attenuator itself.
        if (!PsAutoAttenuate && (int)Math.Round(PsFeedbackAttenDb) != was.PsTxFeedbackAttenuationDb)
            await _client.SetPsFeedbackAttenuationAsync((int)Math.Round(PsFeedbackAttenDb));
        if (PsMonitor != was.PsMonitorEnabled)
            await _client.SetPsMonitorAsync(PsMonitor);
    }

    [RelayCommand]
    private void UseDefaultHwPeak() => PsHwPeak = PsHwPeakDefault;

    [RelayCommand]
    private async Task ResetPsAsync()
    {
        try
        {
            await _client.ResetPsAsync();
            Status = "PureSignal calibration reset.";
        }
        catch (Exception ex)
        {
            Status = $"Reset failed: {ex.Message}";
        }
    }
}
