using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SimpleHPSDR.ViewModels;

/// <summary>One slice's AF trim row: the label in the slice colour (click it
/// to mute the slice), a slider (-40..20 dB) and its dB text. The
/// MainViewModel owns the collection and adds/removes rows as slices come
/// on/off; the row sends to the engine through the VM, which keeps the
/// "operator just moved it" timestamp.</summary>
public sealed partial class SliceAfRow : ObservableObject
{
    private readonly MainViewModel _vm;
    private bool _settingLocal;

    public SliceAfRow(MainViewModel vm, int index)
    {
        _vm = vm;
        Index = index;
    }

    public int Index { get; }

    public string Label => MainViewModel.SliceLabel(Index);
    public string Color => MainViewModel.SliceColor(Index);

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Text))] private double _value;

    public string Text => $"{Value:0} dB";

    /// <summary>The slice is muted: the label dims as its switch.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LabelOpacity))] private bool _muted;

    public double LabelOpacity => Muted ? 0.35 : 1.0;

    /// <summary>The slice layout changed (a two-receiver radio): label and colour again.</summary>
    public void Relabel()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Color));
    }

    /// <summary>The VM's poll or the engine's own change: show it.</summary>
    public void SetMuted(bool muted) => Muted = muted;

    [RelayCommand]
    private async Task ToggleMute() => await _vm.SetSliceMuteAsync(Index, !Muted);

    /// <summary>The engine's value (or a new slice's starting level): show it without
    /// sending it back. The VM guards with its own flag around this too.</summary>
    public void SetLocal(double db)
    {
        _settingLocal = true;
        try { Value = db; }
        finally { _settingLocal = false; }
    }

    partial void OnValueChanged(double value)
    {
        if (_settingLocal) return;
        _vm.SendSliceAf(Index, Math.Round(value));
    }
}
