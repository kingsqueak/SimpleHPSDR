using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>The TX and RX antenna boxes beside the VFO: the current band's
/// antenna routing, the same setting as Configuration > Antennas.</summary>
public sealed partial class MainViewModel
{
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
        _antennaBand = SimpleHPSDR.Band.Find(VfoHz) is { } band ? band.Name + "m" : null;
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
