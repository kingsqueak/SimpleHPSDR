using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>The TX and RX antenna boxes beside the VFO: the current band's
/// antenna routing, the same setting as Configuration > Antennas.</summary>
public sealed partial class MainViewModel
{
    /// <summary>One button for both: "ANT1", or "TX1 RX2" when they differ,
    /// plus "B:Rx2 jack" while slice B listens on the Rx2 jack.</summary>
    public string AntennaText => (TxAntLabel == RxAntLabel ? TxAntLabel
        : $"TX{TxAntLabel.Replace("ANT", "")} RX{RxAntLabel.Replace("ANT", "")}")
        + (SliceBOnJack ? " B:Rx2 jack" : "");
    public bool AntennasDiffer => TxAntLabel != RxAntLabel || SliceBOnJack;
    partial void OnTxAntLabelChanged(string value) => NotifyAntennaText();
    partial void OnRxAntLabelChanged(string value) => NotifyAntennaText();
    partial void OnSliceBEnabledChanged(bool value) => NotifyAntennaText();
    partial void OnHasRx2JackChanged(bool value) => NotifyAntennaText();

    private void NotifyAntennaText()
    {
        OnPropertyChanged(nameof(AntennaText));
        OnPropertyChanged(nameof(AntennasDiffer));
    }

    // ---- Slice B's input: the main antenna (ADC0) or the Rx2 jack (ADC1) ----
    // Both slices hear the main antenna by default. On a Protocol 2 radio with
    // two ADCs the second one has its own jack (Rx2 on the back panel), and
    // slice B can listen there instead, e.g. on a receive-only antenna. The
    // engine forgets the choice on reconnect, so the app keeps it and puts it
    // back whenever the engine's slice B differs.

    /// <summary>The connected radio has an Rx2 jack slice B can use.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowSliceBInput))]
    private bool _hasRx2Jack;
    public bool ShowSliceBInput => HasRx2Jack && SliceBEnabled;
    private bool SliceBOnJack => ShowSliceBInput && _settings.SliceBOnRx2Jack;
    public string SliceBInputLabel => _settings.SliceBOnRx2Jack ? "Rx2 jack" : "Main";
    public ObservableCollection<Choice> SliceBInputs { get; } = [];
    private bool _sendingSliceBInput;

    private async Task LoadRx2JackAsync()
    {
        bool has = false;
        if (_radio is { Protocol2: true })
            try { has = (await _client!.GetCapabilitiesAsync()).RxAdcCount > 1; }
            catch (Exception) { }
        HasRx2Jack = has;
        if (SliceBInputs.Count == 0)
        {
            SliceBInputs.Add(new Choice("Main antenna", false, _ => SetSliceBInput(false)));
            SliceBInputs.Add(new Choice("Rx2 jack", true, _ => SetSliceBInput(true)));
        }
        Mark(SliceBInputs, _settings.SliceBOnRx2Jack);
        SyncSliceBInput();
    }

    private void SetSliceBInput(bool jack)
    {
        _settings.SliceBOnRx2Jack = jack;
        _settings.Save();
        Mark(SliceBInputs, jack);
        OnPropertyChanged(nameof(SliceBInputLabel));
        NotifyAntennaText();
        SyncSliceBInput();
        CheckAttSource();
    }

    // ---- ATT follows the active slice's input ----
    // While slice B is the active slice and listens on the Rx2 jack, the ATT
    // and Pre controls set the second ADC's attenuator; otherwise the main
    // input's, as before. The engine's auto ATT already protects each ADC.

    /// <summary>ATT is acting on the Rx2 jack (ADC1).</summary>
    public bool AttOnRx2Jack => ActiveRx == 1 && SliceBOnJack;
    private bool _attWasRx2;

    private void CheckAttSource()
    {
        bool rx2 = AttOnRx2Jack;
        if (rx2 == _attWasRx2) return;
        _attWasRx2 = rx2;
        NotifyLna();
        _ = rx2 ? ShowRx2JackAttAsync(baseline: true) : ShowMainAttAsync();
    }

    /// <summary>Show ADC1's attenuation, auto-ATT offset and overload. The
    /// baseline only when switching to it, so a poll can't undo a click.</summary>
    private async Task ShowRx2JackAttAsync(bool baseline = false)
    {
        if (!IsConnected) return;
        AdcProtectionStatusDto p;
        try { p = await _client!.GetAdcStatusAsync(); }
        catch (Exception) { return; }
        if (!AttOnRx2Jack || p.PhysicalAdcs?.FirstOrDefault(a => a.PhysicalAdcSource == 1) is not { } adc) return;
        _applyingState = true;
        try
        {
            if (baseline) AttenDb = adc.BaselineDb;
            AttOffsetDb = adc.OffsetDb;
            AdcOverload = adc.Warning;
        }
        finally { _applyingState = false; }
        NotifyAttText();
    }

    private async Task ShowMainAttAsync()
    {
        if (!IsConnected) return;
        StateDto s;
        try { s = await _client!.GetStateAsync(); }
        catch (Exception) { return; }
        if (AttOnRx2Jack) return;
        _applyingState = true;
        try
        {
            AttenDb = s.AttenDb;
            AttOffsetDb = s.AttOffsetDb;
            AdcOverload = s.AdcOverloadWarning;
        }
        finally { _applyingState = false; }
        NotifyAttText();
    }

    private void NotifyAttText()
    {
        OnPropertyChanged(nameof(AttenText));
        OnPropertyChanged(nameof(PreLabel));
        OnPropertyChanged(nameof(RfText));
    }

    /// <summary>Tell the engine slice B's input if it has a different one.</summary>
    private void SyncSliceBInput()
    {
        if (!IsConnected || !HasRx2Jack || !SliceBEnabled || _sliceB is not { } b || _sendingSliceBInput) return;
        byte want = _settings.SliceBOnRx2Jack ? (byte)1 : (byte)0;
        if (b.AdcSource == want) return;
        _ = SendSliceBInputAsync(want);
    }

    private async Task SendSliceBInputAsync(byte adc)
    {
        _sendingSliceBInput = true;
        try { await Try(() => _client!.SetReceiverAsync(1, new ReceiverSetRequest(AdcSource: adc))); }
        finally { _sendingSliceBInput = false; }
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
        await LoadRx2JackAsync();
        await LoadLnaAsync();
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
