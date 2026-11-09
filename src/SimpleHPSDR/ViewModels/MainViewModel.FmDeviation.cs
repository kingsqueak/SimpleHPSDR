using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// While transmitting FM, the meter row's ALC slot shows the peak deviation instead
/// (DEV 4.6k): on FM that's what the other station hears as loudness, and it's what
/// to set the mic gain against. The engine measures the peak since its last read,
/// so we read it five times a second and show the highest of the last second.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] private string _txLevelLabel = "ALC";
    [ObservableProperty] private string _txLevelTip = "How much the ALC is turning the transmit audio down.";

    private DispatcherTimer? _fmDevTimer;
    private readonly double[] _fmDevRecent = new double[5];
    private int _fmDevSlot;
    private bool _fmDevReading;
    private bool FmDeviationShown => _fmDevTimer is { IsEnabled: true };

    /// <summary>On every key change.</summary>
    private void TrackFmDeviation()
    {
        bool want = IsTransmitting && Mode == RxMode.FM && IsConnected;
        if (want == FmDeviationShown) return;
        if (want)
        {
            Array.Clear(_fmDevRecent);
            _fmDevTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
                async (_, _) => await ReadFmDeviationAsync());
            _fmDevTimer.Start();
            TxLevelLabel = "DEV";
            TxLevelTip = "Peak FM deviation over the last second. Full deviation is 5 kHz (2.5 kHz narrow).";
            AlcText = "—";
        }
        else
        {
            _fmDevTimer?.Stop();
            TxLevelLabel = "ALC";
            TxLevelTip = "How much the ALC is turning the transmit audio down.";
        }
    }

    private async Task ReadFmDeviationAsync()
    {
        if (_fmDevReading || _client is not { } client) return;
        _fmDevReading = true;
        try
        {
            var s = await client.GetFmStatusAsync();
            if (!FmDeviationShown) return;
            if (!s.DeviationMeterAvailable) { AlcText = "—"; return; }
            _fmDevRecent[_fmDevSlot++ % _fmDevRecent.Length] = s.TxPeakDeviationHz;
            AlcText = $"{_fmDevRecent.Max() / 1000:0.0}k";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
        finally { _fmDevReading = false; }
    }
}
