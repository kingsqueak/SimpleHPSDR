using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Controls;
using SimpleHPSDR.Engine;
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
    [NotifyPropertyChangedFor(nameof(SliceAActive))] [NotifyPropertyChangedFor(nameof(SliceBActive))]
    [NotifyPropertyChangedFor(nameof(Rx1Active))] [NotifyPropertyChangedFor(nameof(Rx2Active))]
    [NotifyPropertyChangedFor(nameof(Rx2Idle))] [NotifyPropertyChangedFor(nameof(ActiveName))]
    private int _activeRx;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SplitText))]
    [NotifyPropertyChangedFor(nameof(SliceATx))] [NotifyPropertyChangedFor(nameof(SliceBTx))]
    [NotifyPropertyChangedFor(nameof(SliceAActive))] [NotifyPropertyChangedFor(nameof(SliceBActive))]
    private int _txRx;

    // The A and B badges beside the frequency: the active slice blue, the
    // transmitting one red (red wins when they're the same slice).
    public bool SliceAActive => ActiveRx == 0 && TxRx != 0;
    public bool SliceBActive => ActiveRx == 1 && TxRx != 1;
    public bool SliceATx => TxRx == 0;
    public bool SliceBTx => TxRx == 1;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSliceB))]
    [NotifyPropertyChangedFor(nameof(ShowPanadapterB))] [NotifyPropertyChangedFor(nameof(ShowSliceBInput))]
    [NotifyPropertyChangedFor(nameof(ShowAbBadges))] [NotifyPropertyChangedFor(nameof(Rx2Active))]
    [NotifyPropertyChangedFor(nameof(Rx1Active))]
    [NotifyPropertyChangedFor(nameof(Rx2Idle))] [NotifyPropertyChangedFor(nameof(ActiveName))]
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
            OnPropertyChanged(nameof(ShowAbBadges));
            OnPropertyChanged(nameof(AfLabelA));
            OnPropertyChanged(nameof(RxPanTip));
            OnPropertyChanged(nameof(AfLabelB));
            OnPropertyChanged(nameof(Rx1Active));
            OnPropertyChanged(nameof(Rx2Active));
            OnPropertyChanged(nameof(Rx2Idle));
            OnPropertyChanged(nameof(ActiveName));
            OnPropertyChanged(RxReadouts);
            ShowZoomControlsForActive();
        }
    }
    public bool ShowPanadapterB => SliceBEnabled && SliceBOwnPanadapter;

    // Two receivers: RX1 is the first panadapter, RX2 the second, which opens
    // when RX2 is turned on. The RX1 and RX2 buttons beside the frequency pick
    // the one the VFO and the controls act on. The engine's second receiver is
    // either RX2 or slice B on RX1's panadapter, never both.
    public bool Rx1Active => !Rx2Active;
    public bool Rx2Active => ShowPanadapterB && ActiveRx == 1;
    /// <summary>RX2 is on but the controls are on RX1.</summary>
    public bool Rx2Idle => ShowPanadapterB && ActiveRx != 1;
    /// <summary>A and B beside the frequency: only for slice B on RX1's panadapter.</summary>
    public bool ShowAbBadges => SliceBEnabled && !SliceBOwnPanadapter;
    /// <summary>The active one's name: RX1 or RX2 with two panadapters, A or B with one.</summary>
    public string ActiveName => ShowPanadapterB ? (ActiveRx == 0 ? "RX1" : "RX2") : ActiveSliceLabel;

    /// <summary>"RX1" / "RX2" with two panadapters, "slice A" / "slice B" with one.</summary>
    public string SliceName(int rx) => SliceBOwnPanadapter
        ? (rx == 0 ? "RX1" : "RX2")
        : (rx == 0 ? "slice A" : "slice B");

    /// <summary>Raised as a property name when a receiver's frequency or mode
    /// may have changed; the small VFOs on the panadapters read them again.</summary>
    public const string RxReadouts = nameof(RxReadouts);
    public long RxHz(int rx) => rx == ActiveRx ? VfoHz : (rx == 0 ? _sliceA : _sliceB)?.VfoHz ?? 0;
    public string RxModeLabel(int rx) => rx == ActiveRx ? ModeLabel
        : (rx == 0 ? _sliceA : _sliceB) is { } r ? LabelOf(r.Mode) : "";

    /// <summary>The RX1 and RX2 buttons. RX1 takes the controls. RX2 turns on
    /// on RX1's frequency, mode and filter if it's off (slice B on RX1's
    /// panadapter moves to its own), and takes the controls.</summary>
    public async Task SelectRxAsync(int rx)
    {
        if (!IsConnected) return;
        if (rx == 0)
        {
            // With one panadapter the controls are already on RX1, on slice A or B.
            if (ShowPanadapterB) await MakeActiveAsync(0);
            return;
        }
        if (!SliceBEnabled) { await AddSliceBAsync(VfoHz, ownPanadapter: true); return; }
        // Slice B moving to its own panadapter becomes RX2, on RX2's saved input.
        SliceBOwnPanadapter = true;
        SetSliceBMainForNow(false);
        await MakeActiveAsync(1);
    }

    /// <summary>A frequency typed into a panadapter's small VFO: that receiver
    /// takes the controls and tunes there.</summary>
    public async Task EnterRxFrequencyAsync(int rx, string text)
    {
        if (!IsConnected || !TryTypedHz(text, out long hz)) return;
        await TuneSliceAsync(rx, hz);
    }

    /// <summary>The two panadapters side by side, A on the left, instead of one above the other.</summary>
    public bool PanadaptersSideBySide
    {
        get => _settings.PanadaptersSideBySide;
        set
        {
            if (_settings.PanadaptersSideBySide == value) return;
            _settings.PanadaptersSideBySide = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>RX1's share of the room the two panadapters have; the bar
    /// between them drags it, a double-click on the bar makes them equal.</summary>
    public double PanSplit
    {
        get => Math.Clamp(_settings.PanSplit, 0.2, 0.8);
        set
        {
            value = Math.Clamp(value, 0.2, 0.8);
            if (Math.Abs(_settings.PanSplit - value) < 0.001) return;
            _settings.PanSplit = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>The desktop layout's controls in a panel at the side of the
    /// panadapter (true), or above and below it.</summary>
    public bool SidePanel
    {
        get => _settings.SidePanel;
        set
        {
            if (_settings.SidePanel == value) return;
            _settings.SidePanel = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public const double SideRailDefault = 410, SideRailMin = 330, SideRailMax = 900;

    /// <summary>The side panel's width, dragged by the bar at its left edge.</summary>
    public double SideRailWidth
    {
        get => Math.Clamp(_settings.SideRailWidth, SideRailMin, SideRailMax);
        set
        {
            value = Math.Clamp(value, SideRailMin, SideRailMax);
            if (Math.Abs(_settings.SideRailWidth - value) < 0.5) return;
            _settings.SideRailWidth = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    // Each slice's own AF, on top of the main (master) AF. While slice B is on
    // these two sliders (A over B) take the main AF slider's place.
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SliceAfAText))]
    private double _sliceAfA;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SliceAfBText))]
    private double _sliceAfB;
    /// <summary>The two volume sliders' labels.</summary>
    public string AfLabelA => SliceBOwnPanadapter ? "RX1" : "A";
    public string AfLabelB => SliceBOwnPanadapter ? "RX2" : "B";
    public string SliceAfAText => $"{SliceAfA:0} dB";
    public string SliceAfBText => $"{SliceAfB:0} dB";
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
    // The pan under the two volume sliders: RX1 towards one ear, RX2 towards
    // the other. The engine keeps it only while it runs, so it's sent on
    // every connect.
    private Latest<double>? _rxPanSender;

    /// <summary>-100 = RX1 in the left ear and RX2 in the right, 0 = both in
    /// both ears, +100 = RX1 right and RX2 left.</summary>
    public double RxPan
    {
        get => _settings.RxPan;
        set
        {
            int pan = (int)Math.Round(Math.Clamp(value, -100, 100));
            if (_settings.RxPan == pan) return;
            _settings.RxPan = pan;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(RxPanTip));
            SendRxPan();
        }
    }

    private double EngineRxPan => RxPan / 100.0;

    public string RxPanTip
    {
        get
        {
            double pan = EngineRxPan;
            if (pan == 0) return $"Pan: {AfLabelA} and {AfLabelB} in both ears. Slide to split them left and right.";
            string where = Math.Abs(pan) >= 1 ? "" : $" ({Math.Abs(pan) * 100:0}%)";
            return pan < 0
                ? $"Pan: {AfLabelA} left, {AfLabelB} right{where}"
                : $"Pan: {AfLabelA} right, {AfLabelB} left{where}";
        }
    }

    private void SendRxPan()
    {
        if (!IsConnected) return;
        _rxPanSender ??= new Latest<double>(pan => _client?.SetRxPanAsync(pan) ?? Task.CompletedTask);
        _rxPanSender.Send(EngineRxPan);
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
        bool two = SliceBOwnPanadapter;
        marks.Add(new SliceMark(0, a.Item1, a.Item2, a.Item3, two ? "RX1" : "A", ActiveRx == 0, TxRx == 0, _muteA));
        marks.Add(new SliceMark(1, b.Item1, b.Item2, b.Item3, two ? "RX2" : "B", ActiveRx == 1, TxRx == 1, _muteB));
        return marks;
    }

    /// <summary>Marks for one of the two panadapters (0 = RX1's, 1 = RX2's).
    /// With the receivers on different bands each shows only its own: the
    /// other's flag would just sit at the edge pointing off the display.</summary>
    public IReadOnlyList<SliceMark> PanMarks(int panRx)
    {
        var marks = SliceMarks();
        if (!ShowPanadapterB || marks.Count < 2 || SameBand(marks[0].Hz, marks[1].Hz)) return marks;
        return [marks[panRx]];
    }

    // Outside the bands (shortwave listening, say), close together counts as the same band.
    private static bool SameBand(long a, long b) =>
        (SimpleHPSDR.Band.Find(a), SimpleHPSDR.Band.Find(b)) switch
        {
            (null, null) => Math.Abs(a - b) < 500_000,
            var (x, y) => Equals(x, y),
        };

    /// <summary>From engine state (connect and the 1 s poll).</summary>
    private void UpdateSlices(StateDto s)
    {
        _sliceA = s.Receivers?.FirstOrDefault(r => r.Index == 0);
        _sliceB = s.Receivers?.FirstOrDefault(r => r.Index == 1);
        _muteA = _sliceA?.Muted ?? false;
        _muteB = _sliceB?.Muted ?? false;
        SliceBEnabled = _sliceB?.Enabled ?? false;
        OnPropertyChanged(RxReadouts);
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
        // RX2 in its own panadapter has its own zoom. Slice B on RX1's
        // panadapter is drawn at RX1's zoom, so its own is kept the same.
        if (SliceBEnabled && SliceBOwnPanadapter) FollowEngineZoomB(_sliceB!.ZoomLevel);
        else if (SliceBEnabled && _sliceB!.ZoomLevel != s.ZoomLevel && IsConnected)
            _ = Try(SyncSliceBZoomAsync);
        TxRx = SliceBEnabled ? s.TxReceiverIndex : 0;
        // A TX slice that isn't the active one is only seen here: follow its
        // band and mode. The active slice's own changes come through VfoHz and
        // Mode, and while it's being switched VfoHz can still be the other's.
        if (TxRx != ActiveRx) FollowTxSliceForPs();
        if (!SliceBEnabled && ActiveRx != 0) _ = MakeActiveAsync(0);
        SyncSliceBInput();
        CheckAttSource();
    }

    partial void OnTxRxChanged(int value) => FollowTxSliceForPs();

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
        // Until the engine's state arrives, mode and filter are still the other
        // receiver's: wheel tuning on a panadapter waits for this to finish.
        _switchingRx++;
        try
        {
            bool changing = rx != ActiveRx;
            ActiveRx = rx;
            // Show the new receiver's last known frequency straight away, so
            // nothing reads the other one's under the new receiver's name.
            if (changing && (rx == 0 ? _sliceA : _sliceB) is { } known)
            {
                _applyingState = true;
                try { SetVfoLocal(known.VfoHz); }
                finally { _applyingState = false; }
            }
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
        finally
        {
            _switchingRx--;
        }
    }

    /// <summary>More than 0 while MakeActiveAsync is fetching the new active receiver's state.</summary>
    private int _switchingRx;

    /// <summary>Turn on slice B at a frequency, in the active slice's mode and
    /// filter, and make it active: on A's panadapter, or in its own.</summary>
    public async Task AddSliceBAsync(long hz, bool ownPanadapter)
    {
        if (!IsConnected) return;
        SliceBOwnPanadapter = ownPanadapter;
        // The Rx2 jack is remembered for RX2. A slice added on RX1's panadapter
        // starts on the main antenna, or it would come up silent with nothing
        // on that jack; RX2's saved choice is kept, and the input can still be
        // changed afterwards.
        SetSliceBMainForNow(!ownPanadapter);
        byte? adc = HasRx2Jack ? (SliceBJackWanted ? (byte)1 : (byte)0) : null;
        await Try(async () =>
        {
            await _client!.SetReceiverAsync(1, new ReceiverSetRequest(
                Enabled: true, VfoHz: hz, Mode: Mode, FilterLowHz: FilterLowHz, FilterHighHz: FilterHighHz,
                AfGainDb: 0, FilterPresetName: Filters.FirstOrDefault(f => f.IsSelected)?.SlotName,
                ZoomLevel: ZoomLevel, Agc: new AgcConfig(_agcMode), AutoAgcEnabled: AgcAuto, AdcSource: adc));
            // RX2 starts at RX1's zoom; from here each has its own.
            _zoomLevelB = ZoomLevel;
            _zoomFine[1] = _zoomFine[0];
            SliceBEnabled = true;
            SetSliceAfLocal(1, 0);
            await MakeActiveAsync(1);
            Status = ownPanadapter ? $"RX2 on, {hz / 1e6:0.000000} MHz." : $"Slice B on {hz / 1e6:0.000000} MHz.";
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
        // Turning receiver 1 off makes the engine move transmit to RX1 at once,
        // even mid-transmission, so this has to be checked first.
        if (IsTransmitting && TxRx == 1)
        {
            Status = SliceBOwnPanadapter ? "Unkey before turning RX2 off." : "Unkey before removing slice B.";
            return;
        }
        await Try(async () =>
        {
            // TX back to A first; if the engine refuses, B stays on.
            if (TxRx == 1 && !await MoveTxAsync(0)) return;
            if (ActiveRx == 1) await MakeActiveAsync(0);
            await _client!.SetReceiverAsync(1, new ReceiverSetRequest(Enabled: false));
            SliceBEnabled = false;
            TxRx = 0;
            Status = SliceBOwnPanadapter ? "RX2 off." : "Slice B removed.";
        });
    }

    /// <summary>Transmit on slice A (0) or B (1): its frequency, mode and filter.</summary>
    public async Task TransmitOnAsync(int rx)
    {
        if (!IsConnected) return;
        if (IsTransmitting)
        {
            Status = "Unkey before moving transmit.";
            return;
        }
        await Try(async () =>
        {
            if (!await MoveTxAsync(rx)) return;
            TxRx = rx;
            await FollowRxFilterAsync();
            Status = $"Transmit on {SliceName(rx)}.";
        });
    }

    /// <summary>Move transmit to a receiver unless the radio is keyed: the
    /// engine checks too, in case it was keyed after the app last looked.
    /// False, with the reason on the status line, if it refused.</summary>
    private async Task<bool> MoveTxAsync(int rx)
    {
        try
        {
            await _client!.SetTxReceiverAsync(rx, requireIdle: true);
            return true;
        }
        catch (StationException ex) when (ex.Status == HttpStatusCode.Conflict)
        {
            Status = ex.Message == "tx-receiver-unavailable"
                ? $"Transmit stays where it is: {SliceName(rx)} isn't on."
                : "Unkey before moving transmit.";
            return false;
        }
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

    private const int PanStepMs = 35;
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
        // At most one move per engine tick (33 ms): the radio retunes at once but
        // the engine re-aligns the audio only on its tick, so a fast drag sending
        // at mouse rate stacked several retunes per tick and the audio warbled.
        (_panSenders[rx] ??= new Latest<long>(async hz =>
        {
            await _client!.SetReceiverLoAsync(rx, hz);
            await Task.Delay(PanStepMs);
        })).Send(shown);

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
    private async Task CenterOnDialAsync(int rx)
    {
        // RX1's display keeps the dial where its Centre slider puts it.
        if (rx == 0) await _client!.SetDialPositionAsync(DialPositionOf(0) / 100.0);
        else if (ShowPanadapterB) await _client!.SetReceiverLoAsync(1, ActiveRx == 1 ? VfoHz : _sliceB?.VfoHz ?? VfoHz);
    }

    /// <summary>A click or wheel on a slice's own panadapter: make that slice
    /// active first, then tune it. One arriving while the active receiver is
    /// still being switched is dropped: the VFO may still hold the other
    /// receiver's frequency, and steps from it would land on the wrong band.</summary>
    public async Task TuneSliceAsync(int rx, long hz)
    {
        if (_switchingRx > 0) return;
        if (rx != ActiveRx) await MakeActiveAsync(rx);
        if (rx == ActiveRx && _switchingRx == 0) TuneTo(hz);
    }

    public async Task TuneSliceByStepsAsync(int rx, int steps)
    {
        if (_switchingRx > 0) return;
        if (rx != ActiveRx) await MakeActiveAsync(rx);
        if (rx == ActiveRx && _switchingRx == 0) TuneBySteps(steps);
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
