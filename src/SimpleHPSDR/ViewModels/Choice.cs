using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SimpleHPSDR.ViewModels;

/// <summary>One button in a segmented row (band, mode, filter, step).</summary>
public sealed partial class Choice : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public Choice(string label, object value, Action<Choice> onSelect)
    {
        Label = label;
        Value = value;
        Select = new RelayCommand(() => onSelect(this));
    }

    public string Label { get; }
    public object Value { get; }
    public IRelayCommand Select { get; }
    /// <summary>NB and NR buttons: the settings their right-click popup shows.</summary>
    public NoisePanel? Params { get; init; }
}
