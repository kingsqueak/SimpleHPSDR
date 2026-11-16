using System.Collections.ObjectModel;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>Mem: three tabs, A, B and C, of 16 frequency-and-mode memories each,
/// the same on every band, kept in the app's settings. Click a button to tune
/// there and set its mode; right-click (long press) to store the frequency and
/// mode you're on. Settings from before the tabs keep their 16 in A.</summary>
public sealed partial class MainViewModel
{
    public const int MemoriesPerPage = 16;
    public const int MemoryPageCount = 3;
    public const int MemoryCount = MemoriesPerPage * MemoryPageCount;
    private ObservableCollection<MemoryButton>? _memories;
    private MemoryPage[]? _memoryPages;

    public ObservableCollection<MemoryButton> Memories => _memories ??= LoadMemories();

    /// <summary>The tabs, A to C, each with its 16 buttons.</summary>
    public MemoryPage[] MemoryPages => _memoryPages ??= [.. Enumerable.Range(0, MemoryPageCount)
        .Select(p => new MemoryPage(((char)('A' + p)).ToString(),
            [.. Memories.Skip(p * MemoriesPerPage).Take(MemoriesPerPage)], SelectMemoryPage)
            { IsSelected = p == MemoryPageIndex })];

    /// <summary>The tab showing; kept while the app runs so Mem reopens on it.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private int _memoryPageIndex;

    public MemoryPage SelectedMemoryPage => MemoryPages[MemoryPageIndex];

    partial void OnMemoryPageIndexChanged(int value)
    {
        for (int i = 0; i < MemoryPages.Length; i++) MemoryPages[i].IsSelected = i == value;
        OnPropertyChanged(nameof(SelectedMemoryPage));
    }

    private void SelectMemoryPage(MemoryPage page) => MemoryPageIndex = Array.IndexOf(MemoryPages, page);

    private ObservableCollection<MemoryButton> LoadMemories()
    {
        if (_settings.Memories is not { Length: MemoryCount })
        {
            var old = _settings.Memories ?? [];
            _settings.Memories = [.. Enumerable.Range(0, MemoryCount).Select(i => i < old.Length ? old[i] : null)];
        }
        var list = new ObservableCollection<MemoryButton>();
        for (int i = 0; i < MemoryCount; i++)
            list.Add(new MemoryButton(i, RecallMemoryAsync, StoreMemory) { Slot = _settings.Memories[i] });
        return list;
    }

    private async Task RecallMemoryAsync(MemoryButton b)
    {
        if (b.Slot is not { } slot)
        {
            Status = $"Memory {b.Name} is open: right-click it to store this frequency.";
            return;
        }
        if (!IsConnected) return;
        if (IsTransmitting)
        {
            Status = "Unkey before recalling a memory.";
            return;
        }
        if (VfoLocked)
        {
            Status = "VFO is locked.";
            return;
        }
        var mode = (RxMode)slot.Mode;
        // As with a band change: keep this band and mode's settings, then bring
        // back the new band and mode's own (filter, AGC, drive and the rest).
        RememberBandMode();
        TuneTo(slot.Hz);
        if (mode != Mode)
        {
            await ChangeModeAsync(mode);
            await RecallAsync(_settings.ModeMemories.GetValueOrDefault(MemoryKey(BandOf(slot.Hz), mode)));
        }
        Status = $"Memory {b.Name}: {MemoryButton.FormatMHz(slot.Hz)} MHz {mode}.";
    }

    /// <summary>When the Mem popup closes: each tab's stored memories in frequency
    /// order, low to high, left to right and down; the open ones after them.
    /// Memories stay on their own tab.</summary>
    public void SortMemories()
    {
        var sorted = _settings.Memories.Chunk(MemoriesPerPage).SelectMany(page =>
            page.Where(m => m is not null).OrderBy(m => m!.Hz).ThenBy(m => m!.Mode)
                .Concat(page.Where(m => m is null))).ToArray();
        if (sorted.SequenceEqual(_settings.Memories)) return;
        _settings.Memories = sorted;
        _settings.Save();
        for (int i = 0; i < MemoryCount; i++) Memories[i].Slot = sorted[i];
    }

    private void StoreMemory(MemoryButton b)
    {
        if (!IsConnected) return;
        var slot = new MemorySlot(VfoHz, (int)Mode);
        _settings.Memories[b.Index] = slot;
        _settings.Save();
        b.Slot = slot;
        Status = $"Stored {MemoryButton.FormatMHz(slot.Hz)} MHz {Mode} in memory {b.Name}.";
    }
}

/// <summary>One Mem tab: its letter, its 16 buttons, and whether it's the one showing.</summary>
public sealed partial class MemoryPage : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public MemoryPage(string name, MemoryButton[] buttons, Action<MemoryPage> select)
    {
        Name = name;
        Buttons = buttons;
        Select = new CommunityToolkit.Mvvm.Input.RelayCommand(() => select(this));
    }

    public string Name { get; }
    public MemoryButton[] Buttons { get; }
    public CommunityToolkit.Mvvm.Input.IRelayCommand Select { get; }
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _isSelected;
}
