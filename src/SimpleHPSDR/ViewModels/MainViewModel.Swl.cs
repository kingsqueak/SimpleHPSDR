using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Stations;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>SWL: starts the receiver in SAM with a broadcast filter and opens the station
/// guide. It's a starting point, not a mode of its own: the Mode button stays free, and
/// closing the guide ends it.</summary>
public sealed partial class MainViewModel
{
    private const int SwlFilterHz = 5000;

    public const string SwlChoice = "SWL";

    private StationGuideViewModel? _guide;
    public StationGuideViewModel Guide
    {
        get
        {
            if (_guide is null)
            {
                _guide = new StationGuideViewModel(_settings, TuneToStation);
                _guide.Changed += RefreshSwlStation;
            }
            return _guide;
        }
    }

    // EiBi's sites end in coordinates, which don't help on the waterfall.
    private static readonly Regex Coordinates = new(@"\s*\d+[NS][\d'""]*-\d+[EW][\d'""]*", RegexOptions.Compiled);

    // The box over the waterfall: the station on the tuned frequency while the guide is open.
    [ObservableProperty] private bool _swlStationShown;
    [ObservableProperty] private string _swlStationName = "";
    [ObservableProperty] private string _swlStationDetail = "";
    [ObservableProperty] private string _swlStationMore = "";
    [ObservableProperty] private bool _swlHasMore;

    private void RefreshSwlStation()
    {
        var found = _guide is { IsOpen: true } guide ? guide.OnAirAt(VfoHz / 1000.0) : [];
        SwlStationShown = found.Count > 0;
        if (found.Count == 0) return;
        var b = found[0];
        var until = b.Stop % 1440 == b.Start % 1440 ? ""
            : $"until {DateTime.UtcNow.Date.AddMinutes(b.Stop).ToLocalTime():HH:mm}";
        SwlStationName = b.Station;
        SwlStationDetail = string.Join(" · ", new[] { b.Language, b.Target, Coordinates.Replace(b.Site, ""), until }.Where(s => s != ""));
        var others = found.Skip(1).Select(o => o.Station).Distinct().ToList();
        SwlHasMore = others.Count > 0;
        SwlStationMore = others.Count switch
        {
            0 => "",
            1 => $"Also listed: {others[0]}",
            _ => $"Also listed: {others[0]} and {others.Count - 1} more",
        };
    }

    /// <summary>Set by the main window: opens the guide, or brings it forward.</summary>
    public Action? ShowGuide { get; set; }

    /// <summary>A click in the guide. It only tunes: the mode stays what the listener chose.</summary>
    private void TuneToStation(double khz) => TuneTo((long)Math.Round(khz * 1000));

    private async Task SelectSwlAsync()
    {
        if (!IsConnected) return;
        if (IsTransmitting)
        {
            Status = "Unkey before changing mode.";
            return;
        }
        var hadMemory = _settings.ModeMemories.ContainsKey(MemoryKey(BandOf(VfoHz), RxMode.SAM));
        await SelectModeAsync(RxMode.SAM);
        if (!hadMemory) await ApplyFilterAsync(-SwlFilterHz, SwlFilterHz);
        ShowGuide?.Invoke();
    }
}
