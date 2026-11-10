using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Controls;
using SimpleHPSDR.Contracts;

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
    [NotifyPropertyChangedFor(nameof(RitUsable))]
    private int _activeRx;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SplitText))]
    private int _txRx;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSliceB))] [NotifyPropertyChangedFor(nameof(AfTip))]
    [NotifyPropertyChangedFor(nameof(ShowPanadapterB))]
    private bool _sliceBEnabled;

    [ObservableProperty] private bool _fullDuplex;

    public bool HasSliceB => SliceBEnabled;

    /// <summary>Slice B shown in its own panadapter below A's (for another
    /// band); otherwise it's drawn on A's panadapter only.</summary>
    public bool SliceBOwnPanadapter
    {
        get => _settings.SliceBOwnPanadapter;
        set
        {
            if (_settings.SliceBOwnPanadapter == value) return;
            _settings.SliceBOwnPanadapter = value;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowPanadapterB));
        }
    }
    public bool ShowPanadapterB => SliceBEnabled && SliceBOwnPanadapter;

    // Each slice's own AF, on top of the main (master) AF. Shown under the
    // main AF while slice B is on.
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SliceAfText))] [NotifyPropertyChangedFor(nameof(AfTip))]
    private double _sliceAfA;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SliceAfText))] [NotifyPropertyChangedFor(nameof(AfTip))]
    private double _sliceAfB;
    public string SliceAfText => $"A{SliceAfA:0} B{SliceAfB:0}";
    public string AfTip => HasSliceB
        ? $"Ball: main AF, the overall volume ({AfGainDb:0} dB). Flags: each slice's own level on top of it — A {SliceAfA:0} dB above the track, B {SliceAfB:0} dB below. Drag a flag or use the wheel over it."
        : "Main AF: the overall volume";
    private readonly Latest<double>[] _sliceAfSenders;
    private bool _applyingSliceAf;
    private DateTime _sliceAfMovedAt;

    private void SetSliceAfLocal(int rx, double db)
    {
        _applyingSliceAf = true;
        if (rx == 0) SliceAfA = db; else SliceAfB = db;
        _applyingSliceAf = false;
    }

    partial void OnSliceAfAChanged(double value) => SendSliceAf(0, value);
    partial void OnSliceAfBChanged(double value) => SendSliceAf(1, value);

    private void SendSliceAf(int rx, double db)
    {
        if (_applyingSliceAf || !IsConnected) return;
        _sliceAfMovedAt = DateTime.UtcNow;
        _sliceAfSenders[rx].Send(Math.Round(db));
    }
    public string ActiveSliceLabel => ActiveRx == 0 ? "A" : "B";
    /// <summary>Shown under the VFO slice letter when transmit is on the other slice.</summary>
    public string SplitText => SliceBEnabled && TxRx != ActiveRx ? $"TX {(TxRx == 0 ? "A" : "B")}" : "";

    // Last known state of each slice, for drawing the one that isn't active.
    private ReceiverDto? _sliceA, _sliceB;
    private bool _muteA, _muteB;

    /// <summary>Marks for the panadapter: both slices' passbands and tags.</summary>
    // In CW the engine moves the LO by the pitch, so a filter given as an audio
    // tone around the pitch is really centred on the dial. Without this the
    // shading sits a pitch away from what's heard.
    private static int CwShift(RxMode? mode) => mode switch
    {
        RxMode.CWU => -CwDefaults.PitchHz,
        RxMode.CWL => CwDefaults.PitchHz,
        _ => 0,
    };

    /// <summary>The audio tone a signal on the dial comes out at; 0 outside CW.</summary>
    public int CwPitchHz => ModeIsCw ? CwDefaults.PitchHz : 0;

    public IReadOnlyList<SliceMark> SliceMarks()
    {
        var marks = new List<SliceMark>(2);
        int shift = CwShift(Mode);
        if (!SliceBEnabled)
            return [new SliceMark(0, VfoHz, FilterLowHz + shift, FilterHighHz + shift, "A", true, true, false)];
        int shiftA = CwShift(_sliceA?.Mode), shiftB = CwShift(_sliceB?.Mode);
        var a = ActiveRx == 0 ? (VfoHz, FilterLowHz + shift, FilterHighHz + shift)
            : (_sliceA?.VfoHz ?? 0, (_sliceA?.FilterLowHz ?? 0) + shiftA, (_sliceA?.FilterHighHz ?? 0) + shiftA);
        var b = ActiveRx == 1 ? (VfoHz, FilterLowHz + shift, FilterHighHz + shift)
            : (_sliceB?.VfoHz ?? 0, (_sliceB?.FilterLowHz ?? 0) + shiftB, (_sliceB?.FilterHighHz ?? 0) + shiftB);
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
        UpdateAgc(s);
        UpdateOperating(s);
        _ = FollowRxFilterAsync(s);
        // Follow the engine's slice AF unless the operator just moved a slider.
        if (DateTime.UtcNow - _sliceAfMovedAt > TimeSpan.FromSeconds(2))
        {
            SetSliceAfLocal(0, s.Rx1AfGainDb);
            SetSliceAfLocal(1, _sliceB?.AfGainDb ?? 0);
        }
        // B's display frames are labelled with the primary's zoom, so B's own
        // zoom must match or its passband and signals land at different scales.
        if (SliceBEnabled && _sliceB!.ZoomLevel != s.ZoomLevel && IsConnected)
            _ = Try(SyncSliceBZoomAsync);
        TxRx = SliceBEnabled ? s.TxReceiverIndex : 0;
        if (!SliceBEnabled && ActiveRx != 0) _ = MakeActiveAsync(0);
    }

    /// <summary>The engine's view of the active slice.</summary>
    private (long Hz, RxMode Mode, int Low, int High, string? Preset) ActiveOf(StateDto s)
    {
        if (ActiveRx == 1 && s.Receivers?.FirstOrDefault(r => r.Index == 1) is { } b)
        {
            var (bl, bh) = FreeDvEdges(b.Mode, b.VfoHz, b.FilterLowHz, b.FilterHighHz);
            return (b.VfoHz, b.Mode, bl, bh, b.FilterPresetName);
        }
        var (lo, hi) = FreeDvEdges(s.Mode, s.VfoHz, s.FilterLowHz, s.FilterHighHz);
        return (s.VfoHz, s.Mode, lo, hi, s.FilterPresetName);
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
                await SetModeLocalAsync(a.Mode, a.Preset);
            }
            finally
            {
                _applyingState = false;
            }
        });
    }

    /// <summary>Turn on slice B at a frequency, in the active slice's mode and
    /// filter, and make it active: on A's panadapter, or in its own.</summary>
    public async Task AddSliceBAsync(long hz, bool ownPanadapter)
    {
        if (!IsConnected) return;
        SliceBOwnPanadapter = ownPanadapter;
        await Try(async () =>
        {
            await _client!.SetReceiverAsync(1, new ReceiverSetRequest(
                Enabled: true, VfoHz: hz, Mode: Mode, FilterLowHz: FilterLowHz, FilterHighHz: FilterHighHz,
                AfGainDb: 0, FilterPresetName: Filters.FirstOrDefault(f => f.IsSelected)?.SlotName,
                ZoomLevel: ZoomLevel, Agc: new AgcConfig(_agcMode), AutoAgcEnabled: AgcAuto));
            SliceBEnabled = true;
            SetSliceAfLocal(1, 0);
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
            await FollowRxFilterAsync();
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

    private int _sampleRateHz;
    private readonly Latest<long>?[] _panSenders = new Latest<long>?[2];

    /// <summary>How long a drag rests at the end of the span before the dial recentres.</summary>
    private static readonly TimeSpan PanHold = TimeSpan.FromMilliseconds(700);

    private DispatcherTimer? _panHold;
    private int _panRx;
    private long _panWanted, _panShown;
    private bool _panReleased;

    /// <summary>A panadapter dragged: move that receiver's display centre, not
    /// its dial. The dial must stay inside what the receiver captures or its
    /// audio would stop, so the centre stops short of that; this limit sits inside
    /// the engine's, which would recentre the display. With CTUN on, a drag reaching
    /// the limit moves the active slice's dial to the middle of the display after
    /// a short pause: the picture stays put and the drag has room to carry on. A
    /// locked dial or a keyed radio doesn't move. The engine ignores this while keyed.</summary>
    public void PanTo(int rx, long centerHz)
    {
        if (!IsConnected) return;
        _panReleased = false;
        long dial = rx == ActiveRx ? VfoHz : (rx == 0 ? _sliceA : _sliceB)?.VfoHz ?? VfoHz;
        long reach = (long)(_sampleRateHz * 0.43);
        long shown = Math.Clamp(centerHz, dial - reach, dial + reach);
        (_panSenders[rx] ??= new Latest<long>(hz => _client!.SetReceiverLoAsync(rx, hz))).Send(shown);

        (_panRx, _panWanted, _panShown) = (rx, centerHz, shown);
        if (shown != centerHz && rx == ActiveRx && CtunOn && !VfoLocked && !IsTransmitting)
        {
            _panHold ??= MakePanHold();
            _panHold.IsEnabled = true;
        }
        else if (_panHold is { } hold) hold.IsEnabled = false;
    }

    private DispatcherTimer MakePanHold()
    {
        var timer = new DispatcherTimer { Interval = PanHold };
        timer.Tick += (_, _) =>
        {
            timer.IsEnabled = false;
            if (!IsConnected || _panRx != ActiveRx || !CtunOn || VfoLocked || IsTransmitting) return;
            // On the tuning step, if that's small enough to stay close to the middle.
            long snap = Math.Clamp(StepHz, 1, Math.Max(1, _sampleRateHz / 50));
            TuneTo((long)Math.Round((double)_panShown / snap) * snap);
            if (!_panReleased) PanTo(_panRx, _panWanted);
        };
        return timer;
    }

    /// <summary>The drag ended. A recentre already waiting still happens, or a short
    /// drag to the end of the span would just stop there, but the display stays put.</summary>
    public void PanEnded() => _panReleased = true;

    /// <summary>Bring the dial to the middle of the display: with CTUN on or
    /// after a drag it can be off centre, and a narrow span could leave it out
    /// of view. Each panadapter centres on its own slice.</summary>
    private async Task CenterOnDialAsync()
    {
        // Slice A's display keeps the dial where the centring slider puts it.
        await _client!.SetDialPositionAsync(DialPosition / 100.0);
        if (ShowPanadapterB) await _client.SetReceiverLoAsync(1, ActiveRx == 1 ? VfoHz : _sliceB?.VfoHz ?? VfoHz);
    }

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
