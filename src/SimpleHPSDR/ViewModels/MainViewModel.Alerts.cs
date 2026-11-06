using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// The engine's transmit protection alerts, shown as a banner in the main and
/// mini windows. A trip stays up until it's dismissed or the radio next keys;
/// the timeout warning goes when TX ends. The engine has already unkeyed by
/// the time a trip arrives, but it lets a new key through straight away, so
/// everything here that keys by itself is stopped too.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AlertShown))] private string _alertText = "";
    [ObservableProperty] private bool _alertIsWarning;

    public bool AlertShown => AlertText.Length > 0;

    private void WireAlerts(StationClient client)
    {
        _planDismissed = false;
        ClearAlert();   // one from an earlier connection no longer applies
        client.Alert += f => Dispatcher.UIThread.Post(() => OnAlert(f));
    }

    private void OnAlert(AlertFrame f)
    {
        if (f.Kind == AlertKind.TxTimeoutWarning)
        {
            if (AlertShown && !AlertIsWarning) return;   // a trip outranks it
            if (!IsTransmitting) return;                 // already unkeyed
            _planAlert = false;
            AlertIsWarning = true;
            AlertText = f.Message;
            return;
        }

        _planAlert = false;
        AlertIsWarning = false;
        AlertText = VoxOn ? $"{f.Message}. VOX turned off." : f.Message;
        Status = f.Message;
        if (TwoToneOn)
        {
            _applyingTx = true;
            TwoToneOn = false;
            _applyingTx = false;
        }
        VoxOn = false;
        _ = UnkeyAsync();   // also ends PS CAL, queued CW and a keyboard hold
    }

    private void TrackKeyForAlert()
    {
        if (!IsTransmitting) _planDismissed = false;
        if (AlertShown && AlertIsWarning != IsTransmitting) ClearAlert();
        if (IsTransmitting) _ = CheckBandPlanAsync();
    }

    [RelayCommand]
    private void DismissAlert()
    {
        _planDismissed = _planAlert;   // no second warning this transmission
        ClearAlert();
    }

    private void ClearAlert()
    {
        _planAlert = false;
        AlertText = "";
        AlertIsWarning = false;
    }

    // ---- Warn only: the engine's guard is off, so look here -------------

    private bool _planAlert, _planDismissed, _planChecking;

    /// <summary>On key down and each poll while transmitting, so retuning
    /// during a transmission counts too.</summary>
    private async Task CheckBandPlanAsync(StateDto? state = null)
    {
        if (_client is not { } client || _planChecking) return;
        _planChecking = true;
        try
        {
            string? outside = null;
            if (!_settings.GeneralCoverage && !_settings.BandPlanBlocks)
            {
                var s = state ?? await client.GetStateAsync();
                var plan = await client.GetBandPlanAsync();
                var (mode, low, high) = BandPlanCheck.Emission(s, TunOn);
                if (!BandPlanCheck.Inside(plan.Segments, low, high, mode))
                    outside = $"Outside the {plan.Region.DisplayName} band plan: {low / 1e6:0.000000} MHz {mode}";
            }
            if (!IsTransmitting) return;   // unkeyed while asking
            if (outside is null)
            {
                if (_planAlert) ClearAlert();
            }
            else if (!_planDismissed && (!AlertShown || _planAlert))
            {
                _planAlert = true;
                AlertIsWarning = true;
                AlertText = outside;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException) { }
        finally { _planChecking = false; }
    }
}
