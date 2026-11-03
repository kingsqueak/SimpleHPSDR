using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>TX monitor: hear the processed transmit audio, either as a
/// preview while receiving (no RF) or while transmitting.</summary>
public sealed partial class MainViewModel
{
    private bool _applyingMonitor;
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

    partial void OnMonitorVolumeDbChanged(double value)
    {
        if (!_applyingMonitor && IsConnected) _monitorVolumeSender?.Send(Math.Round(value));
    }
}
