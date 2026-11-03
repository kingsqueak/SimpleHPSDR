using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Controls;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// Slices on the panadapter: A is the engine's receiver 0 (RX1), B is
/// receiver 1 (RX2). The active slice is the one the VFO, band, mode, filter,
/// AF and tuning act on; the TX slice is the engine's TX receiver, whose
/// frequency, mode and filter transmit (TX on B while listening on A is split).
/// Both play mixed, each can be muted. Slice B gets its own panadapter, so
/// it can sit on another band. DUP keeps the receivers audible while keyed.
/// The slices share one receive filter (mirrored for the other sideband) and
/// one display zoom; the engine has a single TX filter, signed for whichever
/// slice transmits.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ActiveSliceLabel))] [NotifyPropertyChangedFor(nameof(SplitText))]
    private int _activeRx;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SplitText))]
    private int _txRx;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSliceB))]
    private bool _sliceBEnabled;

    [ObservableProperty] private bool _fullDuplex;

    public bool HasSliceB => SliceBEnabled;
    public string ActiveSliceLabel => ActiveRx == 0 ? "A" : "B";
    /// <summary>Shown under the VFO slice letter when transmit is on the other slice.</summary>
    public string SplitText => SliceBEnabled && TxRx != ActiveRx ? $"TX {(TxRx == 0 ? "A" : "B")}" : "";

    // Last known state of each slice, for drawing the one that isn't active.
    private ReceiverDto? _sliceA, _sliceB;
    private bool _muteA, _muteB;

    /// <summary>Marks for the panadapter: both slices' passbands and tags.</summary>
    public IReadOnlyList<SliceMark> SliceMarks()
    {
        var marks = new List<SliceMark>(2);
        if (!SliceBEnabled)
            return [new SliceMark(0, VfoHz, FilterLowHz, FilterHighHz, "A", true, true, false)];
        var a = ActiveRx == 0 ? (VfoHz, FilterLowHz, FilterHighHz)
            : (_sliceA?.VfoHz ?? 0, _sliceA?.FilterLowHz ?? 0, _sliceA?.FilterHighHz ?? 0);
        var b = ActiveRx == 1 ? (VfoHz, FilterLowHz, FilterHighHz)
            : (_sliceB?.VfoHz ?? 0, _sliceB?.FilterLowHz ?? 0, _sliceB?.FilterHighHz ?? 0);
        marks.Add(new SliceMark(0, a.Item1, a.Item2, a.Item3, "A", ActiveRx == 0, TxRx == 0, _muteA));
        marks.Add(new SliceMark(1, b.Item1, b.Item2, b.Item3, "B", ActiveRx == 1, TxRx == 1, _muteB));
        return marks;
    }

    /// <summary>From engine state (connect and the 1 s poll).</summary>
    private void UpdateSlices(StateDto s)
    {
        _sliceA = s.Receivers?.FirstOrDefault(r => r.Index == 0);
        _sliceB = s.Receivers?.FirstOrDefault(r => r.Index == 1);
        _muteA = _sliceA?.Muted ?? false;
        _muteB = _sliceB?.Muted ?? false;
        SliceBEnabled = _sliceB?.Enabled ?? false;
        FullDuplex = s.FullDuplexMultiRxEnabled;
        // B's display frames are labelled with the primary's zoom, so B's own
        // zoom must match or its passband and signals land at different scales.
        if (SliceBEnabled && _sliceB!.ZoomLevel != s.ZoomLevel && IsConnected)
            _ = Try(SyncSliceBZoomAsync);
        TxRx = SliceBEnabled ? s.TxReceiverIndex : 0;
        if (!SliceBEnabled && ActiveRx != 0) _ = MakeActiveAsync(0);
    }

    /// <summary>The engine's view of the active slice.</summary>
    private (long Hz, RxMode Mode, int Low, int High, string? Preset, double Af) ActiveOf(StateDto s)
    {
        if (ActiveRx == 1 && s.Receivers?.FirstOrDefault(r => r.Index == 1) is { } b)
            return (b.VfoHz, b.Mode, b.FilterLowHz, b.FilterHighHz, b.FilterPresetName, b.AfGainDb);
        return (s.VfoHz, s.Mode, s.FilterLowHz, s.FilterHighHz, s.FilterPresetName, s.RxAfGainDb);
    }

    /// <summary>Point the VFO, band, mode, filter and AF at slice A (0) or B (1).</summary>
    public async Task MakeActiveAsync(int rx)
    {
        if (!IsConnected || (rx == 1 && !SliceBEnabled)) return;
        ActiveRx = rx;
        await Try(async () =>
        {
            var s = await _client!.GetStateAsync();
            UpdateSlices(s);
            var a = ActiveOf(s);
            _applyingState = true;
            try
            {
                SetVfoLocal(a.Hz);
                FilterLowHz = a.Low;
                FilterHighHz = a.High;
                AfGainDb = a.Af;
                await SetModeLocalAsync(a.Mode, a.Preset);
            }
            finally
            {
                _applyingState = false;
            }
        });
    }

    /// <summary>Turn on slice B at a frequency, in the active slice's mode and
    /// filter, and make it active.</summary>
    public async Task AddSliceBAsync(long hz)
    {
        if (!IsConnected) return;
        await Try(async () =>
        {
            await _client!.SetReceiverAsync(1, new ReceiverSetRequest(
                Enabled: true, VfoHz: hz, Mode: Mode, FilterLowHz: FilterLowHz, FilterHighHz: FilterHighHz,
                AfGainDb: AfGainDb, FilterPresetName: Filters.FirstOrDefault(f => f.IsSelected)?.SlotName,
                ZoomLevel: ZoomLevel));
            SliceBEnabled = true;
            await MakeActiveAsync(1);
            Status = $"Slice B on {hz / 1e6:0.000000} MHz.";
        });
    }

    /// <summary>Move slice B to a frequency without changing which slice is active.</summary>
    public async Task MoveSliceBAsync(long hz)
    {
        if (ActiveRx == 1) { TuneTo(hz); return; }
        await Try(() => _client!.SetVfoAsync(hz, 1));
    }

    public async Task RemoveSliceBAsync()
    {
        if (!IsConnected || !SliceBEnabled) return;
        await Try(async () =>
        {
            if (TxRx == 1) await _client!.SetTxReceiverAsync(0);   // TX back to A first
            if (ActiveRx == 1) await MakeActiveAsync(0);
            await _client!.SetReceiverAsync(1, new ReceiverSetRequest(Enabled: false));
            SliceBEnabled = false;
            TxRx = 0;
            Status = "Slice B removed.";
        });
    }

    /// <summary>Transmit on slice A (0) or B (1): its frequency, mode and filter.</summary>
    public async Task TransmitOnAsync(int rx)
    {
        if (!IsConnected) return;
        if (IsTransmitting)
        {
            Status = "Unkey before moving transmit to another slice.";
            return;
        }
        await Try(async () =>
        {
            await _client!.SetTxReceiverAsync(rx);
            TxRx = rx;
            Status = $"Transmit on slice {(rx == 0 ? "A" : "B")}.";
        });
    }

    public async Task SetSliceMuteAsync(int rx, bool muted)
    {
        await Try(async () =>
        {
            await _client!.SetReceiverMuteAsync(rx, muted);
            if (rx == 0) _muteA = muted; else _muteB = muted;
        });
    }

    public bool IsSliceMuted(int rx) => rx == 0 ? _muteA : _muteB;

    private Task SyncSliceBZoomAsync() =>
        SliceBEnabled ? _client!.SetReceiverAsync(1, new ReceiverSetRequest(ZoomLevel: ZoomLevel)) : Task.CompletedTask;

    /// <summary>Give the other slice the same receive filter as the active one,
    /// mirrored when one is lower and the other upper sideband. Slices in
    /// different kinds of mode (SSB and CW, say) keep their own.</summary>
    private async Task MirrorFilterAsync(int low, int high, string? preset)
    {
        if (!SliceBEnabled) return;
        int other = ActiveRx == 0 ? 1 : 0;
        if ((other == 0 ? _sliceA : _sliceB) is not { } o) return;
        if (Kind(o.Mode) != Kind(Mode)) return;
        var (lo, hi) = Lower(o.Mode) == Lower(Mode) ? (low, high) : (-high, -low);
        if (o.FilterLowHz == lo && o.FilterHighHz == hi) return;
        await _client!.SetFilterAsync(lo, hi, preset, other);
        if (other == 0) _sliceA = o with { FilterLowHz = lo, FilterHighHz = hi };
        else _sliceB = o with { FilterLowHz = lo, FilterHighHz = hi };
    }

    private static string Kind(RxMode m) => m switch
    {
        RxMode.LSB or RxMode.USB => "SSB",
        RxMode.CWL or RxMode.CWU => "CW",
        RxMode.DIGL or RxMode.DIGU => "DIG",
        _ => m.ToString(),
    };

    private static bool Lower(RxMode m) => m is RxMode.LSB or RxMode.CWL or RxMode.DIGL;

    /// <summary>A click or wheel on a slice's own panadapter: make that slice
    /// active first, then tune it.</summary>
    public async Task TuneSliceAsync(int rx, long hz)
    {
        if (rx != ActiveRx) await MakeActiveAsync(rx);
        if (rx == ActiveRx) TuneTo(hz);
    }

    public async Task TuneSliceByStepsAsync(int rx, int steps)
    {
        if (rx != ActiveRx) await MakeActiveAsync(rx);
        if (rx == ActiveRx) TuneBySteps(steps);
    }

    /// <summary>DUP: hear the receivers while transmitting. Off by default:
    /// with a shared antenna the T/R relay leaves little to hear, or TX bleeds in.</summary>
    [RelayCommand]
    private async Task ToggleFullDuplexAsync()
    {
        if (!IsConnected) return;
        bool on = !FullDuplex;
        await Try(async () =>
        {
            await _client!.SetFullDuplexAsync(on);
            FullDuplex = on;
            Status = on ? "DUP on: receivers stay audible while transmitting." : "DUP off: receive audio mutes while transmitting.";
        });
    }
}
