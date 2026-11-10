using SimpleHPSDR.Engine;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Dsp;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>One slider of the 10-band TX equalizer.</summary>
public sealed partial class EqBandRow(double freqHz, double gainDb) : ObservableObject
{
    [ObservableProperty] private double _gainDb = gainDb;
    public double FreqHz { get; } = freqHz;
    public string Label => FreqHz >= 1000 ? $"{FreqHz / 1000:0.#}k" : $"{FreqHz:0}";
}

/// <summary>One band of the continuous frequency compressor.</summary>
public sealed partial class CfcBandRow(CfcBand band) : ObservableObject
{
    [ObservableProperty] private double _compLevelDb = band.CompLevelDb;
    [ObservableProperty] private double _postGainDb = band.PostGainDb;
    public double FreqHz { get; } = band.FreqHz;
    public string Label => FreqHz >= 1000 ? $"{FreqHz / 1000:0.#}k" : $"{FreqHz:0}";
    public CfcBand ToBand() => new(FreqHz, CompLevelDb, PostGainDb);
}

/// <summary>Configuration > TX Audio: the WDSP transmit audio chain.</summary>
public sealed partial class SettingsViewModel
{
    private StateDto? _txLoaded;

    // PTT release delay: the engine holds transmit for the speech still in the
    // chain by itself. This only sets the small extra wait on top, a third of
    // its value within 20 to 200 ms. At 0 the engine doesn't hold at all.
    [ObservableProperty] private double _releaseDelayMs;
    [ObservableProperty] private int _cfcFftSize = 16384;
    public int[] CfcFftSizes { get; } = [2048, 4096, 8192, 16384];

    // TX filter (SSB cuts, positive Hz)
    [ObservableProperty] private double _txLowCutHz;
    [ObservableProperty] private double _txHighCutHz;

    // Speech processor
    [ObservableProperty] private bool _compressorEnabled;
    [ObservableProperty] private double _compressorGainDb;
    [ObservableProperty] private bool _cessbEnabled;
    [ObservableProperty] private bool _cessbWide;      // 4 kHz profile instead of 3 kHz

    // Leveler and ALC
    [ObservableProperty] private bool _levelerEnabled;
    [ObservableProperty] private double _levelerMaxGainDb;
    [ObservableProperty] private double _levelerDecayMs;
    [ObservableProperty] private double _alcMaxGainDb;
    [ObservableProperty] private double _alcDecayMs;

    // Equalizer
    [ObservableProperty] private bool _eqEnabled;
    [ObservableProperty] private double _eqPreampDb;
    public ObservableCollection<EqBandRow> EqBands { get; } = [];

    // Parametric EQ: an app setting, sent to the engine as its ten EQ points.
    [ObservableProperty] private bool _parametricEqOn;
    [ObservableProperty] private double[]? _fittedHz;
    [ObservableProperty] private double[]? _fittedDb;
    [ObservableProperty] private string _fitText = "";
    public ObservableCollection<ParametricBandRow> ParametricBands { get; } = [];
    private ParametricEqSettings? _parametricOpened;

    private void PopulateParametric()
    {
        var saved = AppSettings?.ParametricEq ?? ParametricEqSettings.Default;
        _parametricOpened ??= saved;
        ParametricEqOn = saved.Enabled;
        ParametricBands.Clear();
        for (int i = 0; i < saved.Bands.Length; i++)
        {
            var row = Watch(new ParametricBandRow(i + 1, saved.Bands[i]), Group.TxAudio);
            row.PropertyChanged += (_, _) => UpdateFit();
            ParametricBands.Add(row);
        }
        UpdateFit();
    }

    private void UpdateFit()
    {
        var (hz, db, error) = ParametricEq.Fit([.. ParametricBands.Select(b => b.ToBand())]);
        (FittedHz, FittedDb) = (hz, db);
        FitText = $"The dashed line, what's actually used, is within {error:0.0} dB of yours.";
    }

    private ParametricEqSettings SnapshotParametric() =>
        new(ParametricEqOn, [.. ParametricBands.Select(b => b.ToBand())]);

    // Leaving the parametric view: keep its sound on the graphic EQ's own bands.
    partial void OnParametricEqOnChanged(bool value)
    {
        if (_populating) return;
        if (!value)
        {
            var bands = ParametricBands.Select(b => b.ToBand()).ToArray();
            var standard = LegacyTxEqConfig.Default.FrequenciesHz;
            EqBands.Clear();
            foreach (var hz in standard)
                EqBands.Add(Watch(new EqBandRow(hz, Math.Round(ParametricEq.ResponseDb(bands, hz), 1)), Group.TxAudio));
        }
        SaveParametric();
    }

    private void SaveParametric()
    {
        if (AppSettings is not { } app) return;
        app.ParametricEq = SnapshotParametric();
        app.Save();
    }


    // CFC
    [ObservableProperty] private bool _hasCfc;
    [ObservableProperty] private bool _cfcEnabled;
    [ObservableProperty] private bool _cfcPostEqEnabled;
    [ObservableProperty] private double _cfcPreCompDb;
    [ObservableProperty] private double _cfcPrePeqDb;
    public ObservableCollection<CfcBandRow> CfcBands { get; } = [];

    // Noise gate (downward expander)
    [ObservableProperty] private bool _hasDexp;
    [ObservableProperty] private bool _dexpEnabled;
    [ObservableProperty] private double _dexpThresholdDbv;
    [ObservableProperty] private double _dexpExpansionDb;
    [ObservableProperty] private double _dexpHoldMs;
    [ObservableProperty] private double _dexpReleaseMs;

    // Phase rotator
    [ObservableProperty] private bool _phrotEnabled;
    [ObservableProperty] private double _phrotCornerHz;
    [ObservableProperty] private double _phrotStages;
    [ObservableProperty] private bool _phrotReverse;

    private void PopulateTxAudio(StateDto s)
    {
        _txLoaded = s;
        ReleaseDelayMs = s.TxMoxTailDelayMs;

        (TxLowCutHz, TxHighCutHz) = TxCuts.Of(s);

        var lv = s.TxLeveling ?? new TxLevelingConfig();
        CompressorEnabled = lv.CompressorEnabled;
        CompressorGainDb = lv.CompressorGainDb;
        CessbEnabled = lv.CessbEnabled;
        CessbWide = lv.CessbBandwidthHz == 4000;
        LevelerEnabled = lv.LevelerEnabled;
        LevelerDecayMs = lv.LevelerDecayMs;
        AlcMaxGainDb = lv.AlcMaxGainDb;
        AlcDecayMs = lv.AlcDecayMs;
        LevelerMaxGainDb = s.LevelerMaxGainDb;

        var eq = s.LegacyTxEq ?? LegacyTxEqConfig.Default;
        EqEnabled = eq.Enabled;
        EqPreampDb = eq.PreampDb;
        EqBands.Clear();
        for (int i = 0; i < eq.FrequenciesHz.Length; i++)
            EqBands.Add(Watch(new EqBandRow(eq.FrequenciesHz[i], eq.GainsDb[i]), Group.TxAudio));
        PopulateParametric();

        HasCfc = s.Cfc is not null;
        CfcBands.Clear();
        if (s.Cfc is { } cfc)
        {
            CfcEnabled = cfc.Enabled;
            CfcPostEqEnabled = cfc.PostEqEnabled;
            CfcPreCompDb = cfc.PreCompDb;
            CfcPrePeqDb = cfc.PrePeqDb;
            CfcFftSize = cfc.FftSize;
            foreach (var b in cfc.Bands) CfcBands.Add(Watch(new CfcBandRow(b), Group.TxAudio));
        }

        HasDexp = s.Dexp is not null;
        if (s.Dexp is { } dx)
        {
            DexpEnabled = dx.Enabled;
            DexpThresholdDbv = dx.ThresholdDbv;
            DexpExpansionDb = dx.ExpansionDb;
            DexpHoldMs = dx.HoldMs;
            DexpReleaseMs = dx.ReleaseMs;
        }

        var pr = s.TxPhaseRotator ?? new TxPhaseRotatorConfig();
        PhrotEnabled = pr.Enabled;
        PhrotCornerHz = pr.CornerHz;
        PhrotStages = pr.Stages;
        PhrotReverse = pr.Reverse;
    }

    // The page's values laid over the engine's last records, so fields the
    // page doesn't show (CFC FFT size, noise-gate attack, ...) carry through.
    private TxLevelingConfig BuildLeveling(StateDto was) => (was.TxLeveling ?? new TxLevelingConfig()) with
    {
        CompressorEnabled = CompressorEnabled,
        CompressorGainDb = Math.Round(CompressorGainDb, 1),
        CessbEnabled = CessbEnabled,
        CessbBandwidthHz = CessbWide ? 4000 : 3000,
        LevelerEnabled = LevelerEnabled,
        LevelerDecayMs = (int)Math.Round(LevelerDecayMs),
        AlcMaxGainDb = Math.Round(AlcMaxGainDb, 1),
        AlcDecayMs = (int)Math.Round(AlcDecayMs),
    };

    private LegacyTxEqConfig BuildEq()
    {
        if (ParametricEqOn && FittedHz is { } hz && FittedDb is { } db)
            return new(EqEnabled, Math.Round(EqPreampDb, 1), hz, db);
        return new(EqEnabled, Math.Round(EqPreampDb, 1),
            EqBands.Select(b => b.FreqHz).ToArray(), EqBands.Select(b => Math.Round(b.GainDb, 1)).ToArray());
    }

    private CfcConfig BuildCfc(StateDto was) => (was.Cfc ?? CfcConfig.Default) with
    {
        Enabled = CfcEnabled,
        PostEqEnabled = CfcPostEqEnabled,
        PreCompDb = Math.Round(CfcPreCompDb, 1),
        PrePeqDb = Math.Round(CfcPrePeqDb, 1),
        FftSize = CfcFftSize,
        Bands = CfcBands.Count > 0 ? CfcBands.Select(b => b.ToBand()).ToArray() : (was.Cfc ?? CfcConfig.Default).Bands,
    };

    private DexpConfig BuildDexp(StateDto was) => (was.Dexp ?? DexpConfig.Default) with
    {
        Enabled = DexpEnabled,
        ThresholdDbv = Math.Round(DexpThresholdDbv, 1),
        ExpansionDb = Math.Round(DexpExpansionDb, 1),
        HoldMs = Math.Round(DexpHoldMs),
        ReleaseMs = Math.Round(DexpReleaseMs),
    };

    private TxPhaseRotatorConfig BuildPhaseRotator(StateDto was) => (was.TxPhaseRotator ?? new TxPhaseRotatorConfig()) with
    {
        Enabled = PhrotEnabled,
        CornerHz = (int)Math.Round(PhrotCornerHz),
        Stages = (int)Math.Round(PhrotStages),
        Reverse = PhrotReverse,
    };

    /// <summary>Set by the main window: apply the follow at once when ticked.</summary>
    public Func<Task>? FollowRxFilterNow { get; init; }

    /// <summary>TX filter follows the RX filter: the main window keeps the
    /// TX cuts equal to the transmitting slice's RX filter, and the fields
    /// here are greyed out.</summary>
    public bool TxFilterFollowsRx
    {
        get => AppSettings?.TxFilterFollowsRx ?? false;
        set
        {
            if (AppSettings is null || AppSettings.TxFilterFollowsRx == value) return;
            AppSettings.TxFilterFollowsRx = value;
            AppSettings.Save();
            OnPropertyChanged();
            if (value && FollowRxFilterNow is { } follow) _ = FollowAndShowAsync(follow);
        }
    }

    /// <summary>Config came forward: show what changed on the radio meanwhile, unless
    /// an edit here is still on its way to the engine.</summary>
    public async Task RefreshLiveAsync()
    {
        if (Loading || _pending.Count > 0 || _txLoaded is null) return;
        try
        {
            var s = await _client.GetStateAsync();
            if (_pending.Count > 0) return;
            _populating = true;
            try
            {
                PopulatePureSignal(s);
                PopulateTxAudio(s);
            }
            finally { _populating = false; }
        }
        catch (Exception) { }
    }

    private async Task FollowAndShowAsync(Func<Task> follow)
    {
        await follow();
        try
        {
            var s = await _client.GetStateAsync();
            (TxLowCutHz, TxHighCutHz) = CutsOf(s);
            if (_txLoaded is { } was) _txLoaded = was with { TxFilterLowHz = s.TxFilterLowHz, TxFilterHighHz = s.TxFilterHighHz };
        }
        catch (Exception) { }
    }

    private static (int Low, int High) CutsOf(StateDto s) => TxCuts.Of(s);

    /// <summary>Send only the parts that changed, each as the engine's whole record.</summary>
    private async Task ApplyTxAudioAsync()
    {
        if (_txLoaded is not { } was) return;

        if ((int)Math.Round(ReleaseDelayMs) != was.TxMoxTailDelayMs)
            await _client.SetTxTailDelayAsync((int)Math.Round(ReleaseDelayMs));

        int lo = (int)Math.Round(TxLowCutHz), hi = (int)Math.Round(TxHighCutHz);
        if (!TxFilterFollowsRx && (lo, hi) != CutsOf(was))
        {
            if (AppSettings is { } app)
            {
                (app.TxLowCutHz, app.TxHighCutHz) = (lo, hi);
                app.Save();
            }
            // The radio's mode may have moved on since this page was loaded.
            await _client.SetTxFilterAsync((await _client.GetStateAsync()).Mode, lo, hi);
        }

        var lv = BuildLeveling(was);
        if (lv != (was.TxLeveling ?? new TxLevelingConfig()))
            await _client.SetTxLevelingAsync(lv);
        if (Math.Abs(LevelerMaxGainDb - was.LevelerMaxGainDb) > 0.05)
            await _client.SetLevelerMaxGainAsync(Math.Round(LevelerMaxGainDb, 1));

        var eqWas = was.LegacyTxEq ?? LegacyTxEqConfig.Default;
        var eq = BuildEq();
        if (eq.Enabled != eqWas.Enabled || eq.PreampDb != eqWas.PreampDb || !eq.GainsDb.SequenceEqual(eqWas.GainsDb)
            || !eq.FrequenciesHz.SequenceEqual(eqWas.FrequenciesHz))
            await _client.SetTxEqAsync(eq);
        SaveParametric();

        if (was.Cfc is { } cfcWas)
        {
            var cfc = BuildCfc(was);
            if (cfc.Enabled != cfcWas.Enabled || cfc.PostEqEnabled != cfcWas.PostEqEnabled
                || cfc.PreCompDb != cfcWas.PreCompDb || cfc.PrePeqDb != cfcWas.PrePeqDb
                || cfc.FftSize != cfcWas.FftSize
                || !cfc.Bands.SequenceEqual(cfcWas.Bands))
                await _client.SetCfcAsync(cfc);
        }

        if (was.Dexp is { } dxWas && BuildDexp(was) is var dx && dx != dxWas)
            await _client.SetDexpAsync(dx);

        var pr = BuildPhaseRotator(was);
        if (pr != (was.TxPhaseRotator ?? new TxPhaseRotatorConfig()))
            await _client.SetPhaseRotatorAsync(pr);
        _txLoaded = await _client.GetStateAsync();
        TxProfileModified = true;
    }
}
