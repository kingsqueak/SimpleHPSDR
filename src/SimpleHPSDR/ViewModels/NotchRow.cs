using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>One manual notch in the ANF right-click panel.</summary>
public sealed partial class NotchRow : ObservableObject
{
    private static readonly int[] WidthsHz = [50, 100, 200, 400];
    public static string[] WidthNames { get; } = [.. WidthsHz.Select(w => $"{w} Hz")];

    private readonly Func<NotchRow, double, Task> _setWidth;

    public NotchDto Notch { get; set; }
    public string Frequency => $"{Notch.CenterHz / 1e6:0.000000} MHz";
    public IAsyncRelayCommand RemoveCommand { get; }

    [ObservableProperty] private int _widthIndex;

    public NotchRow(NotchDto notch, Func<NotchRow, double, Task> setWidth, Func<NotchRow, Task> remove)
    {
        Notch = notch;
        _setWidth = setWidth;
        _widthIndex = Array.IndexOf(WidthsHz, (int)notch.WidthHz);
        RemoveCommand = new AsyncRelayCommand(() => remove(this));
    }

    partial void OnWidthIndexChanged(int value)
    {
        if (value >= 0) _ = _setWidth(this, WidthsHz[value]);
    }
}
