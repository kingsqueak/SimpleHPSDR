using System.Collections.ObjectModel;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>Mem: 16 frequency-and-mode memories, the same on every band, kept in
/// the app's settings. Click a button to tune there and set its mode;
/// right-click (long press) to store the frequency and mode you're on.</summary>
public sealed partial class MainViewModel
{
    public const int MemoryCount = 16;
    private ObservableCollection<MemoryButton>? _memories;

    public ObservableCollection<MemoryButton> Memories => _memories ??= LoadMemories();

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
            Status = $"Memory {b.Index + 1} is open: right-click it to store this frequency.";
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
        Status = $"Memory {b.Index + 1}: {MemoryButton.FormatMHz(slot.Hz)} MHz {mode}.";
    }

    /// <summary>When the Mem popup closes: stored memories in frequency order,
    /// low to high, left to right and down; the open ones after them.</summary>
    public void SortMemories()
    {
        var sorted = _settings.Memories.Where(m => m is not null).OrderBy(m => m!.Hz).ThenBy(m => m!.Mode)
            .Concat(_settings.Memories.Where(m => m is null)).ToArray();
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
        Status = $"Stored {MemoryButton.FormatMHz(slot.Hz)} MHz {Mode} in memory {b.Index + 1}.";
    }
}
