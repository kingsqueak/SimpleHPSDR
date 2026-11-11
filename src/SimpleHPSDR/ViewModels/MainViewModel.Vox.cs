using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// VOX. The engine has none, so this watches the mic level and keys MOX while
/// you speak. Off each time the app starts, voice modes only, and any other
/// transmit takes precedence. It has no anti-VOX, so it's for headphones. A
/// stale level counts as silence, so a stopped mic meter can't hold it keyed.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan VoxTick = TimeSpan.FromMilliseconds(40);
    private const long MicFrameStaleMs = 300;

    [ObservableProperty] private bool _voxOn;

    public double VoxThresholdDb
    {
        get => _settings.VoxThresholdDb;
        set
        {
            if (value == _settings.VoxThresholdDb) return;
            _settings.VoxThresholdDb = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public double VoxDelayMs
    {
        get => _settings.VoxDelayMs;
        set
        {
            if (value == _settings.VoxDelayMs) return;
            _settings.VoxDelayMs = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    private DispatcherTimer? _voxTimer;
    private bool _voxKeyed;
    private long _voxLastHeardMs, _micFrameMs;

    private bool VoxAllowed => VoxOn && IsConnected
        && Mode is RxMode.LSB or RxMode.USB or RxMode.AM or RxMode.SAM or RxMode.DSB or RxMode.FM
        && !PsCalibrating && !_keyboardHold && !_momentaryTx && !CwSending;

    partial void OnVoxOnChanged(bool value)
    {
        // VOX pauses the monitor (see EngineMonitor) and lifting VOX restores it.
        OnPropertyChanged(nameof(MonitorOn));
        SendMonitor();
        if (value && (MonitorPreview || MonitorOnTransmit))
            Status = "Mon is paused while VOX is on: the mic would hear it and hold VOX keyed.";
        if (value)
        {
            _voxTimer ??= new DispatcherTimer(VoxTick, DispatcherPriority.Normal, (_, _) => CheckVox());
            _voxTimer.Start();
            return;
        }
        _voxTimer?.Stop();
        EndVoxKey();
    }

    private void CheckVox()
    {
        if (!VoxAllowed)
        {
            EndVoxKey();
            return;
        }
        long now = Environment.TickCount64;
        bool fresh = now - _micFrameMs < MicFrameStaleMs;
        if (fresh && _latestMicDbfs >= VoxThresholdDb)
        {
            _voxLastHeardMs = now;
            if (!_voxKeyed && !MoxOn && !TunOn && !TwoToneOn)
            {
                _voxKeyed = true;
                MoxOn = true;
            }
        }
        else if (_voxKeyed && now - _voxLastHeardMs > VoxDelayMs) EndVoxKey();
    }

    private void EndVoxKey()
    {
        if (!_voxKeyed) return;
        _voxKeyed = false;
        if (IsConnected) MoxOn = false;
    }
}
