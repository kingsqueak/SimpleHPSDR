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
        get => Math.Max(_settings.VoxDelayMs, VoxDelayMinimum);
        set
        {
            value = Math.Max(Math.Round(value / 10) * 10, VoxDelayMinimum);
            if (value == _settings.VoxDelayMs) return;
            _settings.VoxDelayMs = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    // VOX may key again only once the engine has confirmed the last unkey (its
    // release runs the transmit tail first) and a short margin has passed.
    // Keying while that unkey was still in flight let its late "off" report
    // arrive after VOX had keyed again: VOX then forgot it held the key and
    // never let go. The RX resume delay plays no part: it only mutes receive
    // audio after an unkey, and the engine ends that mute when it is keyed.
    private const int VoxRekeyMarginMs = 20;
    private const long UnkeyConfirmTimeoutMs = 3000;
    private const long StaleOffReportMs = 2000;
    private bool _unkeyPending;
    private long _unkeyRequestedMs, _engineUnkeyedMs = long.MinValue / 2, _voxKeyedMs;

    /// <summary>Every engine key report.</summary>
    private void TrackEngineKeyForVox(bool keyed)
    {
        if (keyed) return;
        _unkeyPending = false;
        _engineUnkeyedMs = Environment.TickCount64;
    }

    /// <summary>MOX went off: our own request, or the engine's report.</summary>
    private void VoxSawUnkey(bool fromEngine)
    {
        long now = Environment.TickCount64;
        if (!fromEngine)
        {
            _unkeyPending = true;
            _unkeyRequestedMs = now;
        }
        // A report of an earlier unkey can land just after VOX keyed again: it
        // isn't about this key, so VOX keeps it and still drops it on silence.
        if (fromEngine && _voxKeyed && now - _voxKeyedMs < StaleOffReportMs) return;
        _voxKeyed = false;   // whoever dropped it, VOX starts afresh
    }

    private bool VoxMayKey(long now) =>
        (!_unkeyPending || now - _unkeyRequestedMs > UnkeyConfirmTimeoutMs)
        && now - _engineUnkeyedMs > VoxRekeyMarginMs;

    /// <summary>The shortest VOX delay allowed.</summary>
    public double VoxDelayMinimum => 100;
    // Never below the saved delay, so the slider can't clamp (and save) it.
    public double VoxDelayMaximum => Math.Max(2000, _settings.VoxDelayMs);
    public string VoxDelayTip =>
        "How long transmit stays on after you stop speaking. Shorter gets you back to receive "
        + "sooner. Longer keeps transmit on through the pauses between phrases.";

    /// <summary>Headphone mode: Mon and TX Mon work while VOX is on.</summary>
    public bool VoxHeadphones
    {
        get => _settings.VoxHeadphones;
        set
        {
            if (value == _settings.VoxHeadphones) return;
            _settings.VoxHeadphones = value;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MonitorOn));
            SendMonitor();
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
        if (value && !VoxHeadphones && (MonitorPreview || MonitorOnTransmit))
            Status = "Mon is paused while VOX is on: the mic would hear it and hold VOX keyed. With headphones, turn on headphone mode in the VOX settings.";
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
            if (!_voxKeyed && !MoxOn && !TunOn && !TwoToneOn && VoxMayKey(now))
            {
                _voxKeyed = true;
                _voxKeyedMs = now;
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
