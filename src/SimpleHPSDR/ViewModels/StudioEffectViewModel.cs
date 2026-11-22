using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Contracts;
using SimpleHPSDR.Dsp;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// One parameter of a SimpleStudio effect, built from its catalogue entry.
/// A Log parameter's slider runs 0 to 100 along a log scale; the number box
/// beside it always shows the real value.
/// </summary>
public sealed class StudioParamRow : ObservableObject
{
    public const double LogSliderMax = 100;

    private double _value;

    public StudioParamRow(StudioParamInfo info, double value, string? label = null)
    {
        Info = info;
        Label = label ?? info.Label;
        (Format, Decimals, Step) = Precision(info);
        _value = Math.Round(info.Valid(value), Decimals);
    }

    public StudioParamInfo Info { get; }
    public string Name => Info.Name;
    public string Label { get; }
    public double Min => Info.Min;
    public double Max => Info.Max;
    public double Default => Info.Default;
    public string Format { get; }
    public int Decimals { get; }
    public double Step { get; }
    public IReadOnlyList<string> Options => Info.Options ?? [];

    public bool IsLog => Info.Kind == StudioParamKind.Log;
    public bool IsToggle => Info.Kind == StudioParamKind.Toggle;
    public bool IsChoice => Info.Kind == StudioParamKind.Choice;
    public bool IsSlider => Info.Kind is StudioParamKind.Linear or StudioParamKind.Log;

    /// <summary>The unit as shown after the number.</summary>
    public string UnitText => Info.Unit switch { ":1" or "1:" => ": 1", var u => u };

    /// <summary>Raised on every change of value (not on construction).</summary>
    public event Action<StudioParamRow>? Changed;

    public double Value
    {
        get => _value;
        set
        {
            double v = Math.Round(Info.Valid(value), Decimals);
            if (v == _value) return;
            _value = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SliderValue));
            OnPropertyChanged(nameof(IsOn));
            OnPropertyChanged(nameof(ChoiceIndex));
            Changed?.Invoke(this);
        }
    }

    public double SliderMin => IsLog ? 0 : Min;
    public double SliderMax => IsLog ? LogSliderMax : Max;

    public double SliderValue
    {
        get => IsLog ? LogSliderMax * Math.Log(_value / Min) / Math.Log(Max / Min) : _value;
        set => Value = IsLog ? Min * Math.Pow(Max / Min, Math.Clamp(value, 0, LogSliderMax) / LogSliderMax) : value;
    }

    public bool IsOn
    {
        get => _value >= 0.5;
        set => Value = value ? 1 : 0;
    }

    public int ChoiceIndex
    {
        get => (int)Math.Round(_value);
        set { if (value >= 0) Value = value; }
    }

    public void Reset() => Value = Default;

    // How finely each kind of value is shown and stepped.
    private static (string Format, int Decimals, double Step) Precision(StudioParamInfo p)
    {
        if (p.Kind is StudioParamKind.Toggle or StudioParamKind.Choice) return ("0", 0, 1);
        return p.Unit switch
        {
            "Hz" => ("0", 0, 10),
            "dB" => ("0.0", 1, 0.5),
            "%" => ("0", 0, 1),
            "LUFS" => ("0", 0, 1),
            "s" => ("0.0", 1, 0.1),
            ":1" or "1:" => ("0.0", 1, 0.1),
            "ms" when p.Min < 1 => ("0.0", 1, 0.5),
            "ms" when p.Kind == StudioParamKind.Log => ("0", 0, 10),
            "ms" => ("0", 0, 5),
            _ when p.Max - p.Min <= 20 => ("0.00#", 3, 0.05),
            _ => ("0.0", 1, 0.5),
        };
    }
}

/// <summary>A small heading and the parameter rows under it.</summary>
public sealed partial class StudioParamGroup(string title, IReadOnlyList<StudioParamRow> rows, string? note = null)
    : ObservableObject
{
    public string Title { get; } = title;
    public IReadOnlyList<StudioParamRow> Rows { get; } = rows;
    public string? Note { get; } = note;
    public bool HasNote => !string.IsNullOrEmpty(Note);
    [ObservableProperty] private bool _isVisible = true;
}

/// <summary>One column of the parametric EQ's band grid.</summary>
public sealed class StudioEqBandColumn : ObservableObject
{
    public StudioEqBandColumn(int number, StudioParamRow on, StudioParamRow type, StudioParamRow freq,
        StudioParamRow gain, StudioParamRow q)
    {
        (Number, On, Type, Freq, Gain, Q) = (number, on, type, freq, gain, q);
        type.Changed += _ => OnPropertyChanged(nameof(HasGain));
    }

    public int Number { get; }
    public string Title => $"Band {Number}";
    public StudioParamRow On { get; }
    public StudioParamRow Type { get; }
    public StudioParamRow Freq { get; }
    public StudioParamRow Gain { get; }
    public StudioParamRow Q { get; }
    /// <summary>The cuts have no gain.</summary>
    public bool HasGain => Type.ChoiceIndex is StudioEq.Bell or StudioEq.LowShelf or StudioEq.HighShelf;

    public StudioEqBand ToBand() => new(On.IsOn, Type.ChoiceIndex, Freq.Value, Gain.Value, Q.Value);
}

/// <summary>One column of the multiband compressor's band grid.</summary>
public sealed partial class StudioMbBandColumn(int number, string title, StudioParamRow on, StudioParamRow solo,
    StudioParamRow threshold, StudioParamRow ratio, StudioParamRow attack, StudioParamRow release,
    StudioParamRow makeup) : ObservableObject
{
    public int Number { get; } = number;
    public string Title { get; } = title;
    public StudioParamRow On { get; } = on;
    public StudioParamRow Solo { get; } = solo;
    public StudioParamRow Threshold { get; } = threshold;
    public StudioParamRow Ratio { get; } = ratio;
    public StudioParamRow Attack { get; } = attack;
    public StudioParamRow Release { get; } = release;
    public StudioParamRow Makeup { get; } = makeup;
    [ObservableProperty] private double _reductionDb;
    public string ReductionText => $"{ReductionDb:0.0} dB";
    partial void OnReductionDbChanged(double value) => OnPropertyChanged(nameof(ReductionText));
}

/// <summary>A numbered point on the EQ curve.</summary>
public readonly record struct StudioEqMarker(int Number, double Hz, double Db, bool On);

/// <summary>
/// One effect in the SimpleStudio chain: its parameters (from the catalogue),
/// how its panel groups them, and its live meters.
/// </summary>
public sealed partial class StudioEffectViewModel : ObservableObject
{
    /// <summary>Bottom of the level bars.</summary>
    public const double FloorDb = -60;

    /// <summary>Frequencies the EQ curve is drawn at.</summary>
    public static readonly double[] CurveHz = StudioEq.Grid(241);

    private readonly Dictionary<string, StudioParamRow> _byName = new(StringComparer.Ordinal);
    private readonly StudioParamRow? _advanced;

    public StudioEffectViewModel(StudioEffectInfo info, string id, bool enabled, IReadOnlyDictionary<string, double>? values)
    {
        Info = info;
        Id = id;
        _enabled = enabled;
        var v = info.Values(values);
        var rows = new List<StudioParamRow>(info.Params.Count);
        for (int i = 0; i < info.Params.Count; i++)
        {
            var row = new StudioParamRow(info.Params[i], v[i], ShortLabel(info.Params[i]));
            row.Changed += OnParamChanged;
            rows.Add(row);
            _byName[row.Name] = row;
        }
        Params = rows;
        _advanced = Type == StudioCatalogue.Compressor ? Row("advanced") : null;

        if (IsEq)
        {
            var bands = new List<StudioEqBandColumn>();
            for (int b = 1; b <= StudioCatalogue.EqBands; b++)
                if (Row($"b{b}On") is { } on && Row($"b{b}Type") is { } type && Row($"b{b}Freq") is { } freq
                    && Row($"b{b}Gain") is { } gain && Row($"b{b}Q") is { } q)
                    bands.Add(new StudioEqBandColumn(b, on, type, freq, gain, q));
            EqBands = bands;
            UpdateCurve();
        }
        if (IsMultiband)
        {
            string[] titles = ["Low", "Low-mid", "High-mid", "High"];
            var bands = new List<StudioMbBandColumn>();
            for (int b = 1; b <= StudioCatalogue.MultibandBands; b++)
                if (Row($"b{b}On") is { } on && Row($"b{b}Solo") is { } solo && Row($"b{b}Threshold") is { } th
                    && Row($"b{b}Ratio") is { } ratio && Row($"b{b}Attack") is { } at && Row($"b{b}Release") is { } rel
                    && Row($"b{b}Makeup") is { } mk)
                    bands.Add(new StudioMbBandColumn(b, b <= titles.Length ? titles[b - 1] : $"Band {b}",
                        on, solo, th, ratio, at, rel, mk));
            MbBands = bands;
        }
        Groups = BuildGroups();
        UpdateModeGroups();
        ClearMeter();
    }

    public StudioEffectInfo Info { get; }
    public string Id { get; }
    public string Type => Info.Type;
    public string Label => Info.Label;
    public IReadOnlyList<StudioParamRow> Params { get; }
    public IReadOnlyList<StudioParamGroup> Groups { get; }
    public IReadOnlyList<StudioEqBandColumn> EqBands { get; } = [];
    public IReadOnlyList<StudioMbBandColumn> MbBands { get; } = [];

    public bool IsEq => Type == StudioCatalogue.Eq;
    public bool IsMultiband => Type == StudioCatalogue.Multiband;
    public bool IsCompressor => Type == StudioCatalogue.Compressor;
    public bool IsLeveler => Type == StudioCatalogue.Leveler;
    public bool HasGroups => Groups.Count > 0;

    /// <summary>Has a gain-reduction reading (the tile's thin bar, the GR meter).</summary>
    public bool HasReduction => Type is StudioCatalogue.Gate or StudioCatalogue.Expander or StudioCatalogue.DeEsser
        or StudioCatalogue.Limiter or StudioCatalogue.Compressor or StudioCatalogue.Multiband;

    /// <summary>The GR bar's full scale: gates and expanders cut much further.</summary>
    public double ReductionScaleDb => Type is StudioCatalogue.Gate or StudioCatalogue.Expander ? 40 : 24;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _canMoveLeft;
    [ObservableProperty] private bool _canMoveRight;

    /// <summary>Any edit: a parameter or the on/off box.</summary>
    public event Action? Changed;

    /// <summary>Set by the chain: what the tile's buttons do.</summary>
    internal Action<StudioEffectViewModel, int>? MoveBy { get; set; }
    internal Action<StudioEffectViewModel>? RemoveMe { get; set; }
    internal Action<StudioEffectViewModel>? SelectMe { get; set; }

    [RelayCommand] private void MoveLeft() => MoveBy?.Invoke(this, -1);
    [RelayCommand] private void MoveRight() => MoveBy?.Invoke(this, +1);
    [RelayCommand] private void Remove() => RemoveMe?.Invoke(this);
    [RelayCommand] private void Select() => SelectMe?.Invoke(this);

    partial void OnEnabledChanged(bool value) => Changed?.Invoke();

    public StudioParamRow? Row(string name) => _byName.GetValueOrDefault(name);

    public StudioEffectConfig ToConfig() =>
        new(Id, Type, Enabled, Params.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal));

    private void OnParamChanged(StudioParamRow row)
    {
        if (row == _advanced) UpdateModeGroups();
        if (IsEq) UpdateCurve();
        if (row.Name == "threshold" && !_meterActive) UpdateThresholdLine(null);
        Changed?.Invoke();
    }

    // ---- compressor: one knob, or the full controls ----

    public bool IsOneKnob
    {
        get => _advanced is { IsOn: false };
        set { if (_advanced is { } a && value) a.IsOn = false; }
    }

    public bool IsFullControls
    {
        get => _advanced is { IsOn: true };
        set { if (_advanced is { } a && value) a.IsOn = true; }
    }

    [RelayCommand] private void UseOneKnob() => IsOneKnob = true;
    [RelayCommand] private void UseFullControls() => IsFullControls = true;

    private void UpdateModeGroups()
    {
        if (_advanced is null) return;
        bool full = _advanced.IsOn;
        foreach (var g in Groups) g.IsVisible = g.Title == AmountTitle ? !full : full;
        OnPropertyChanged(nameof(IsOneKnob));
        OnPropertyChanged(nameof(IsFullControls));
    }

    // ---- grouping, as Patch Prime's panels ----

    private const string AmountTitle = "Amount";

    private static readonly Dictionary<string, string> ToggleText = new(StringComparer.Ordinal)
    {
        ["listen"] = "Listen to only what the de-esser hears (to find the right frequency)",
        ["truePeak"] = "True peak: also catch peaks between samples",
    };

    private static string? ShortLabel(StudioParamInfo p) => ToggleText.GetValueOrDefault(p.Name);

    private List<StudioParamGroup> BuildGroups()
    {
        (string Title, string[] Names, string? Note)[] layout = Type switch
        {
            StudioCatalogue.Gate => [("Detection", ["threshold", "range"], null), ("Timing", ["attack", "hold", "release"], null)],
            StudioCatalogue.Expander => [("Detection", ["threshold", "ratio", "range"], null), ("Timing", ["attack", "release"], null)],
            StudioCatalogue.Compressor =>
            [
                (AmountTitle, ["amount"], "0 % does nothing, 40 % is a light touch for speech, 100 % is heavy, radio-style."),
                ("Detection", ["threshold", "ratio"], null),
                ("Timing", ["attack", "release"], null),
                ("Output", ["makeup"], null),
            ],
            StudioCatalogue.DeEsser => [("Detection", ["frequency", "threshold"], null), ("Reduction", ["range"], null), ("Listen", ["listen"], null)],
            StudioCatalogue.Limiter => [("Limit", ["ceiling", "release"], null), ("True peak", ["truePeak"], null)],
            StudioCatalogue.Leveler => [("Loudness", ["target", "freeze"], null), ("Limits", ["maxBoost", "maxCut"], null), ("Speed", ["speed"], null)],
            StudioCatalogue.Reverb => [("Space", ["size", "decay", "predelay"], null), ("Tone", ["damping"], null), ("Blend", ["mix"], null)],
            StudioCatalogue.Gain => [("Gain", ["gainDb"], null)],
            StudioCatalogue.Multiband => [("Crossovers", ["x1", "x2", "x3"], null)],
            StudioCatalogue.Eq => [],
            _ => [],
        };
        var used = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<StudioParamGroup>();
        foreach (var (title, names, note) in layout)
        {
            var rows = names.Select(Row).OfType<StudioParamRow>().ToList();
            foreach (var r in rows) used.Add(r.Name);
            if (rows.Count > 0) groups.Add(new StudioParamGroup(title, rows, note));
        }
        // Anything the layout doesn't place (a newer catalogue) still gets a row.
        bool Placed(StudioParamRow r) => used.Contains(r.Name) || r == _advanced
            || ((IsEq || IsMultiband) && r.Name.Length > 1 && r.Name[0] == 'b' && char.IsDigit(r.Name[1]));
        var rest = Params.Where(r => !Placed(r)).ToList();
        if (rest.Count > 0) groups.Add(new StudioParamGroup(groups.Count == 0 ? "Settings" : "More", rest));
        return groups;
    }

    // ---- the EQ curve ----

    [ObservableProperty] private double[] _curveDb = [];
    [ObservableProperty] private IReadOnlyList<StudioEqMarker> _markers = [];

    private void UpdateCurve()
    {
        var bands = EqBands.Select(b => b.ToBand()).ToArray();
        CurveDb = StudioEq.ResponseDb(bands, CurveHz);
        Markers = bands.Select((b, i) => new StudioEqMarker(i + 1, b.Hz,
            b.Type is StudioEq.LowCut or StudioEq.HighCut ? 0 : b.GainDb, b.On)).ToList();
    }

    // ---- meters ----

    private bool _meterActive;
    [ObservableProperty] private double _inPeakDb;
    [ObservableProperty] private double _inRmsDb;
    [ObservableProperty] private double _outPeakDb;
    [ObservableProperty] private double _outRmsDb;
    [ObservableProperty] private double _reductionDb;
    [ObservableProperty] private double _levelerGainDb;
    [ObservableProperty] private double _loudnessLufs;
    /// <summary>The threshold drawn across the IN bar; NaN for none.</summary>
    [ObservableProperty] private double _thresholdLineDb = double.NaN;
    [ObservableProperty] private string _inText = "";
    [ObservableProperty] private string _outText = "";
    [ObservableProperty] private string _reductionText = "";
    [ObservableProperty] private string _levelerText = "";

    /// <summary>The tile's thin bar, 0 to 1 of <see cref="ReductionScaleDb"/>.</summary>
    public double TileReduction => HasReduction ? Math.Clamp(ReductionDb / ReductionScaleDb, 0, 1) : 0;
    partial void OnReductionDbChanged(double value) => OnPropertyChanged(nameof(TileReduction));

    public void ClearMeter() => ApplyMeter(null, false);

    /// <summary>Show one reading; nothing (empty bars) when the chain had no audio.</summary>
    public void ApplyMeter(StudioEffectMeterDto? m, bool active)
    {
        _meterActive = active && m is not null;
        if (!_meterActive || m is null)
        {
            InPeakDb = InRmsDb = OutPeakDb = OutRmsDb = FloorDb - 1;
            ReductionDb = 0;
            LevelerGainDb = 0;
            LoudnessLufs = -70;
            foreach (var b in MbBands) b.ReductionDb = 0;
            InText = OutText = ReductionText = LevelerText = "–";
            UpdateThresholdLine(null);
            return;
        }
        InPeakDb = m.InPeakDb;
        InRmsDb = m.InRmsDb;
        OutPeakDb = m.OutPeakDb;
        OutRmsDb = m.OutRmsDb;
        var values = m.Values ?? [];
        if (IsMultiband)
        {
            double most = 0;
            for (int b = 0; b < MbBands.Count; b++)
            {
                double gr = b < values.Count ? Math.Max(0, values[b]) : 0;
                MbBands[b].ReductionDb = gr;
                most = Math.Max(most, gr);
            }
            ReductionDb = most;
        }
        else if (HasReduction)
            ReductionDb = values.Count > 0 ? Math.Max(0, values[0]) : 0;
        if (IsLeveler)
        {
            LevelerGainDb = values.Count > 0 ? values[0] : 0;
            LoudnessLufs = values.Count > 1 ? values[1] : -70;
            LevelerText = $"{LevelerGainDb:+0.0;-0.0;0.0} dB\n{LoudnessLufs:0.0} LUFS";
        }
        InText = Readout(InPeakDb, InRmsDb);
        OutText = Readout(OutPeakDb, OutRmsDb);
        ReductionText = $"{ReductionDb:0.0} dB";
        UpdateThresholdLine(values);
    }

    private static string Readout(double peak, double rms) =>
        $"{Math.Max(peak, -99):0} peak\n{Math.Max(rms, -99):0} RMS";

    // The compressor reports the threshold its one knob sets; the others
    // draw their own threshold setting.
    private void UpdateThresholdLine(IReadOnlyList<double>? values)
    {
        if (IsCompressor)
            ThresholdLineDb = values is { Count: > 1 } ? values[1]
                : _advanced is { IsOn: true } && Row("threshold") is { } t ? t.Value : double.NaN;
        else
            ThresholdLineDb = Row("threshold")?.Value ?? double.NaN;
    }

    /// <summary>The same effect, values and all (comparing configs from the engine).</summary>
    public static bool Same(StudioEffectConfig a, StudioEffectConfig b)
    {
        if (a.Id != b.Id || a.Type != b.Type || a.Enabled != b.Enabled) return false;
        var pa = a.Params ?? new Dictionary<string, double>();
        var pb = b.Params ?? new Dictionary<string, double>();
        if (pa.Count != pb.Count) return false;
        foreach (var (k, v) in pa)
            if (!pb.TryGetValue(k, out double w) || Math.Abs(v - w) > 1e-6) return false;
        return true;
    }
}
