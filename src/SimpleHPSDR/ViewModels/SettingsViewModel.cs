using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>One band's PA calibration. Everything the window doesn't edit
/// (open-collector outputs) rides along in <see cref="Original"/>.</summary>
public sealed partial class PaBandRow(PaBandSettingsDto original) : ObservableObject
{
    [ObservableProperty] private double _paGainDb = Math.Round(original.PaGainDb, 1);
    [ObservableProperty] private bool _disablePa = original.DisablePa;

    // Open-collector outputs (pins 1-7 = bits 0-6): asserted on receive, on
    // transmit, and added on top of the TX pins while TUNE is on.
    [ObservableProperty] private byte _ocTx = original.OcTx;
    [ObservableProperty] private byte _ocRx = original.OcRx;
    [ObservableProperty] private byte _ocTune = original.OcTune;

    public PaBandSettingsDto Original { get; } = original;
    public string Band => Original.Band;
    public PaBandSettingsDto ToDto() => Original with
    {
        PaGainDb = PaGainDb, DisablePa = DisablePa, OcTx = OcTx, OcRx = OcRx, OcTune = OcTune,
    };

    private static bool Bit(byte mask, int pin) => (mask & (1 << (pin - 1))) != 0;
    private static byte Set(byte mask, int pin, bool on) =>
        (byte)(on ? mask | (1 << (pin - 1)) : mask & ~(1 << (pin - 1)));

    // One property per checkbox (pins 1-7 for TX, RX and Tune).
    public bool OcTx1 { get => Bit(OcTx, 1); set => OcTx = Set(OcTx, 1, value); }
    public bool OcTx2 { get => Bit(OcTx, 2); set => OcTx = Set(OcTx, 2, value); }
    public bool OcTx3 { get => Bit(OcTx, 3); set => OcTx = Set(OcTx, 3, value); }
    public bool OcTx4 { get => Bit(OcTx, 4); set => OcTx = Set(OcTx, 4, value); }
    public bool OcTx5 { get => Bit(OcTx, 5); set => OcTx = Set(OcTx, 5, value); }
    public bool OcTx6 { get => Bit(OcTx, 6); set => OcTx = Set(OcTx, 6, value); }
    public bool OcTx7 { get => Bit(OcTx, 7); set => OcTx = Set(OcTx, 7, value); }
    public bool OcRx1 { get => Bit(OcRx, 1); set => OcRx = Set(OcRx, 1, value); }
    public bool OcRx2 { get => Bit(OcRx, 2); set => OcRx = Set(OcRx, 2, value); }
    public bool OcRx3 { get => Bit(OcRx, 3); set => OcRx = Set(OcRx, 3, value); }
    public bool OcRx4 { get => Bit(OcRx, 4); set => OcRx = Set(OcRx, 4, value); }
    public bool OcRx5 { get => Bit(OcRx, 5); set => OcRx = Set(OcRx, 5, value); }
    public bool OcRx6 { get => Bit(OcRx, 6); set => OcRx = Set(OcRx, 6, value); }
    public bool OcRx7 { get => Bit(OcRx, 7); set => OcRx = Set(OcRx, 7, value); }
    public bool OcTune1 { get => Bit(OcTune, 1); set => OcTune = Set(OcTune, 1, value); }
    public bool OcTune2 { get => Bit(OcTune, 2); set => OcTune = Set(OcTune, 2, value); }
    public bool OcTune3 { get => Bit(OcTune, 3); set => OcTune = Set(OcTune, 3, value); }
    public bool OcTune4 { get => Bit(OcTune, 4); set => OcTune = Set(OcTune, 4, value); }
    public bool OcTune5 { get => Bit(OcTune, 5); set => OcTune = Set(OcTune, 5, value); }
    public bool OcTune6 { get => Bit(OcTune, 6); set => OcTune = Set(OcTune, 6, value); }
    public bool OcTune7 { get => Bit(OcTune, 7); set => OcTune = Set(OcTune, 7, value); }

    partial void OnOcTxChanged(byte value) => NotifyPins("Tx");
    partial void OnOcRxChanged(byte value) => NotifyPins("Rx");
    partial void OnOcTuneChanged(byte value) => NotifyPins("Tune");

    private void NotifyPins(string kind)
    {
        for (int i = 1; i <= 7; i++) OnPropertyChanged($"Oc{kind}{i}");
    }
}

/// <summary>
/// The Configuration window. Changes apply live: each edit schedules its
/// group (PA, audio, PureSignal, TX audio) to be sent a quarter second later,
/// so a slider drag or a run of arrow clicks becomes one engine call. Each
/// group sends only what differs from what the engine last had. "Undo
/// changes" puts everything back to how it was when the window opened.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly string[] EditableBands =
        ["160m", "80m", "60m", "40m", "30m", "20m", "17m", "15m", "12m", "10m", "6m"];
    private static readonly TimeSpan ApplyDelay = TimeSpan.FromMilliseconds(250);

    private enum Group { Pa, Audio, PureSignal, TxAudio, Antennas }

    private static readonly Dictionary<string, Group> GroupOf = new()
    {
        [nameof(PaEnabled)] = Group.Pa, [nameof(PaMaxPowerWatts)] = Group.Pa,

        [nameof(SelectedOutput)] = Group.Audio, [nameof(SelectedInput)] = Group.Audio,
        [nameof(SelectedChannel)] = Group.Audio, [nameof(SelectedTxSource)] = Group.Audio,
        [nameof(MicBoost)] = Group.Audio, [nameof(MicBias)] = Group.Audio, [nameof(LineInGain)] = Group.Audio,

        [nameof(PsExternalFeedback)] = Group.PureSignal, [nameof(PsAutoAttenuate)] = Group.PureSignal,
        [nameof(PsFeedbackAttenDb)] = Group.PureSignal, [nameof(PsMoxDelaySec)] = Group.PureSignal,
        [nameof(PsLoopDelaySec)] = Group.PureSignal, [nameof(PsAmpDelayNs)] = Group.PureSignal,
        [nameof(PsHwPeak)] = Group.PureSignal, [nameof(PsMonitor)] = Group.PureSignal,

        [nameof(ReleaseDelayMs)] = Group.TxAudio, [nameof(CfcFftSize)] = Group.TxAudio,
        [nameof(TxLowCutHz)] = Group.TxAudio, [nameof(TxHighCutHz)] = Group.TxAudio,
        [nameof(CompressorEnabled)] = Group.TxAudio, [nameof(CompressorGainDb)] = Group.TxAudio,
        [nameof(CessbEnabled)] = Group.TxAudio, [nameof(CessbWide)] = Group.TxAudio,
        [nameof(LevelerEnabled)] = Group.TxAudio, [nameof(LevelerMaxGainDb)] = Group.TxAudio,
        [nameof(LevelerDecayMs)] = Group.TxAudio, [nameof(AlcMaxGainDb)] = Group.TxAudio,
        [nameof(AlcDecayMs)] = Group.TxAudio, [nameof(EqEnabled)] = Group.TxAudio,
        [nameof(EqPreampDb)] = Group.TxAudio, [nameof(CfcEnabled)] = Group.TxAudio,
        [nameof(CfcPostEqEnabled)] = Group.TxAudio, [nameof(CfcPreCompDb)] = Group.TxAudio,
        [nameof(CfcPrePeqDb)] = Group.TxAudio, [nameof(DexpEnabled)] = Group.TxAudio,
        [nameof(DexpThresholdDbv)] = Group.TxAudio, [nameof(DexpExpansionDb)] = Group.TxAudio,
        [nameof(DexpHoldMs)] = Group.TxAudio, [nameof(DexpReleaseMs)] = Group.TxAudio,
        [nameof(PhrotEnabled)] = Group.TxAudio, [nameof(PhrotCornerHz)] = Group.TxAudio,
        [nameof(PhrotStages)] = Group.TxAudio, [nameof(PhrotReverse)] = Group.TxAudio,
    };

    private readonly StationClient _client;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly Dictionary<Group, CancellationTokenSource> _pending = [];
    private bool _populating;
    private PaSettingsDto? _loaded;

    // As the engine had them when the window opened, for "Undo changes".
    private PaSettingsDto? _origPa;
    private AudioDevices? _origDevices;
    private AudioFrontEndDto? _origRadioAudio;
    private StateDto? _origState;
    private AntennaSettingsDto? _origAntennas;

    [ObservableProperty] private bool _paEnabled;
    [ObservableProperty] private double _paMaxPowerWatts;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _loading = true;

    public ObservableCollection<PaBandRow> PaBands { get; } = [];
    public event Action<int>? PaMaxPowerSaved;

    public SettingsViewModel(StationClient client) => _client = client;

    public async Task LoadAsync()
    {
        Loading = true;
        try
        {
            var pa = await _client.GetPaSettingsAsync();
            var devices = await _client.GetAudioDevicesAsync();
            var radio = await _client.GetRadioAudioAsync();
            var state = await _client.GetStateAsync();
            var antennas = await _client.GetAntennasAsync();
            (_origPa, _origDevices, _origRadioAudio, _origState, _origAntennas) = (pa, devices, radio, state, antennas);

            Populate(pa, devices, radio, state, antennas);
            await RefreshAudioStatusAsync(devices);
            Status = "Changes apply as you make them.";
        }
        catch (Exception ex)
        {
            Status = $"Could not read settings: {ex.Message}";
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>Fill every page without sending anything back.</summary>
    private void Populate(PaSettingsDto pa, AudioDevices devices, AudioFrontEndDto radio, StateDto state,
        AntennaSettingsDto antennas)
    {
        _populating = true;
        try
        {
            PopulatePa(pa);
            PopulateAudio(devices, radio);
            PopulatePureSignal(state);
            PopulateTxAudio(state);
            PopulateTxProfiles();
            PopulateAntennas(antennas);
        }
        finally
        {
            _populating = false;
        }
    }

    private void PopulatePa(PaSettingsDto pa)
    {
        _loaded = pa;
        PaEnabled = pa.Global.PaEnabled;
        PaMaxPowerWatts = pa.Global.PaMaxPowerWatts;
        PaBands.Clear();
        foreach (var b in pa.Bands.Where(b => EditableBands.Contains(b.Band)))
            PaBands.Add(Watch(new PaBandRow(b), Group.Pa));
    }

    /// <summary>Rows (PA bands, EQ and CFC bands) report their own edits.</summary>
    private T Watch<T>(T row, Group group) where T : INotifyPropertyChanged
    {
        row.PropertyChanged += (_, _) => { if (!_populating) Schedule(group); };
        return row;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_populating && e.PropertyName is { } name && GroupOf.TryGetValue(name, out var group))
            Schedule(group);
    }

    private void Schedule(Group group)
    {
        if (_pending.Remove(group, out var old)) old.Cancel();
        var cts = _pending[group] = new CancellationTokenSource();
        _ = ApplyLaterAsync(group, cts.Token);
    }

    private async Task ApplyLaterAsync(Group group, CancellationToken ct)
    {
        try { await Task.Delay(ApplyDelay, ct); }
        catch (OperationCanceledException) { return; }
        await ApplyAsync(group);
    }

    // One group at a time, so engine calls never interleave.
    private async Task ApplyAsync(Group group)
    {
        await _applyGate.WaitAsync();
        try
        {
            switch (group)
            {
                case Group.Pa: await ApplyPaAsync(); break;
                case Group.Audio: await ApplyAudioAsync(); break;
                case Group.PureSignal: await ApplyPureSignalAsync(); break;
                case Group.TxAudio: await ApplyTxAudioAsync(); break;
                case Group.Antennas: await ApplyAntennasAsync(); break;
            }
            Status = "Applied.";
        }
        catch (Exception ex)
        {
            Status = $"Not applied: {ex.Message}";
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private async Task ApplyPaAsync()
    {
        if (_loaded is not { } was) return;
        var edited = PaBands.ToDictionary(r => r.Band, r => r.ToDto());
        // Send every band back, including transverter entries we don't show.
        var bands = was.Bands.Select(b => edited.GetValueOrDefault(b.Band, b)).ToList();
        var global = was.Global with { PaEnabled = PaEnabled, PaMaxPowerWatts = (int)Math.Round(PaMaxPowerWatts) };
        if (global == was.Global && bands.SequenceEqual(was.Bands)) return;
        await _client.SetPaSettingsAsync(new PaSettingsSetRequest(global, bands));
        _loaded = new PaSettingsDto(global, bands);
        PaMaxPowerSaved?.Invoke(global.PaMaxPowerWatts);
    }

    /// <summary>Put every page back to how the engine had it when the window
    /// opened, and send whatever differs now.</summary>
    [RelayCommand]
    private async Task UndoAsync()
    {
        if (_origPa is null || _origDevices is null || _origRadioAudio is null || _origState is null
            || _origAntennas is null) return;
        foreach (var cts in _pending.Values) cts.Cancel();
        _pending.Clear();

        // Populate from the originals, but keep comparing against what the
        // engine has now, so only real differences are sent.
        var (pa, devices, radio, ps, tx) = (_loaded, _devicesWas, _radioAudio, _psLoaded, _txLoaded);
        var antennasNow = Antennas.ToDictionary(r => r.Band, r => r.Loaded);
        Populate(_origPa, _origDevices, _origRadioAudio, _origState, _origAntennas);
        (_loaded, _devicesWas, _radioAudio, _psLoaded, _txLoaded) = (pa, devices, radio, ps, tx);
        // Compare restored antenna rows against what the engine has now.
        foreach (var row in Antennas)
            if (antennasNow.TryGetValue(row.Band, out var now)) row.Loaded = now;

        foreach (var group in Enum.GetValues<Group>())
            await ApplyAsync(group);
        Status = "Changes undone.";
    }
}
