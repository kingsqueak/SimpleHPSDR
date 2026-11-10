using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>One band's antenna routing: TX port, RX port, RX aux input.</summary>
public sealed partial class AntennaRow(AntennaBandDto dto) : ObservableObject
{
    [ObservableProperty] private string _txAnt = dto.TxAnt;
    [ObservableProperty] private string _rxAnt = dto.RxAnt;
    [ObservableProperty] private string _rxAux = dto.RxAux;

    public string Band { get; } = dto.Band;
    public string Label => Band == "Xvtr" ? "Transverters" : Band;
    public AntennaBandDto Loaded { get; set; } = dto;
    public bool Changed => TxAnt != Loaded.TxAnt || RxAnt != Loaded.RxAnt || RxAux != Loaded.RxAux;
}

/// <summary>Configuration > Antennas: the engine's per-band antenna relays.</summary>
public sealed partial class SettingsViewModel
{
    public ObservableCollection<AntennaRow> Antennas { get; } = [];
    public string[] AntennaPorts { get; } = ["Ant1", "Ant2", "Ant3"];
    public ObservableCollection<string> RxAuxInputs { get; } = [];
    [ObservableProperty] private bool _hasTxRelays;
    [ObservableProperty] private bool _hasRxRelays;
    [ObservableProperty] private bool _hasRxAux;

    private void PopulateAntennas(AntennaSettingsDto a)
    {
        HasTxRelays = a.HasTxAntennaRelays;
        HasRxRelays = a.HasRxAntennaRelays;
        RxAuxInputs.Clear();
        RxAuxInputs.Add("None");
        foreach (var x in a.AvailableRxAux ?? []) RxAuxInputs.Add(x);
        HasRxAux = RxAuxInputs.Count > 1;
        Antennas.Clear();
        foreach (var b in a.Bands) Antennas.Add(Watch(new AntennaRow(b), Group.Antennas));
    }

    // One PUT per band that changed; the engine defers a relay change made
    // while keyed to the unkey.
    private async Task ApplyAntennasAsync()
    {
        foreach (var row in Antennas.Where(r => r.Changed).ToList())
        {
            var req = new AntennaSetRequest(row.Band, row.TxAnt, row.RxAnt, row.RxAux);
            await _client.SetAntennaAsync(req);
            row.Loaded = new AntennaBandDto(row.Band, row.TxAnt, row.RxAnt, row.RxAux);
        }
    }
}
