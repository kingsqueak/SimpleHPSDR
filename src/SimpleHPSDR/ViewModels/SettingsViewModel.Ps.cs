using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

public sealed partial class SettingsViewModel
{
    private StateDto? _psLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PsInternalFeedback))]
    [NotifyPropertyChangedFor(nameof(CanChangeAutoAttenuate))]
    private bool _psExternalFeedback;

    /// <summary>An external amplifier's sample level moves with band, drive
    /// and amplifier gain, so external feedback always uses automatic
    /// attenuation.</summary>
    public bool CanChangeAutoAttenuate => !PsExternalFeedback;

    partial void OnPsExternalFeedbackChanged(bool value)
    {
        if (value) PsAutoAttenuate = true;
    }

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

    /// <summary>Opens the guided PureSignal setup (the window closes first).</summary>
    public Action? OpenPsSetup { get; init; }

    /// <summary>The app's own settings file, for options the engine doesn't hold.</summary>
    public Settings? AppSettings { get; init; }

    public bool AutoHwPeak
    {
        get => AppSettings?.AutoHwPeak ?? true;
        set
        {
            if (AppSettings is null || AppSettings.AutoHwPeak == value) return;
            AppSettings.AutoHwPeak = value;
            AppSettings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Peak TX envelope from the last transmission, from the main window.</summary>
    public double MeasuredTxPeak { get; init; }
    public bool HasMeasuredTxPeak => MeasuredTxPeak > 0;
    public string MeasuredTxPeakText => HasMeasuredTxPeak
        ? $"Your last transmission peaked at {MeasuredTxPeak:0.0000}. HW peak should be a little above that: {PsPeak.Target(MeasuredTxPeak):0.0000}."
        : "Run PS CAL once and your signal's peak will show here.";

    private void PopulatePureSignal(StateDto s)
    {
        _psLoaded = s;
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

    private async Task ApplyPureSignalAsync()
    {
        if (_psLoaded is not { } was) return;
        var source = PsExternalFeedback ? PsFeedbackSource.External : PsFeedbackSource.Internal;
        // Only when the source itself was changed on this page. Saving it on any
        // PureSignal change wrote back whatever the page loaded, which may be
        // stale (the guided setup can change it while Config is open), and the
        // next connect then forced the engine back to it: External became Internal.
        if (source != was.PsFeedbackSource)
        {
            await _client.SetPsFeedbackSourceAsync(source);
            if (AppSettings is { } app)
            {
                app.PsExternalFeedback = PsExternalFeedback;
                app.Save();
            }
        }
        bool autoAttenuate = PsAutoAttenuate || PsExternalFeedback;
        if (autoAttenuate != was.PsAutoAttenuate || PsMoxDelaySec != was.PsMoxDelaySec
            || PsLoopDelaySec != was.PsLoopDelaySec || PsAmpDelayNs != was.PsAmpDelayNs
            || PsHwPeak != was.PsHwPeak)
            await _client.SetPsAdvancedAsync(new PsAdvancedSetRequest(
                autoAttenuate, PsMoxDelaySec, PsLoopDelaySec, PsAmpDelayNs, PsHwPeak));
        // With auto-attenuate on, the engine manages the attenuator itself.
        if (!autoAttenuate && (int)Math.Round(PsFeedbackAttenDb) != was.PsTxFeedbackAttenuationDb)
            await _client.SetPsFeedbackAttenuationAsync((int)Math.Round(PsFeedbackAttenDb));
        if (PsMonitor != was.PsMonitorEnabled)
            await _client.SetPsMonitorAsync(PsMonitor);
        _psLoaded = await _client.GetStateAsync();
    }

    [RelayCommand]
    private void UseDefaultHwPeak() => PsHwPeak = PsHwPeakDefault;

    // A little above the measured peak, so it lands in calcc's top step.
    [RelayCommand]
    private void UseMeasuredHwPeak() => PsHwPeak = PsPeak.Target(MeasuredTxPeak);

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
