using CommunityToolkit.Mvvm.ComponentModel;
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

    [ObservableProperty] private bool _monitorPreview;
    [ObservableProperty] private bool _monitorOnTransmit;
    [ObservableProperty] private double _monitorVolumeDb;

    private void WireMonitor(StationClient client) =>
        _monitorVolumeSender = new Latest<double>(db => client.SetMonitorVolumeAsync(db));

    private async Task LoadMonitorAsync()
    {
        try
        {
            var m = await _client!.GetMonitorAsync();
            _applyingMonitor = true;
            MonitorPreview = m.Enabled;
            MonitorOnTransmit = m.MonitorOnTransmit;
            MonitorVolumeDb = m.VolumeDb;
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
        _ = Try(() => _client!.SetMonitorAsync(MonitorPreview, MonitorOnTransmit));
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
        if (!MonitorPreview && !MonitorOnTransmit) return;
        await Task.Delay(300);
        if (IsTransmitting || !IsConnected) return;
        try
        {
            var m = await _client!.GetMonitorAsync();
            if (m.Enabled != MonitorPreview || m.MonitorOnTransmit != MonitorOnTransmit)
                await _client.SetMonitorAsync(MonitorPreview, MonitorOnTransmit);
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
