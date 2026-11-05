using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// Config > PA: putting the power right with an external meter. The radio's own
/// meter can be several dB out, and calibration trusts it. From one reading,
/// the gap between what was asked for and the external meter shifts every
/// band's PA gain, and the gap between the radio's meter and the external one
/// trims the PWR display. No keying here: the readings are typed in afterwards.
/// </summary>
public sealed partial class SettingsViewModel
{
    private static readonly string[] HfBands =
        ["160m", "80m", "60m", "40m", "30m", "20m", "17m", "15m", "12m", "10m", "6m"];
    private const double MaxTrimDb = 12;

    [ObservableProperty] private double _trimAskedWatts = 10;
    [ObservableProperty] private double _trimMeterWatts;
    [ObservableProperty] private double _trimExternalWatts;
    [ObservableProperty] private string _paTrimStatus = "";

    /// <summary>Back to the engine's stock table for this radio. Changing a
    /// gain also makes the engine drop its calibration curves for that band.</summary>
    [RelayCommand]
    private async Task ResetPaToStockAsync()
    {
        try
        {
            var stock = (await _client.GetPaDefaultsAsync()).Bands.ToDictionary(b => b.Band);
            foreach (var row in PaBands)
                if (stock.TryGetValue(row.Band, out var b)) row.PaGainDb = Math.Round(b.PaGainDb, 1);
            if (AppSettings is { } app)
            {
                app.PowerMeterTrimDb = 0;
                app.Save();
            }
            PaTrimStatus = "PA gains are back to stock, and the PWR display trim is cleared.";
        }
        catch (Exception ex) { PaTrimStatus = $"Could not read the stock gains: {ex.Message}"; }
    }

    [RelayCommand]
    private void ApplyPaTrim()
    {
        if (TrimAskedWatts <= 0 || TrimMeterWatts <= 0 || TrimExternalWatts <= 0)
        {
            PaTrimStatus = "Enter all three readings first.";
            return;
        }
        double shift = 10 * Math.Log10(TrimExternalWatts / TrimAskedWatts);
        double meter = 10 * Math.Log10(TrimExternalWatts / TrimMeterWatts);
        if (Math.Abs(shift) > MaxTrimDb || Math.Abs(meter) > MaxTrimDb)
        {
            PaTrimStatus = $"That's more than {MaxTrimDb:0} dB out, so nothing was changed. Check the readings.";
            return;
        }
        foreach (var row in PaBands.Where(r => HfBands.Contains(r.Band)))
            row.PaGainDb = Math.Round(row.PaGainDb + shift, 1);
        if (AppSettings is { } app)
        {
            app.PowerMeterTrimDb = Math.Round(app.PowerMeterTrimDb + meter, 1);
            app.Save();
            PaTrimStatus = $"HF bands shifted {shift:+0.0;-0.0;0.0} dB. PWR display trim is now {app.PowerMeterTrimDb:+0.0;-0.0;0.0} dB.";
        }
        (TrimMeterWatts, TrimExternalWatts) = (0, 0);
    }
}
