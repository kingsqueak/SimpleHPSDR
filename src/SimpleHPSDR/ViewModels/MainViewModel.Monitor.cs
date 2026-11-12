using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>TX monitor: hear the processed transmit audio, either as a
/// preview while receiving (no RF) or while transmitting. The buttons are
/// the operator's choice: the engine turns the preview off whenever it keys
/// (and TUNE/2-TONE also clear monitor-on-transmit), so after each unkey the
/// choice is put back.</summary>
public sealed partial class MainViewModel
{
    private bool _applyingMonitor;
    private bool _wasKeyedForMonitor;
    private Latest<double>? _monitorVolumeSender;
    private Latest<(bool Preview, bool OnTransmit)>? _monitorSender;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonitorOn))] [NotifyPropertyChangedFor(nameof(MonitorLabel))]
    private bool _monitorPreview;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonitorOn))] [NotifyPropertyChangedFor(nameof(MonitorLabel))]
    private bool _monitorOnTransmit;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MonitorPct))] [NotifyPropertyChangedFor(nameof(MonitorPctText))]
    private double _monitorVolumeDb;

    // The level as a percentage of the slider's -60..0 dB. Starts low so that
    // Mon can't howl through open speakers the first time it's pressed.
    private const double MonitorMinDb = -60, MonitorDefaultPct = 30;
    public double MonitorPct
    {
        get => (MonitorVolumeDb - MonitorMinDb) / -MonitorMinDb * 100;
        set => MonitorVolumeDb = Math.Round(MonitorMinDb + value / 100 * -MonitorMinDb);
    }
    public string MonitorPctText => $"{MonitorPct:0}%";

    /// <summary>Lit: the monitor is set and not paused by VOX.</summary>
    public bool MonitorOn => (MonitorPreview || MonitorOnTransmit) && !MonitorPausedByVox;

    /// <summary>VOX pauses the monitor unless headphone mode is on.</summary>
    private bool MonitorPausedByVox => VoxOn && !VoxHeadphones;

    // What the engine gets. VOX pauses the monitor: through speakers the mic
    // hears it, and VOX keyed itself on the monitor and then held the radio
    // keyed on that feedback (TX Mon kept it transmitting until unkeyed by hand).
    // The buttons keep the operator's choice; turning VOX off brings it back.
    // Headphone mode (in the VOX panel) lets it run: the mic can't hear headphones.
    private (bool Preview, bool OnTransmit) EngineMonitor =>
        MonitorPausedByVox ? (false, false) : (MonitorPreview, MonitorOnTransmit);
    public string MonitorLabel => (MonitorPreview, MonitorOnTransmit) switch
    {
        (true, true) => "Mon+TX",
        (false, true) => "TX Mon",
        _ => "Mon",
    };

    /// <summary>Off, Mon (preview without transmitting), TX Mon (hear yourself
    /// while transmitting), Mon+TX (both), then off.</summary>
    [RelayCommand]
    private void CycleMonitor()
    {
        var next = (MonitorPreview, MonitorOnTransmit) switch
        {
            (false, false) => (true, false),
            (true, false) => (false, true),
            (false, true) => (true, true),
            _ => (false, false),
        };
        // Both flags change together and go to the engine as one request:
        // set one at a time, the two halves were sent as separate requests
        // that could arrive out of order and leave the engine on a step
        // the button had already passed.
        bool applying = _applyingMonitor;   // a load at connect may be under way
        _applyingMonitor = true;
        try { (MonitorPreview, MonitorOnTransmit) = next; }
        finally { _applyingMonitor = applying; }
        SendMonitor();
    }

    private void WireMonitor(StationClient client)
    {
        _monitorVolumeSender = new Latest<double>(db => client.SetMonitorVolumeAsync(db));
        // One request at a time, newest wins, so quick clicks land in order.
        _monitorSender = new Latest<(bool Preview, bool OnTransmit)>(m => client.SetMonitorAsync(m.Preview, m.OnTransmit));
    }

    private async Task LoadMonitorAsync()
    {
        try
        {
            var m = await _client!.GetMonitorAsync();
            _applyingMonitor = true;
            MonitorVolumeDb = m.VolumeDb;
            if (!_settings.MonitorLevelDefaulted)
            {
                _settings.MonitorLevelDefaulted = true;
                _settings.Save();
                MonitorPct = MonitorDefaultPct;
                await _client.SetMonitorVolumeAsync(MonitorVolumeDb);
            }
            // The engine keeps the volume but starts with MON and MON TX off,
            // so put back what the operator had.
            MonitorPreview = _settings.MonitorPreview;
            MonitorOnTransmit = _settings.MonitorOnTransmit;
            var want = EngineMonitor;
            if (m.Enabled != want.Preview || m.MonitorOnTransmit != want.OnTransmit)
                await _client.SetMonitorAsync(want.Preview, want.OnTransmit);
        }
        catch (Exception ex)
        {
            Status = $"Could not read monitor state: {ex.Message}";
        }
        finally
        {
            _applyingMonitor = false;
        }
    }

    partial void OnMonitorPreviewChanged(bool value) => SendMonitor();
    partial void OnMonitorOnTransmitChanged(bool value) => SendMonitor();

    private void SendMonitor()
    {
        if (_applyingMonitor || !IsConnected) return;
        _settings.MonitorPreview = MonitorPreview;
        _settings.MonitorOnTransmit = MonitorOnTransmit;
        _settings.Save();
        _monitorSender?.Send(EngineMonitor);
    }

    /// <summary>Called on every MOX state frame.</summary>
    private void TrackKeyForMonitor()
    {
        bool keyed = IsTransmitting;
        if (_wasKeyedForMonitor && !keyed) _ = RestoreMonitorAfterUnkeyAsync();
        _wasKeyedForMonitor = keyed;
    }

    // The engine switches the monitor off just after its MOX-off edge, so
    // wait for that, then put back what the buttons say if it differs.
    private async Task RestoreMonitorAfterUnkeyAsync()
    {
        if (EngineMonitor is (false, false)) return;
        await Task.Delay(300);
        if (IsTransmitting || !IsConnected) return;
        try
        {
            var m = await _client!.GetMonitorAsync();
            var want = EngineMonitor;
            if (m.Enabled != want.Preview || m.MonitorOnTransmit != want.OnTransmit)
                await _client.SetMonitorAsync(want.Preview, want.OnTransmit);
        }
        catch (Exception ex)
        {
            Status = $"Could not restore the monitor: {ex.Message}";
        }
    }

    partial void OnMonitorVolumeDbChanged(double value)
    {
        if (!_applyingMonitor && IsConnected) _monitorVolumeSender?.Send(Math.Round(value));
    }
}
