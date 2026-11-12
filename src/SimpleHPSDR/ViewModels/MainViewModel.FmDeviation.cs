using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Contracts;

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
    [ObservableProperty] private string _txLevelTip = AlcTip;

    // The ALC/DEV bar: value and held peak as fractions of the scale, and where it
    // turns orange and red. ALC reads 0-20 dB of gain reduction, orange from 10 dB,
    // red from 15. DEV reads to 125% of full deviation, orange from 90%, red over 100%.
    private const double AlcScaleDb = 20;
    private const double DevScale = 1.25;
    private const string AlcTip = "How much the ALC is turning the transmit audio down: 0 to 20 dB. "
        + "Green is fine, orange from 10 dB, red from 15 dB means the mic gain is too high. White mark: peak, held 2 s.";
    private const string DevTip = "Peak FM deviation over the last second. Full deviation is 5 kHz (2.5 kHz narrow). "
        + "Orange near full deviation, red over it.";
    [ObservableProperty] private double _txLevel;
    [ObservableProperty] private double _txLevelPeak;
    [ObservableProperty] private double _txLevelWarnAt = 10 / AlcScaleDb;
    [ObservableProperty] private double _txLevelOverAt = 15 / AlcScaleDb;
    private long _txLevelPeakAt;
    private const long TxLevelPeakHoldMs = 2000;

    private void ShowTxLevel(double fraction)
    {
        TxLevel = fraction;
        long now = Environment.TickCount64;
        if (fraction >= TxLevelPeak || now - _txLevelPeakAt > TxLevelPeakHoldMs)
        {
            TxLevelPeak = fraction;
            _txLevelPeakAt = now;
        }
    }

    private void ClearTxLevel() => (TxLevel, TxLevelPeak) = (0, 0);

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
            TxLevelTip = DevTip;
            (TxLevelWarnAt, TxLevelOverAt) = (0.9 / DevScale, 1 / DevScale);
            AlcText = "—";
            ClearTxLevel();
        }
        else
        {
            _fmDevTimer?.Stop();
            TxLevelLabel = "ALC";
            TxLevelTip = AlcTip;
            (TxLevelWarnAt, TxLevelOverAt) = (10 / AlcScaleDb, 15 / AlcScaleDb);
            ClearTxLevel();
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
            if (!s.DeviationMeterAvailable) { AlcText = "—"; ClearTxLevel(); return; }
            _fmDevRecent[_fmDevSlot++ % _fmDevRecent.Length] = s.TxPeakDeviationHz;
            double dev = _fmDevRecent.Max();
            AlcText = $"{dev / 1000:0.0}k";
            // The text already holds the peak of the last second, so the bar does too.
            double full = FmNarrow ? 2500 : 5000;
            TxLevel = TxLevelPeak = Math.Clamp(dev / full / DevScale, 0, 1);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
        finally { _fmDevReading = false; }
    }
}
