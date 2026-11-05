using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Stations;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>SWL: starts the receiver in SAM with a broadcast filter and opens the station
/// guide. SAM is only a starting point: the Mode button stays free. SWL stays on, with its
/// button beside Mode and the station box on the waterfall, until it's turned off there.
/// The guide's window can be closed and reopened meanwhile.</summary>
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
    [ObservableProperty] private bool _swlActive;
    [ObservableProperty] private bool _swlStationShown;
    [ObservableProperty] private string _swlStationName = "";
    [ObservableProperty] private string _swlStationDetail = "";
    [ObservableProperty] private string _swlStationMore = "";
    [ObservableProperty] private bool _swlHasMore;

    private void RefreshSwlStation()
    {
        var found = SwlActive && _guide is { } guide ? guide.OnAirAt(VfoHz / 1000.0) : [];
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
    /// <summary>Set by the main window: closes the guide if it's open.</summary>
    public Action? CloseGuide { get; set; }

    [RelayCommand] private void OpenGuide() => ShowGuide?.Invoke();

    [RelayCommand]
    private void TurnSwlOff()
    {
        SwlActive = false;
        Guide.Stop();
        CloseGuide?.Invoke();
    }

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
        SwlActive = true;
        Guide.Start();
        RefreshSwlStation();
        ShowGuide?.Invoke();
    }
}
