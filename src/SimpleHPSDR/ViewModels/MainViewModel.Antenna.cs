using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>The TX and RX antenna boxes beside the VFO: the current band's
/// antenna routing, the same setting as Configuration > Antennas.</summary>
public sealed partial class MainViewModel
{
    /// <summary>One button for both: "ANT1", or "TX1 RX2" when they differ,
    /// plus "A2:Rx2 jack" beside each on-slice listening on the Rx2 jack.</summary>
    public string AntennaText
    {
        get
        {
            string text = TxAntLabel == RxAntLabel ? TxAntLabel
                : $"TX{TxAntLabel.Replace("ANT", "")} RX{RxAntLabel.Replace("ANT", "")}";
            var jack = JackSlices();
            return jack.Count == 0 ? text
                : text + " " + string.Join(" ", jack.Select(SliceLabel)) + ":Rx2 jack";
        }
    }
    public bool AntennasDiffer => TxAntLabel != RxAntLabel || JackSlices().Count > 0;
    partial void OnTxAntLabelChanged(string value) => NotifyAntennaText();
    partial void OnRxAntLabelChanged(string value) => NotifyAntennaText();
    partial void OnHasRx2JackChanged(bool value) => NotifyAntennaText();

    /// <summary>The on-slices (past A1) listening on the Rx2 jack.</summary>
    private List<int> JackSlices() =>
        [.. Enumerable.Range(1, MaxSlices - 1).Where(i => ShowSliceInput(i) && WantsJack(i))];

    private void NotifyAntennaText()
    {
        OnPropertyChanged(nameof(AntennaText));
        OnPropertyChanged(nameof(AntennasDiffer));
        OnPropertyChanged(nameof(ShowB1Input));
        OnPropertyChanged(nameof(B1InputLabel));
    }

    // ---- Slice inputs: the main antenna (ADC0) or the Rx2 jack (ADC1) ----
    // Every slice hears the main antenna by default. On a Protocol 2 radio with
    // two ADCs the second one has its own jack (Rx2 on the back panel), and any
    // slice past A1 can listen there instead, e.g. on a receive-only antenna.
    // The engine forgets the choice on reconnect, so the app keeps it and puts
    // it back whenever the engine's slice differs. Only B1's choice is saved:
    // it is RX2's, the setting that predates per-slice inputs.

    /// <summary>The connected radio has an Rx2 jack the slices can use.</summary>
    [ObservableProperty]
    private bool _hasRx2Jack;

    /// <summary>Each slice's wanted input: false = main antenna, true = Rx2
    /// jack. Slice 0 (A1) is always the main antenna.</summary>
    private readonly bool[] _wantsJack = new bool[MaxSlices];

    /// <summary>Slice i listens on the Rx2 jack (ADC1) rather than the main
    /// antenna.</summary>
    internal bool WantsJack(int i) => i > 0 && i < MaxSlices && _wantsJack[i];

    /// <summary>Slice i's input button shows: the radio has the jack and the
    /// slice is on. A1 has no button; it is always the main antenna.</summary>
    public bool ShowSliceInput(int i) => HasRx2Jack && SliceOn(i) && i > 0;
    public string SliceInputLabel(int i) => WantsJack(i) ? "Rx2 jack" : "Main";

    /// <summary>The desktop view's B1 button.</summary>
    public bool ShowB1Input => ShowSliceInput(2);
    public string B1InputLabel => SliceInputLabel(2);
    private bool _sendingSliceInput;

    private async Task LoadRx2JackAsync()
    {
        bool has = false;
        if (_radio is { Protocol2: true })
            try { has = (await _client!.GetCapabilitiesAsync()).RxAdcCount > 1; }
            catch (Exception) { }
        HasRx2Jack = has;
        // B1 is RX2's slice: its wanted input is the saved choice.
        _wantsJack[2] = _settings.SliceBOnRx2Jack;
        NotifyAntennaText();
        SyncSliceInputs();
    }

    /// <summary>Flip a slice's input between the main antenna and the Rx2 jack.</summary>
    public void ToggleSliceInput(int i)
    {
        if (i <= 0 || i >= MaxSlices) return;
        _wantsJack[i] = !_wantsJack[i];
        if (i == 2)
        {
            _settings.SliceBOnRx2Jack = _wantsJack[i];
            _settings.Save();
        }
        if (SliceOn(i)) _ = SendSliceInputAsync(i, WantsJack(i) ? (byte)1 : (byte)0);
        CheckAttSource();
        NotifyAntennaText();
    }

    // ---- ATT follows the active slice's input ----
    // While the active slice listens on the Rx2 jack, the ATT and Pre controls
    // set the second ADC's attenuator; otherwise the main input's, as before.
    // The engine's auto ATT already protects each ADC.

    /// <summary>ATT is acting on the Rx2 jack (ADC1).</summary>
    public bool AttOnRx2Jack => ActiveRx > 0 && WantsJack(ActiveRx);
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

    /// <summary>Give each on-slice the input it wants when the engine has a
    /// different one.</summary>
    private void SyncSliceInputs()
    {
        if (!IsConnected || !HasRx2Jack || _sendingSliceInput) return;
        for (int i = 1; i < MaxSlices; i++)
        {
            if (!SliceOn(i) || _slices[i] is not { } r) continue;
            byte want = WantsJack(i) ? (byte)1 : (byte)0;
            if (r.AdcSource == want) continue;
            _ = SendSliceInputAsync(i, want);
        }
    }

    private async Task SendSliceInputAsync(int rx, byte adc)
    {
        _sendingSliceInput = true;
        try { await Try(() => _client!.SetReceiverAsync(rx, new ReceiverSetRequest(AdcSource: adc))); }
        finally { _sendingSliceInput = false; }
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
