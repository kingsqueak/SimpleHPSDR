using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// One of the five filter buttons: an engine filter slot (F1-F5) for the
/// current mode. Left-click selects it; right-click edits its label and edges,
/// which the engine stores per mode. In lower-sideband modes the edges are
/// shown and typed as positive cut frequencies and stored negated.
/// </summary>
public sealed partial class FilterSlot : ObservableObject
{
    private readonly Func<FilterSlot, Task> _select;
    private readonly Func<FilterSlot, Task> _save;
    private readonly Func<FilterSlot, Task> _reset;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _editLabel = "";
    [ObservableProperty] private double _editLowHz;
    [ObservableProperty] private double _editHighHz;

    public FilterSlot(RxMode mode, FilterPreset preset,
        Func<FilterSlot, Task> select, Func<FilterSlot, Task> save, Func<FilterSlot, Task> reset)
    {
        Mode = mode;
        Preset = preset;
        (_select, _save, _reset) = (select, save, reset);
    }

    public RxMode Mode { get; }
    public FilterPreset Preset { get; }
    public string Label => Preset.Label;
    public string SlotName => Preset.SlotName;
    /// <summary>FreeDV below 10 MHz (60 m excepted) runs on the lower sideband.</summary>
    public bool FreeDvLower { get; init; }
    public bool LowerSideband => Mode is RxMode.LSB or RxMode.CWL or RxMode.DIGL
        || (Mode is RxMode.FreeDv && FreeDvLower);
    public string EdgeHint => LowerSideband
        ? "Hz below the carrier (lower sideband)"
        : Mode is RxMode.AM or RxMode.SAM or RxMode.DSB or RxMode.FM
            ? "Hz from the carrier, negative = below"
            : "Hz above the carrier";

    /// <summary>Edges as the operator sees them.</summary>
    public (int Low, int High) DisplayEdges => LowerSideband
        ? (-Preset.HighHz, -Preset.LowHz)
        : (Preset.LowHz, Preset.HighHz);

    /// <summary>Signed edges for the engine from the edited values.</summary>
    public (int Low, int High) EngineEdges
    {
        get
        {
            int lo = (int)Math.Round(EditLowHz), hi = (int)Math.Round(EditHighHz);
            if (hi < lo) (lo, hi) = (hi, lo);
            return LowerSideband ? (-hi, -lo) : (lo, hi);
        }
    }

    /// <summary>Load the editor fields from the stored preset (on opening the editor).</summary>
    public void BeginEdit()
    {
        EditLabel = Preset.Label;
        (EditLowHz, EditHighHz) = DisplayEdges;
    }

    [RelayCommand] private Task Select() => _select(this);
    [RelayCommand] private Task Save() => _save(this);
    [RelayCommand] private Task Reset() => _reset(this);
}
