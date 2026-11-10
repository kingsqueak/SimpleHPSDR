using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>The TX and RX antenna boxes beside the VFO: the current band's
/// antenna routing, the same setting as Configuration > Antennas.</summary>
public sealed partial class MainViewModel
{
    /// <summary>One button for both: "ANT1", or "TX1 RX2" when they differ.</summary>
    public string AntennaText => TxAntLabel == RxAntLabel ? TxAntLabel
        : $"TX{TxAntLabel.Replace("ANT", "")} RX{RxAntLabel.Replace("ANT", "")}";
    public bool AntennasDiffer => TxAntLabel != RxAntLabel;
    partial void OnTxAntLabelChanged(string value) => NotifyAntennaText();
    partial void OnRxAntLabelChanged(string value) => NotifyAntennaText();

    private void NotifyAntennaText()
    {
        OnPropertyChanged(nameof(AntennaText));
        OnPropertyChanged(nameof(AntennasDiffer));
    }

    private static readonly string[] AntennaPorts = ["Ant1", "Ant2", "Ant3"];
    private readonly Dictionary<string, AntennaBandDto> _antennas = [];
    private string? _antennaBand;

    public ObservableCollection<Choice> TxAntennas { get; } = [];
    public ObservableCollection<Choice> RxAntennas { get; } = [];
    [ObservableProperty] private string _txAntLabel = "—";
    [ObservableProperty] private string _rxAntLabel = "—";
    [ObservableProperty] private bool _antennaBoxesEnabled;
    [ObservableProperty] private bool _hasTxRelays;
    [ObservableProperty] private bool _hasRxRelays;

    private static string PortLabel(string port) => port.ToUpperInvariant();

    private async Task LoadAntennasAsync()
    {
        try
        {
            var a = await _client!.GetAntennasAsync();
            HasTxRelays = a.HasTxAntennaRelays;
            HasRxRelays = a.HasRxAntennaRelays;
            _antennas.Clear();
            foreach (var b in a.Bands) _antennas[b.Band] = b;
        }
        catch (Exception) { }
        if (TxAntennas.Count == 0)
            foreach (var p in AntennaPorts)
            {
                TxAntennas.Add(new Choice(PortLabel(p), p, c => _ = SetAntennaAsync(tx: (string)c.Value)));
                RxAntennas.Add(new Choice(PortLabel(p), p, c => _ = SetAntennaAsync(rx: (string)c.Value)));
            }
        ShowAntennasForBand();
    }

    /// <summary>Show the routing for the band the VFO is in.</summary>
    private void ShowAntennasForBand()
    {
        // Transverter bands share the engine's one Xvtr antenna route.
        _antennaBand = SimpleHPSDR.Band.Find(VfoHz) is { } band ? band.Transverter ? "Xvtr" : band.Name + "m" : null;
        if (_antennaBand is null || !_antennas.TryGetValue(_antennaBand, out var a))
        {
            (TxAntLabel, RxAntLabel, AntennaBoxesEnabled) = ("—", "—", false);
            return;
        }
        AntennaBoxesEnabled = IsConnected || _antennas.Count > 0;
        TxAntLabel = PortLabel(a.TxAnt);
        RxAntLabel = PortLabel(a.RxAnt);
        Mark(TxAntennas, a.TxAnt);
        Mark(RxAntennas, a.RxAnt);
    }

    private async Task SetAntennaAsync(string? tx = null, string? rx = null)
    {
        if (!IsConnected || _antennaBand is not { } band || !_antennas.TryGetValue(band, out var a)) return;
        var req = new AntennaSetRequest(band, tx ?? a.TxAnt, rx ?? a.RxAnt, a.RxAux);
        await Try(async () =>
        {
            await _client!.SetAntennaAsync(req);
            _antennas[band] = new AntennaBandDto(band, req.TxAnt, req.RxAnt, req.RxAux);
            ShowAntennasForBand();
        });
    }
}

public sealed partial class MainViewModel
{
    /// <summary>A fresh antenna analyser, on the band and antenna in use.</summary>
    /// <summary>A wideband view of the connected radio.</summary>
    public WidebandViewModel NewWideband() => new(() => IsConnected ? _client : null, () => VfoHz);

    public AnalyzerViewModel NewAnalyzer() =>
        new(() => IsConnected ? _client : null, VfoHz, TxAntLabel is "—" or "" ? "ANT1" : TxAntLabel);
}
