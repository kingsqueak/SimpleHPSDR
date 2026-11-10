using SimpleHPSDR.Engine;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// Transverter bands, as in Thetis: while transverters are on, each enabled band in
/// Config > Station > Transverters gets a band button after 6 m, and the VFO, typed
/// frequencies and the digit wheel reach its RF range. The VFO shows the RF frequency
/// (144.200.000); the engine sets the radio to the IF.
/// </summary>
public sealed partial class MainViewModel
{
    private async Task LoadTransvertersAsync()
    {
        try { ShowTransverterBands(await _client!.GetTransverterAsync()); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
    }

    private void ShowTransverterBands(TransverterSettingsDto t)
    {
        for (int i = Bands.Count - 1; i >= 0; i--)
            if (Bands[i].Value is Band { Transverter: true }) Bands.RemoveAt(i);
        foreach (var b in SimpleHPSDR.Band.SetTransverters(t))
            Bands.Add(new Choice(b.Label, b, c => _ = SelectBandAsync((Band)c.Value)));
        SetVfoLocal(VfoHz);
    }
}
