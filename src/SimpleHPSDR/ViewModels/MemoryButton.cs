using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>One of the 16 Mem buttons: "Open" when empty, otherwise the
/// frequency over the mode. Click recalls it, right-click (long press) stores.</summary>
public sealed partial class MemoryButton : ObservableObject
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Top))] [NotifyPropertyChangedFor(nameof(Bottom))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))] [NotifyPropertyChangedFor(nameof(Tip))]
    private MemorySlot? _slot;

    public MemoryButton(int index, Func<MemoryButton, Task> recall, Action<MemoryButton> store)
    {
        Index = index;
        Recall = new AsyncRelayCommand(() => recall(this));
        Store = new RelayCommand(() => store(this));
    }

    /// <summary>"Open" is drawn fainter than a stored memory.</summary>
    public static readonly Avalonia.Data.Converters.FuncValueConverter<bool, double> EmptyOpacity = new(e => e ? 0.55 : 1.0);

    public int Index { get; }
    public IAsyncRelayCommand Recall { get; }
    public IRelayCommand Store { get; }
    public bool IsEmpty => Slot is null;
    public string Top => Slot is { } s ? FormatMHz(s.Hz) : "Open";
    public string Bottom => Slot is { } s ? ((RxMode)s.Mode).ToString() : "";
    public string Tip => Slot is null
        ? $"Memory {Index + 1} is open. Right-click (or long-press) to store the frequency and mode you're on."
        : $"Memory {Index + 1}: {FormatMHz(Slot.Hz)} MHz {Bottom}. Click to tune; right-click (or long-press) to store what you're on instead.";

    /// <summary>MHz with the kHz always shown and finer digits only when set: 7.255, 14.0745.</summary>
    public static string FormatMHz(long hz) =>
        (hz / 1e6).ToString("0.000###", System.Globalization.CultureInfo.InvariantCulture);
}
