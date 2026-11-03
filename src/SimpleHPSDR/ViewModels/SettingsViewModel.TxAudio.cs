using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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

        TxLowCutHz = Math.Min(Math.Abs(s.TxFilterLowHz), Math.Abs(s.TxFilterHighHz));
        TxHighCutHz = Math.Max(Math.Abs(s.TxFilterLowHz), Math.Abs(s.TxFilterHighHz));

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

        HasCfc = s.Cfc is not null;
        CfcBands.Clear();
        if (s.Cfc is { } cfc)
        {
            CfcEnabled = cfc.Enabled;
            CfcPostEqEnabled = cfc.PostEqEnabled;
            CfcPreCompDb = cfc.PreCompDb;
            CfcPrePeqDb = cfc.PrePeqDb;
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

    private LegacyTxEqConfig BuildEq() => new(EqEnabled, Math.Round(EqPreampDb, 1),
        EqBands.Select(b => b.FreqHz).ToArray(), EqBands.Select(b => Math.Round(b.GainDb, 1)).ToArray());

    private CfcConfig BuildCfc(StateDto was) => (was.Cfc ?? CfcConfig.Default) with
    {
        Enabled = CfcEnabled,
        PostEqEnabled = CfcPostEqEnabled,
        PreCompDb = Math.Round(CfcPreCompDb, 1),
        PrePeqDb = Math.Round(CfcPrePeqDb, 1),
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

    private static (int Low, int High) CutsOf(StateDto s) =>
        (Math.Min(Math.Abs(s.TxFilterLowHz), Math.Abs(s.TxFilterHighHz)),
         Math.Max(Math.Abs(s.TxFilterLowHz), Math.Abs(s.TxFilterHighHz)));

    /// <summary>Send only the parts that changed, each as the engine's whole record.</summary>
    private async Task ApplyTxAudioAsync()
    {
        if (_txLoaded is not { } was) return;

        int lo = (int)Math.Round(TxLowCutHz), hi = (int)Math.Round(TxHighCutHz);
        if ((lo, hi) != CutsOf(was))
            await _client.SetTxFilterAsync(was.Mode, lo, hi);

        var lv = BuildLeveling(was);
        if (lv != (was.TxLeveling ?? new TxLevelingConfig()))
            await _client.SetTxLevelingAsync(lv);
        if (Math.Abs(LevelerMaxGainDb - was.LevelerMaxGainDb) > 0.05)
            await _client.SetLevelerMaxGainAsync(Math.Round(LevelerMaxGainDb, 1));

        var eqWas = was.LegacyTxEq ?? LegacyTxEqConfig.Default;
        var eq = BuildEq();
        if (eq.Enabled != eqWas.Enabled || eq.PreampDb != eqWas.PreampDb || !eq.GainsDb.SequenceEqual(eqWas.GainsDb))
            await _client.SetTxEqAsync(eq);

        if (was.Cfc is { } cfcWas)
        {
            var cfc = BuildCfc(was);
            if (cfc.Enabled != cfcWas.Enabled || cfc.PostEqEnabled != cfcWas.PostEqEnabled
                || cfc.PreCompDb != cfcWas.PreCompDb || cfc.PrePeqDb != cfcWas.PrePeqDb
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
