using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>One setting in an NB or NR right-click popup: a slider, a tick
/// box, or a list of options. Reads and writes its field of the engine's
/// NrConfig.</summary>
public sealed partial class NoiseParam : ObservableObject
{
    private bool _loading;

    public required string Label { get; init; }
    public required Func<NrConfig, double> Read { get; init; }
    public required Func<NrConfig, double, NrConfig> Write { get; init; }
    public required double Default { get; init; }
    public double Min { get; init; }
    public double Max { get; init; } = 100;
    public double Step { get; init; } = 1;
    public string Format { get; init; } = "0";
    public string Unit { get; init; } = "";
    public bool IsToggle { get; init; }
    public string[]? Options { get; init; }

    public bool IsOptions => Options is not null;
    public bool IsSlider => !IsToggle && !IsOptions;
    /// <summary>Set once the operator changes it, so untouched fields stay as the engine has them.</summary>
    public bool Touched { get; private set; }
    public Action? Changed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    [NotifyPropertyChangedFor(nameof(On))]
    [NotifyPropertyChangedFor(nameof(Option))]
    private double _value;

    public string Text => $"{Value.ToString(Format)}{Unit}";
    public bool On { get => Value != 0; set => Value = value ? 1 : 0; }
    public int Option { get => (int)Value; set { if (value >= 0 && value < (Options?.Length ?? int.MaxValue)) Value = value; } }

    public void Load(NrConfig nr)
    {
        _loading = true;
        Value = Read(nr);
        _loading = false;
        Touched = false;
    }

    partial void OnValueChanged(double value)
    {
        if (_loading) return;
        Touched = true;
        Changed?.Invoke();
    }
}

/// <summary>The settings shown when an NB or NR button is right-clicked.</summary>
public sealed partial class NoisePanel(string title, string note, params NoiseParam[] rows)
{
    public string Title { get; } = title;
    public string Note { get; } = note;
    public bool HasNote => Note.Length > 0;
    public bool HasRows => Rows.Count > 0;
    public ObservableCollection<NoiseParam> Rows { get; } = [.. rows];

    [RelayCommand]
    private void Reset()
    {
        foreach (var r in Rows) r.Value = r.Default;
    }
}
