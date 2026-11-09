using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

public partial class MainViewModel
{
    /// <summary>The band strip along the top of the spectrum, from the band plan
    /// chosen in Config > Station. Empty while it's turned off or not yet loaded.</summary>
    public IReadOnlyList<StripSpan> BandStrip { get; private set; } = [];

    /// <summary>Config > Station: how solid the strip's colours are.</summary>
    public double BandStripOpacity => _settings.BandStripOpacity;

    /// <summary>On connect, and when the band plan or the strip setting changes.</summary>
    private async Task LoadBandStripAsync()
    {
        if (!_settings.BandStrip || _client is not { } client)
        {
            BandStrip = [];
            return;
        }
        try
        {
            var plan = await client.GetBandPlanAsync();
            BandStrip = SimpleHPSDR.BandStrip.Build(plan.RegionId, plan.Segments);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException) { }
    }
}
