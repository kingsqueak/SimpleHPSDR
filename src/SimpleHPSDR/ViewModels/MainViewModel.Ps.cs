using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Engine;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>PureSignal (WDSP calcc/iqc adaptive predistortion) and the
/// two-tone generator used to calibrate it.</summary>
public sealed partial class MainViewModel
{
    private PsMetersFrame _latestPs;
    private int _psScheduled;
    private bool _psStalled;

    [ObservableProperty] private bool _psOn;
    [ObservableProperty] private bool _twoToneOn;
    [ObservableProperty] private string _psStatus = "Off";
    /// <summary>off, ok, warn or bad: picks the badge colour.</summary>
    [ObservableProperty] private string _psLevel = "off";
    [ObservableProperty] private string _psFeedbackText = "";

    private void WirePureSignal(StationClient client)
    {
        client.PsMeters += f =>
        {
            _latestPs = f;
            if (Interlocked.Exchange(ref _psScheduled, 1) == 0)
                Dispatcher.UIThread.Post(() =>
                {
                    _psScheduled = 0;
                    UpdatePsStatus();
                });
        };
    }

    private void LoadPureSignal(StateDto s)
    {
        _applyingTx = true;
        PsOn = s.PsEnabled;
        TwoToneOn = s.TwoToneEnabled;
        _applyingTx = false;
        _psStalled = s.PsCalibrationStalled;
        UpdatePsStatus();
    }

    /// <summary>From the 1 s state poll: two-tone and the stall flag have no frame.</summary>
    private void PollPureSignal(StateDto s)
    {
        _applyingTx = true;
        TwoToneOn = s.TwoToneEnabled;
        PsOn = s.PsEnabled;
        _applyingTx = false;
        _psStalled = s.PsCalibrationStalled;
        UpdatePsStatus();
    }

    partial void OnPsOnChanged(bool value)
    {
        UpdatePsStatus();
        if (_applyingTx || !IsConnected) return;
        _ = Try(() => _client!.SetPsAsync(value));
    }

    partial void OnTwoToneOnChanged(bool value)
    {
        if (_applyingTx || !IsConnected) return;
        _ = KeyAsync(() => _client!.SetTwoToneAsync(value), value);
    }

    // Feedback level is calcc's info[4], 0..256. Thetis colours it: about
    // 128-181 is right, lower means too little sample, higher too much.
    private void UpdatePsStatus()
    {
        if (!PsOn)
        {
            (PsStatus, PsLevel, PsFeedbackText) = ("PS off", "off", "");
            return;
        }
        var f = _latestPs;
        int fb = (int)Math.Round(f.FeedbackLevel);
        PsFeedbackText = IsTransmitting ? $"FB {fb}" : "";
        if (_psStalled)
            (PsStatus, PsLevel) = ("Stalled", "bad");
        else if (f.Correcting)
            (PsStatus, PsLevel) = ("Correcting", fb is >= 128 and <= 181 ? "ok" : "warn");
        else if (IsTransmitting)
            (PsStatus, PsLevel) = ("Calibrating", "warn");
        else
            (PsStatus, PsLevel) = ("Armed", "off");
    }
}
