using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>Configuration > Amp and Calibration. The TX timeout stays on: the
/// engine only lets it be turned off with an acknowledgement that a lost
/// connection could leave the radio keyed, and this page doesn't offer that.</summary>
public sealed partial class SettingsViewModel
{
    private sealed record AmpValues(int PreKey, int RxResume, int Timeout, int DriveMax);
    private sealed record CalValues(double Trim, double Ppm, bool ExtRef, double ExtPpm, int AdcMax, int AdcHold);
    private AmpValues? _ampWas, _ampOrig;
    private CalValues? _calWas, _calOrig;

    [ObservableProperty] private double _preKeyDelayMs;
    [ObservableProperty] private double _rxResumeDelayMs;
    [ObservableProperty] private double _txTimeoutSec;
    [ObservableProperty] private double _driveMaxPct;
    [ObservableProperty] private string _supplyText = "—";

    [ObservableProperty] private double _sMeterTrimDb;
    [ObservableProperty] private double _frequencyPpm;
    [ObservableProperty] private bool _externalRefOn;
    [ObservableProperty] private double _externalRefPpm;
    [ObservableProperty] private double _adcMaxOffsetDb;
    [ObservableProperty] private double _adcReleaseHoldMs;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CalibrateWwvCommand))] private bool _calibrating;
    [ObservableProperty] private string _calibrationResult = "";

    private async Task LoadAmpCalibrationAsync(StateDto s)
    {
        _ampWas = new AmpValues(s.TxMoxPreKeyDelayMs, s.TxPostTxRxMuteDelayMs, s.TxTimeoutSec, s.DriveMaxPct);
        _ampOrig = _ampWas;
        ShowAmp(_ampWas);
        var freq = await _client.GetFrequencyCalibrationAsync();
        var adc = await _client.GetAdcProtectionAsync();
        _calWas = new CalValues(await _client.GetSMeterTrimAsync(), freq.Ppm, freq.ExternalReference.Enabled,
            freq.ExternalReference.Ppm, adc.MaxOffsetDb, adc.ReleaseHoldMs);
        _calOrig = _calWas;
        ShowCalibration(_calWas);
        await RefreshSupplyAsync();
    }

    private void ShowAmp(AmpValues a) =>
        (PreKeyDelayMs, RxResumeDelayMs, TxTimeoutSec, DriveMaxPct) = (a.PreKey, a.RxResume, a.Timeout, a.DriveMax);

    private void ShowCalibration(CalValues c) =>
        (SMeterTrimDb, FrequencyPpm, ExternalRefOn, ExternalRefPpm, AdcMaxOffsetDb, AdcReleaseHoldMs) =
        (c.Trim, c.Ppm, c.ExtRef, c.ExtPpm, c.AdcMax, c.AdcHold);

    private void RestoreAmpCalibrationFields()
    {
        if (_ampOrig is { } a) ShowAmp(a);
        if (_calOrig is { } c) ShowCalibration(c);
    }

    /// <summary>The window refreshes this while it's open.</summary>
    public async Task RefreshSupplyAsync()
    {
        try
        {
            var v = await _client.GetSupplyVoltsAsync();
            SupplyText = v is { Supported: true, Volts: { } volts } ? $"{volts:0.0} V" : "Not reported";
        }
        catch (Exception) { SupplyText = "—"; }
    }

    private async Task ApplyAmpAsync()
    {
        if (_ampWas is not { } was) return;
        var now = new AmpValues((int)Math.Round(PreKeyDelayMs), (int)Math.Round(RxResumeDelayMs),
            (int)Math.Round(TxTimeoutSec), (int)Math.Round(DriveMaxPct));
        if (now.PreKey != was.PreKey) await _client.SetPreKeyDelayAsync(now.PreKey);
        if (now.RxResume != was.RxResume) await _client.SetRxResumeDelayAsync(now.RxResume);
        if (now.Timeout != was.Timeout) await _client.SetTxTimeoutAsync(now.Timeout);
        if (now.DriveMax != was.DriveMax) await _client.SetDriveMaxAsync(now.DriveMax);
        // The engine can hold the pre-key delay below PureSignal's hold-off:
        // show what it kept.
        var s = await _client.GetStateAsync();
        _ampWas = new AmpValues(s.TxMoxPreKeyDelayMs, s.TxPostTxRxMuteDelayMs, s.TxTimeoutSec, s.DriveMaxPct);
        if (_ampWas.PreKey != now.PreKey)
        {
            _populating = true;
            PreKeyDelayMs = _ampWas.PreKey;
            _populating = false;
            Status = $"Pre-key delay held at {_ampWas.PreKey} ms: it has to stay below PureSignal's MOX delay.";
        }
    }

    private async Task ApplyCalibrationAsync()
    {
        if (_calWas is not { } was) return;
        var now = new CalValues(SMeterTrimDb, FrequencyPpm, ExternalRefOn, ExternalRefPpm,
            (int)Math.Round(AdcMaxOffsetDb), (int)Math.Round(AdcReleaseHoldMs));
        if (now.Trim != was.Trim) await _client.SetSMeterTrimAsync(now.Trim);
        if (now.Ppm != was.Ppm) await _client.SetFrequencyPpmAsync(now.Ppm);
        if (now.ExtRef != was.ExtRef || now.ExtPpm != was.ExtPpm)
            await _client.SetExternalReferenceAsync(now.ExtRef, now.ExtPpm);
        if (now.AdcMax != was.AdcMax || now.AdcHold != was.AdcHold)
            await _client.SetAdcProtectionAsync(now.AdcMax, now.AdcHold);
        _calWas = now;
    }

    private bool CanCalibrate() => !Calibrating;

    /// <summary>Receive only: the engine tunes to WWV, measures it and tunes back.</summary>
    [RelayCommand(CanExecute = nameof(CanCalibrate))]
    private async Task CalibrateWwvAsync(string megahertz)
    {
        Calibrating = true;
        CalibrationResult = $"Listening for WWV on {megahertz} MHz…";
        try
        {
            var r = await _client.CalibrateOnWwvAsync(double.Parse(megahertz) * 1e6);
            CalibrationResult = r.Message;
            var freq = await _client.GetFrequencyCalibrationAsync();
            _populating = true;
            FrequencyPpm = Math.Round(freq.Ppm, 2);
            _populating = false;
            if (_calWas is { } was) _calWas = was with { Ppm = FrequencyPpm };
        }
        catch (Exception ex)
        {
            CalibrationResult = $"Calibration failed: {ex.Message}";
        }
        finally
        {
            Calibrating = false;
        }
    }

    [RelayCommand]
    private void ResetFrequencyPpm() => FrequencyPpm = 0;
}
