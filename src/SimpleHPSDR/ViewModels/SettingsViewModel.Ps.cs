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

    /// <summary>For the other radio button. Both buttons only show the choice
    /// (one-way); a click goes through <see cref="ChooseFeedbackCommand"/>.</summary>
    public bool PsInternalFeedback => !PsExternalFeedback;

    /// <summary>The feedback source is one setting for the station, and it
    /// changes in exactly one way here: a click on Internal or External. It is
    /// sent at once and by itself. It is not part of the page's "send what
    /// differs" pass or of Undo, which compare against a copy of the engine's
    /// settings that can be out of date, and have put a station back on the
    /// internal coupler with nobody asking.</summary>
    [RelayCommand]
    private async Task ChooseFeedbackAsync(string which)
    {
        bool external = which == "External";
        if (_psLoaded is null) return;
        try
        {
            await _client.SetPsFeedbackSourceAsync(
                external ? PsFeedbackSource.External : PsFeedbackSource.Internal, "Config: clicked");
            if (AppSettings is { } app)
            {
                app.PsExternalFeedback = external;
                app.Save();
            }
            _psLoaded = _psLoaded with { PsFeedbackSource = external ? PsFeedbackSource.External : PsFeedbackSource.Internal };
            _populating = true;
            try { PsExternalFeedback = external; }
            finally { _populating = false; }
            Status = $"PureSignal feedback: {(external ? "external" : "internal")} coupler.";
            // External turns automatic attenuation on: send that with the rest of the page.
            Schedule(Group.PureSignal);
        }
        catch (Exception ex)
        {
            Status = $"Not applied: {ex.Message}";
            // Put the buttons back on what the engine still has.
            OnPropertyChanged(nameof(PsExternalFeedback));
            OnPropertyChanged(nameof(PsInternalFeedback));
        }
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
        // The feedback source is not sent from here: see ChooseFeedbackAsync.
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
            // The corrections saved for each band belong to the setup being left.
            int forgotten = 0;
            string dir = Path.Combine(SimpleHPSDR.Engine.EngineHost.DataDir, "ps-corrections");
            if (Directory.Exists(dir))
                foreach (string file in Directory.GetFiles(dir, "*.txt"))
                {
                    try { File.Delete(file); forgotten++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            Status = forgotten > 0
                ? "PureSignal calibration reset, and the saved corrections forgotten."
                : "PureSignal calibration reset.";
        }
        catch (Exception ex)
        {
            Status = $"Reset failed: {ex.Message}";
        }
    }
}
