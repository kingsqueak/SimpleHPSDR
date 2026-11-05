using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Dsp;

namespace SimpleHPSDR.ViewModels;

/// <summary>One band of the parametric TX EQ. Bands 1 and 6 can also be a
/// pass filter or a shelf.</summary>
public sealed partial class ParametricBandRow : ObservableObject
{
    private readonly EqBandType[] _types;
    private bool _settingType;

    public int Number { get; }
    public string[] TypeNames { get; }
    public bool HasTypes => _types.Length > 1;

    [ObservableProperty] private bool _on;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Type), nameof(HasGain), nameof(ShapeLabel), nameof(ShapeMin),
        nameof(ShapeMax), nameof(ShapeStep), nameof(ShapeFormat))]
    private int _typeIndex;
    [ObservableProperty] private double _hz;
    [ObservableProperty] private double _gainDb;
    [ObservableProperty] private double _shape;

    public ParametricBandRow(int number, ParametricBand band)
    {
        Number = number;
        (_types, TypeNames) = number switch
        {
            1 => (new[] { EqBandType.Peak, EqBandType.HighPass, EqBandType.LowShelf },
                  new[] { "Peak", "High-pass", "Low shelf" }),
            6 => (new[] { EqBandType.Peak, EqBandType.LowPass, EqBandType.HighShelf },
                  new[] { "Peak", "Low-pass", "High shelf" }),
            _ => (new[] { EqBandType.Peak }, new[] { "Peak" }),
        };
        _on = band.On;
        _typeIndex = Math.Max(0, Array.IndexOf(_types, band.Type));
        _hz = band.Hz;
        _gainDb = band.GainDb;
        _shape = band.Shape;
    }

    public EqBandType Type => _types[Math.Clamp(TypeIndex, 0, _types.Length - 1)];
    private bool IsPass => Type is EqBandType.HighPass or EqBandType.LowPass;
    public bool HasGain => !IsPass;
    public string ShapeLabel => IsPass ? "dB/oct" : "Q";
    public double ShapeMin => IsPass ? 12 : 0.3;
    public double ShapeMax => IsPass ? 48 : Type == EqBandType.Peak ? 10 : 2;
    public double ShapeStep => IsPass ? 12 : 0.1;
    public string ShapeFormat => IsPass ? "0" : "0.0";

    partial void OnTypeIndexChanged(int value)
    {
        if (_settingType) return;
        _settingType = true;
        Shape = IsPass ? 24 : Type == EqBandType.Peak ? 1 : 0.7;
        _settingType = false;
    }

    public ParametricBand ToBand() => new(On, Type, Math.Round(Hz), Math.Round(GainDb, 1), Math.Round(Shape, 1));
}
