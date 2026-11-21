using System.Collections.ObjectModel;
using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Controls;
using SimpleHPSDR.Engine;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// Slices on the panadapters: up to four, numbered by the engine's receiver
/// index — A1 and A2 on RX1's panadapter (receivers 0 and 2), B1 and B2 on
/// RX2's (receivers 1 and 3). The second slices come and go in any order;
/// B2 needs B1, and RX2 off takes B2 with it. The active slice is the one the VFO,
/// band, mode, filter, AF and tuning act on; the TX slice is the engine's TX
/// receiver, whose frequency, mode and filter transmit (TX on B1 while
/// listening on A1 is split). All slices play mixed, each can be muted. Each
/// panadapter is drawn from its first slice's DDC stream and zooms as one;
/// the second slice on a pan keeps its own dial, mode, filter, the entire
/// noise block and AGC. DUP keeps the receivers audible while keyed.
/// A Protocol 1 radio (two receivers), or a Protocol 2 one with two whose
/// engine can't share streams, gets one slice per panadapter instead: A1 is
/// receiver 0 on RX1's pan and B1 is receiver 1 on RX2's. On a Protocol 2 radio with receivers to spare A2 and B2 have DDCs
/// of their own and tune anywhere. On one with only two receivers (a Brick 3
/// with four DDCs, two of them PureSignal's) they run without: A2 on A1's
/// stream and B2 on B1's. Those can only be tuned inside their stream's span,
/// and fall silent, keeping their frequency, when the first slice or a drag
/// moves the span away from them.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The most slices the app offers, whatever the radio allows.</summary>
    public const int MaxSlices = 4;

    // The connected radio has only two receivers: one slice per panadapter,
    // A1 = receiver 0, B1 = receiver 1. Static because the views and controls
    // ask the static helpers below; there is one radio connection per app.
    private static volatile bool s_twoReceivers;

    /// <summary>The radio has two receivers only: one slice per panadapter.</summary>
    public static bool TwoReceiversOnly => s_twoReceivers;

    // The second slices run without a DDC of their own on the first slices'
    // streams: A2 (receiver 2) on receiver 0's, B2 (receiver 3) on receiver
    // 1's. Never set together with s_twoReceivers.
    private static volatile bool s_sharedSlices;

    // SIMPLEHPSDR_SHARED_SLICES=1: shared streams on any Protocol 2 radio,
    // for testing them on one with receivers to spare.
    private static readonly bool SharedSlicesForced = Environment.GetEnvironmentVariable("SIMPLEHPSDR_SHARED_SLICES") == "1";

    // This connection's engine turned on a receiver with a DDC of its own when
    // asked for a shared one: it predates shared streams.
    private bool _sharingUnsupported;

    /// <summary>The second slices (A2, B2) share their pan's first slice's
    /// stream instead of having DDCs of their own.</summary>
    public static bool SharedSlices => s_sharedSlices;

    // Where slice i sits in the order A1, A2, B1, B2: receivers 1 (B1) and
    // 2 (A2) swap places. With one slice per panadapter only receiver 1 does.
    private static int LayoutSlot(int i) => s_twoReceivers
        ? i == 1 ? 2 : i
        : i switch { 1 => 2, 2 => 1, _ => i };

    // The receivers in layout order (A1, A2, B1, B2). With one slice per
    // panadapter only 0 and 1 are ever on: A1 then B1.
    private static readonly int[] LayoutOrder = [0, 2, 1, 3];

    /// <summary>Slice i's label: A1, A2 on RX1's panadapter, B1, B2 on RX2's.</summary>
    public static string SliceLabel(int i) => LayoutSlot(i) switch { 0 => "A1", 1 => "A2", 2 => "B1", 3 => "B2", _ => $"RX{i + 1}" };

    /// <summary>Which of the four slice colours slice i wears.</summary>
    public static int SliceColorIndex(int i) => Math.Clamp(LayoutSlot(i), 0, SliceColors.Length - 1);

    /// <summary>The colour a slice's chip, flag and volume row wear.</summary>
    public static string SliceColor(int i) => SliceColors[SliceColorIndex(i)];
    private static readonly string[] SliceColors = ["#F25F5C", "#F5A524", "#C084FC", "#38BDF8"];

    /// <summary>The slice colour at a colour index (what SliceColorIndex gives).</summary>
    public static string SliceColorAt(int colorIndex) => SliceColors[Math.Clamp(colorIndex, 0, SliceColors.Length - 1)];

    /// <summary>Which panadapter a slice sits on: 0 = RX1's, 1 = RX2's.</summary>
    public static int SlicePan(int i) =>
        s_twoReceivers ? Math.Clamp(i, 0, 1) : Math.Clamp(i, 0, MaxSlices - 1) % 2;

    /// <summary>The receiver whose DDC stream and zoom a panadapter shows.</summary>
    public static int PanPrimary(int pan) => Math.Clamp(pan, 0, 1);

    /// <summary>The pan's second slice, if it is on; -1 with one slice per
    /// panadapter.</summary>
    public static int PanSecondary(int pan) => s_twoReceivers ? -1 : PanPrimary(pan) + 2;

    /// <summary>The other slice on rx's panadapter, or -1 when its pan holds
    /// one slice only.</summary>
    public static int PanOther(int rx)
    {
        int pan = SlicePan(rx), first = PanPrimary(pan), second = PanSecondary(pan);
        if (second < 0) return -1;
        return rx == first ? second : rx == second ? first : -1;
    }

    /// <summary>Slice rx is its pan's second slice (A2 or B2).</summary>
    public static bool IsSecondSlice(int rx) => rx is >= 0 and < MaxSlices && rx == PanSecondary(SlicePan(rx));

    /// <summary>The slices that are on, in layout order (A1, A2, B1, B2). A1
    /// is always there: the engine never turns receiver 0 off.</summary>
    public List<int> SlicesOn()
    {
        var on = new List<int>(MaxSlices);
        foreach (int i in LayoutOrder)
            if (i == 0 || SliceOn(i)) on.Add(i);
        return on;
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ActiveSliceLabel))] [NotifyPropertyChangedFor(nameof(SplitText))]
    [NotifyPropertyChangedFor(nameof(RitUsable))]
    [NotifyPropertyChangedFor(nameof(Rx1Active))] [NotifyPropertyChangedFor(nameof(Rx2Active))]
    [NotifyPropertyChangedFor(nameof(Rx2Idle))] [NotifyPropertyChangedFor(nameof(ActiveName))]
    [NotifyPropertyChangedFor(nameof(AbLabel1))] [NotifyPropertyChangedFor(nameof(AbLabel2))]
    [NotifyPropertyChangedFor(nameof(AbActive1))] [NotifyPropertyChangedFor(nameof(AbActive2))]
    [NotifyPropertyChangedFor(nameof(AbTx1))] [NotifyPropertyChangedFor(nameof(AbTx2))]
    [NotifyPropertyChangedFor(nameof(AbOn2))] [NotifyPropertyChangedFor(nameof(ActivePan))]
    private int _activeRx;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SplitText))]
    [NotifyPropertyChangedFor(nameof(AbTx1))] [NotifyPropertyChangedFor(nameof(AbTx2))]
    // The badges' blue depends on TX too: the transmitting slice shows red instead.
    [NotifyPropertyChangedFor(nameof(AbActive1))] [NotifyPropertyChangedFor(nameof(AbActive2))]
    private int _txRx;

    /// <summary>The panadapter the controls act on: the active slice's.</summary>
    public int ActivePan => SlicePan(ActiveRx);

    // Last known state of each slice, for drawing the ones that aren't active.
    private readonly ReceiverDto?[] _slices = new ReceiverDto?[MaxSlices];
    private readonly bool[] _mute = new bool[MaxSlices];
    // The last slice made active on each pan, so its RX badge returns to it.
    private readonly int[] _lastActive = [PanPrimary(0), PanPrimary(1)];

    public bool SliceOn(int i) => i >= 0 && i < MaxSlices && (_slices[i]?.Enabled ?? false) && i < MaxSlicesAllowed;

    /// <summary>The radio's receiver ceiling (protocol/board), clamped to what the app shows.</summary>
    public int MaxSlicesAllowed => Math.Min(MaxSlices, Math.Max(2, _maxReceivers));

    private int _maxReceivers = 2;

    [ObservableProperty] private bool _fullDuplex;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSliceB))] [NotifyPropertyChangedFor(nameof(CanSwapSlices))] [NotifyPropertyChangedFor(nameof(ShowPanadapterB))]
    [NotifyPropertyChangedFor(nameof(ShowAbBadges))] [NotifyPropertyChangedFor(nameof(Rx2Active))]
    [NotifyPropertyChangedFor(nameof(Rx2Idle))] [NotifyPropertyChangedFor(nameof(ActiveName))]
    [NotifyPropertyChangedFor(nameof(AbOn2))]
    private int _sliceCount = 1;

    // Which slices were on at the last count, one bit per receiver: a
    // different set can be on with the same count, and the views still have
    // to hear of it.
    private int _sliceMask = 1;

    /// <summary>Count the slices that are on (A1 always), after _slices changed.</summary>
    private void RecountSlices()
    {
        int mask = 1, count = 1;
        for (int i = 1; i < MaxSlices; i++)
            if (SliceOn(i))
            {
                mask |= 1 << i;
                count++;
            }
        bool sameCount = count == SliceCount, moved = mask != _sliceMask;
        _sliceMask = mask;
        SliceCount = count;
        if (moved)
        {
            // Which slices the pan sets against each other goes by what is on.
            SendRxPan();
            OnPropertyChanged(nameof(RxPanTip));
        }
        if (sameCount && moved)
            foreach (string name in (string[])[nameof(SliceCount), nameof(HasSliceB), nameof(CanSwapSlices), nameof(ShowPanadapterB),
                         nameof(ShowAbBadges), nameof(Rx2Active), nameof(Rx2Idle), nameof(ActiveName), nameof(AbOn2),
                         nameof(NextSliceLabel)])
                OnPropertyChanged(name);
    }

    /// <summary>Any slice beyond A1.</summary>
    public bool HasSliceB => SliceCount > 1;

    /// <summary>A1⇄A2 has something to swap: A2 is on, or B1 with one slice per panadapter.</summary>
    public bool CanSwapSlices => SliceOn(TwoReceiversOnly ? 1 : PanSecondary(0));

    /// <summary>Slice B1 is on, so RX2's panadapter shows.</summary>
    public bool ShowPanadapterB => SliceOn(PanPrimary(1));

    // Two panadapters: RX1's holds A1/A2, RX2's B1/B2, opening when B1 comes
    // on. The RX1 and RX2 buttons beside the frequency pick the pan's slice.
    public bool Rx1Active => ActivePan == 0;
    public bool Rx2Active => ActivePan == 1 && ShowPanadapterB;
    /// <summary>RX2's pan is on but the controls are on RX1's.</summary>
    public bool Rx2Idle => ShowPanadapterB && ActivePan == 0;
    /// <summary>The slice badges beside the frequency: the active pan's two
    /// slices. None with two receivers: RX1 and RX2 pick the slice there.</summary>
    public bool ShowAbBadges => SliceCount > 1 && !TwoReceiversOnly;

    // The A1/A2 (or B1/B2) badges: the active pan's slices. The active one is
    // blue; the transmitting one wears the red edge.
    public string AbLabel1 => SliceLabel(BadgeSlice(0));
    public string AbLabel2 => SliceLabel(BadgeSlice(1));
    public bool AbOn2 => SliceOn(PanSecondary(ActivePan));
    public bool AbActive1 => ActiveRx == BadgeSlice(0) && TxRx != ActiveRx;
    public bool AbActive2 => ActiveRx == PanSecondary(ActivePan) && TxRx != ActiveRx;
    public bool AbTx1 => TxRx == BadgeSlice(0);
    public bool AbTx2 => TxRx == PanSecondary(ActivePan);
    /// <summary>The badge row's slice for one of its two buttons.</summary>
    public int BadgeSlice(int n) => Math.Clamp(n == 0 ? PanPrimary(ActivePan) : PanSecondary(ActivePan), 0, MaxSlices - 1);

    /// <summary>The active slice's label: A1, A2, B1 or B2.</summary>
    public string ActiveName => SliceLabel(ActiveRx);
    public string ActiveSliceLabel => ActiveName;
    /// <summary>Shown under the VFO slice label when transmit is on another slice.</summary>
    public string SplitText => SliceCount > 1 && TxRx != ActiveRx ? $"TX {SliceLabel(TxRx)}" : "";

    /// <summary>Raised as a property name when a slice's frequency or mode may
    /// have changed; the small VFOs on the panadapters read them again.</summary>
    public const string RxReadouts = nameof(RxReadouts);
    public long RxHz(int rx) => rx == ActiveRx ? VfoHz : _slices[Math.Clamp(rx, 0, MaxSlices - 1)]?.VfoHz ?? 0;
    public string RxModeLabel(int rx) => rx == ActiveRx ? ModeLabel
        : _slices[Math.Clamp(rx, 0, MaxSlices - 1)] is { } r ? LabelOf(r.Mode) : "";

    /// <summary>A panadapter chip's settings column for one slice: its filter
    /// (preset label, or the width when the label isn't known yet), then the
    /// lit noise blanker/reduction and AGC. Empty when that slice isn't on or
    /// its state hasn't arrived.</summary>
    public string RxSettingsLabel(int rx)
    {
        if (!SliceOn(rx)) return "";
        if (_slices[rx] is not { } r) return "";
        var parts = new List<string>(5);
        if (SliceOutOfSpan(rx)) parts.Add("out of span");
        if (r.FilterPresetName is { } slot && _filterLabels.TryGetValue(r.Mode, out var labels)
            && labels.TryGetValue(slot, out string? label))
            parts.Add(label);
        else
            parts.Add(FilterWidthLabel(r.FilterLowHz, r.FilterHighHz));
        if (r.Nr?.NbMode is NbMode nb and not NbMode.Off) parts.Add("NB");
        if (r.NrMode is NrMode nr and not NrMode.Off) parts.Add(NrShortLabel(nr));
        parts.Add(r.AutoAgcEnabled ? "AGC auto" : $"AGC {SpeedName(r.Agc?.Mode ?? AgcMode.Med).ToLowerInvariant()}");
        return string.Join(" · ", parts);
    }

    /// <summary>A filter's width the way the preset buttons label it: 2.7k, 500…</summary>
    private static string FilterWidthLabel(int low, int high)
    {
        int width = Math.Abs(high - low);
        return width >= 1000 ? $"{width / 1000.0:0.#}k" : $"{width}";
    }

    private static string NrShortLabel(NrMode mode) => mode switch
    {
        NrMode.Anr => "NR1", NrMode.Emnr => "NR2", NrMode.Rnnr => "NR3",
        NrMode.Sbnr => "NR4", NrMode.Nnr => "NNR", _ => mode.ToString(),
    };

    /// <summary>The RX1 and RX2 buttons: pick the panadapter, turning it on if
    /// it isn't. The pan's most recently active slice takes the controls.</summary>
    public async Task SelectRxAsync(int pan)
    {
        if (!IsConnected) return;
        int first = PanPrimary(pan);
        if (!SliceOn(first) && SliceTowards(first) >= 0) await TurnOnSliceAsync(first, VfoHz);
        if (!SliceOn(first)) return;   // refused: the status line says why
        int want = SliceOn(_lastActive[pan]) && SlicePan(_lastActive[pan]) == pan ? _lastActive[pan] : first;
        if (want != ActiveRx) await MakeActiveAsync(want);
    }

    /// <summary>A frequency typed into a panadapter's small VFO: that slice
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

    // ---- per-slice AF ----------------------------------------------------

    /// <summary>A volume row per on-slice: the main AF is the master, these
    /// are trims. Kept in step with the slices that are on.</summary>
    public ObservableCollection<SliceAfRow> SliceAfs { get; } = [];

    private readonly SliceAfRow[] _sliceAfRows = new SliceAfRow[MaxSlices];
    private readonly Latest<double>?[] _sliceAfSenders = new Latest<double>?[MaxSlices];
    private bool _applyingSliceAf;
    private DateTime _sliceAfMovedAt;

    private void SyncSliceAfRows()
    {
        for (int i = 0; i < MaxSlices; i++)
        {
            _sliceAfRows[i] ??= new SliceAfRow(this, i);
            bool on = SliceOn(i);
            bool shown = SliceAfs.Contains(_sliceAfRows[i]);
            if (on && !shown) SliceAfs.Add(_sliceAfRows[i]);
            else if (!on && shown) SliceAfs.Remove(_sliceAfRows[i]);
        }
    }

    internal void SetSliceAfLocal(int rx, double db)
    {
        _applyingSliceAf = true;
        _sliceAfRows[rx]?.SetLocal(db);
        _applyingSliceAf = false;
    }

    /// <summary>A slice's volume slider moved (SliceAfRow calls here).</summary>
    internal void SendSliceAf(int rx, double db)
    {
        if (_applyingSliceAf || !IsConnected) return;
        _sliceAfMovedAt = DateTime.UtcNow;
        (_sliceAfSenders[rx] ??= new Latest<double>(v =>
            _client!.SetReceiverAsync(rx, new ReceiverSetRequest(AfGainDb: v)))).Send(db);
    }

    /// <summary>Nudge a slice's AF trim (the G2 panel's A/B AF knobs).</summary>
    internal void NudgeSliceAf(int rx, double delta)
    {
        if (_sliceAfRows[rx] is not { } row) return;
        row.Value = Math.Clamp(row.Value + delta, -40, 20);
    }

    // The pan under the volume sliders: RX1's panadapter (A1, A2) towards
    // one ear and RX2's (B1, B2) towards the other; with RX2 off, A1 towards
    // one ear and A2 towards the other. The engine keeps it only while it
    // runs, so it's sent on every connect, and again when the slices that
    // are on change, since the sides go with it.
    private Latest<PanOrder>? _rxPanSender;
    private readonly record struct PanOrder(double Pan, int S1, int S2, int S3);

    /// <summary>Each receiver's side of the pan: +1 with A1, -1 opposite, 0 centred.</summary>
    private int[] RxPanSides()
    {
        var sides = new int[MaxSlices];
        sides[0] = 1;
        bool panB = ShowPanadapterB;
        for (int i = 1; i < MaxSlices; i++)
            sides[i] = !SliceOn(i) ? 0
                : panB ? (SlicePan(i) == 0 ? 1 : -1)
                : -1;   // RX2 off: whatever is on besides A1 is A2
        return sides;
    }

    /// <summary>-100 = RX1 (or A1) in the left ear and RX2 (or A2) in the
    /// right, 0 = all in both ears, +100 the other way round.</summary>
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
            // RX1's slices against RX2's; with RX2 off, A1 against A2.
            bool panB = ShowPanadapterB || SliceCount < 2;
            string one = panB ? "RX1" : SliceLabel(PanPrimary(0)), other = panB ? "RX2" : SliceLabel(PanSecondary(0));
            if (pan == 0) return $"Pan: {one} and {other} in both ears. Slide to split them left and right.";
            string where = Math.Abs(pan) >= 1 ? "" : $" ({Math.Abs(pan) * 100:0}%)";
            return pan < 0
                ? $"Pan: {one} left, {other} right{where}"
                : $"Pan: {one} right, {other} left{where}";
        }
    }

    private void SendRxPan()
    {
        if (!IsConnected) return;
        _rxPanSender ??= new Latest<PanOrder>(o =>
            _client?.SetRxPanAsync(o.Pan, [1, o.S1, o.S2, o.S3]) ?? Task.CompletedTask);
        int[] sides = RxPanSides();
        _rxPanSender.Send(new PanOrder(EngineRxPan, sides[1], sides[2], sides[3]));
    }

    // ---- marks for the panadapters ---------------------------------------

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
        var marks = new List<SliceMark>(SliceCount);
        int shift = CwShift(Mode);
        foreach (int i in SlicesOn())
        {
            long hz;
            int low, high;
            if (i == ActiveRx)
            {
                (hz, low, high) = (VfoHz, FilterLowHz + shift, FilterHighHz + shift);
            }
            else if (_slices[i] is { } r)
            {
                int s = CwShift(r.Mode);
                (hz, low, high) = (r.VfoHz, r.FilterLowHz + s, r.FilterHighHz + s);
            }
            else continue;
            marks.Add(new SliceMark(i, hz, low, high, SliceLabel(i), i == ActiveRx, TxRx == i, _mute[i], SliceOutOfSpan(i)));
        }
        if (marks.Count == 0)
            marks.Add(new SliceMark(0, VfoHz, FilterLowHz + shift, FilterHighHz + shift, SliceLabel(0), true, true, false));
        return marks;
    }

    /// <summary>Marks for one of the two panadapters (0 = RX1's, 1 = RX2's).
    /// With the pans on different bands each shows only its own slices: the
    /// other's flags would just sit at the edge pointing off the display.</summary>
    public IReadOnlyList<SliceMark> PanMarks(int panRx)
    {
        var marks = SliceMarks();
        if (!ShowPanadapterB || marks.Count < 2) return marks;
        long a = _slices[PanPrimary(0)]?.VfoHz ?? 0, b = _slices[PanPrimary(1)]?.VfoHz ?? 0;
        if (SameBand(a, b)) return marks;
        return [.. marks.Where(m => SlicePan(m.Rx) == panRx)];
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
        // Shared streams on a Protocol 2 radio with two receivers (or any
        // Protocol 2 radio, with the test switch), unless this engine can't.
        // The engine only offers receivers 2 and 3 below its ceiling for the board.
        bool shared = _radio is { Protocol2: true } && (RadioHasTwoReceivers || SharedSlicesForced)
            && s.MaxReceivers >= MaxSlices && !_sharingUnsupported;
        _maxReceivers = shared ? MaxSlices : RadioHasTwoReceivers ? 2 : Math.Clamp(s.MaxReceivers, 2, MaxSlices);
        SetLayout(two: s.MaxReceivers <= 2 || RadioHasTwoReceivers, shared: shared);
        if (s.RadioLoHz > 0) _streamCentre[0] = s.RadioLoHz;
        for (int i = 0; i < MaxSlices; i++)
        {
            _slices[i] = s.Receivers?.FirstOrDefault(r => r.Index == i);
            if (_slices[i] is { } r)
            {
                _mute[i] = r.Muted;
                _sliceAfRows[i]?.SetMuted(r.Muted);
                // One that doesn't fit the layout is on its way off: not counted.
                if (r.Enabled && !FitsLayout(r)) _slices[i] = r with { Enabled = false };
            }
        }
        RecountSlices();
        TurnOffMisfitSlices(s);
        if (ActiveRx != 0 && !SliceOn(ActiveRx)) _ = MakeActiveAsync(0);
        OnPropertyChanged(RxReadouts);
        FullDuplex = s.FullDuplexMultiRxEnabled;
        // The control bank shows the active slice's AGC and noise block;
        // the other slices' live on their panadapter chips.
        var view = RxSettingsOf(s, ActiveRx);
        UpdateAgc(view);
        UpdateNoise(view);
        UpdateOperating(s);
        var on = SlicesOn();
        foreach (int i in on)
            if (_slices[i] is { } r) EnsureFilterLabels(r.Mode);
        _ = FollowRxFilterAsync(s);
        // Follow the engine's slice AF unless the operator just moved a slider.
        if (DateTime.UtcNow - _sliceAfMovedAt > TimeSpan.FromSeconds(2))
            foreach (int i in on)
                if (_slices[i] is { } r) SetSliceAfLocal(i, r.AfGainDb);
        SyncSliceAfRows();
        // Each panadapter zooms as one: the second slice on a pan is kept at
        // the pan's zoom, and RX2's pan follows its first slice's engine zoom.
        if (SliceOn(PanSecondary(0))) SyncSecondaryZoom(0);
        if (ShowPanadapterB) FollowEngineZoomB(_slices[PanPrimary(1)]?.ZoomLevel ?? 1);
        if (SliceOn(PanSecondary(1))) SyncSecondaryZoom(1);
        // Transmit on a slice the app doesn't count as on goes back to A1.
        TxRx = SliceOn(s.TxReceiverIndex) ? s.TxReceiverIndex : 0;
        // A TX slice that isn't the active one is only seen here: follow its
        // band and mode. The active slice's own changes come through VfoHz and
        // Mode, and while it's being switched VfoHz can still be the other's.
        if (TxRx != ActiveRx) FollowTxSliceForPs();
        SyncSliceInputs();
        CheckAttSource();
        CheckOutOfSpan();
    }

    partial void OnTxRxChanged(int value) => FollowTxSliceForPs();

    /// <summary>The radio's discovery reply leaves room for two receivers only.
    /// The engine's ceiling comes from a table by board and can be higher: a
    /// Brick 3 is Angelia-class (8 DDCs in the table, the first two reserved
    /// for PureSignal) but has 4, so receivers 2 and up get no IQ at all.
    /// Hermes-class boards (Hermes, Hermes II, G2E) reserve none.</summary>
    private bool RadioHasTwoReceivers =>
        _radio is { Protocol2: true, Ddcs: int ddcs }
        && ddcs - (_radio.BoardId is 0x01 or 0x02 or 0x14 ? 0 : 2) <= 2;

    /// <summary>Switch between one slice per panadapter and two, and between
    /// second slices with DDCs of their own and on their pan's stream, when
    /// the connected radio says so.</summary>
    private void SetLayout(bool two, bool shared)
    {
        two &= !shared;
        if (s_twoReceivers == two && s_sharedSlices == shared) return;
        s_twoReceivers = two;
        s_sharedSlices = shared;
        _lastActive[0] = 0;
        _lastActive[1] = PanPrimary(1);
        LayoutChangedForJack();
        foreach (string name in (string[])[nameof(SliceCount), nameof(ShowPanadapterB), nameof(HasSliceB), nameof(CanSwapSlices),
                     nameof(ShowAbBadges), nameof(ActivePan), nameof(Rx1Active), nameof(Rx2Active), nameof(Rx2Idle),
                     nameof(ActiveName), nameof(ActiveSliceLabel), nameof(SplitText), nameof(AbLabel1), nameof(AbLabel2),
                     nameof(AbOn2), nameof(AbActive1), nameof(AbActive2), nameof(AbTx1), nameof(AbTx2), nameof(NextSliceLabel)])
            OnPropertyChanged(name);
        foreach (var row in _sliceAfRows) row?.Relabel();
        OnPropertyChanged(RxReadouts);
    }

    /// <summary>A receiver the engine has on fits the layout: with one slice
    /// per panadapter only receivers 0 and 1; with shared streams A2 and B2
    /// only on A1's and B1's streams; otherwise A2 and B2 with DDCs of their
    /// own.</summary>
    private static bool FitsLayout(ReceiverDto r) =>
        r.Index < 2 || (SharedSlices ? r.SourceReceiver == StreamOf(r.Index) && SharesStream(r.Index)
            : !TwoReceiversOnly && r.SourceReceiver is null);

    // Receivers left on in the engine from an earlier version, session or
    // layout that don't fit this one: receivers 2 and 3 on a radio with one
    // slice per panadapter would only cost DSP (and hold transmit, if it was
    // moved there); a shared-stream one where the second slices have DDCs of
    // their own, or one with its own DDC (or the wrong stream) where they
    // share, would also block the right kind from coming on. Off, B2 first,
    // once each per connection.
    private readonly bool[] _unusableOffSent = new bool[MaxSlices];
    // The slice TurnOnSliceAsync is turning on right now, or -1. Its own
    // check decides whether the engine took it; the poll's clean-up keeps
    // its hands off meanwhile, or an engine that can't share streams would
    // look like one that merely refused.
    private int _sliceComingOn = -1;

    private void TurnOffMisfitSlices(StateDto s)
    {
        // Runs on the connect pass too, before IsConnected is set: the client is there.
        if (_client is null) return;
        var on = new List<int>();
        for (int i = MaxSlices - 1; i >= 2; i--)
            if (i != _sliceComingOn && !_unusableOffSent[i] && s.Receivers?.FirstOrDefault(r => r.Index == i) is { Enabled: true } r && !FitsLayout(r))
            {
                _unusableOffSent[i] = true;
                on.Add(i);
            }
        if (on.Count == 0) return;
        _ = Try(async () =>
        {
            foreach (int i in on)
                await _client!.SetReceiverAsync(i, new ReceiverSetRequest(Enabled: false));
        });
    }

    /// <summary>A new connection: unusable slices may need turning off again.</summary>
    private void ResetUnusableSlices()
    {
        Array.Clear(_unusableOffSent);
        _sharingUnsupported = false;
        Array.Clear(_streamCentre);
    }

    /// <summary>One slice's mode, filter, noise and AGC settings as the engine
    /// reports them. The engine's receiver entries carry everything; an engine
    /// without them (RX1 only) mirrors the same values in the global state
    /// fields, which are the fallback.</summary>
    private sealed record RxSettings(
        RxMode Mode, int FilterLowHz, int FilterHighHz, string? FilterPresetName,
        NrConfig? Nr, AgcConfig? Agc, double AgcTopDb, bool AutoAgcEnabled);

    /// <summary>Receiver rx's settings from an engine state snapshot.</summary>
    private RxSettings RxSettingsOf(StateDto s, int rx)
    {
        if (s.Receivers?.FirstOrDefault(r => r.Index == rx) is { } r)
            return new(r.Mode, r.FilterLowHz, r.FilterHighHz, r.FilterPresetName,
                r.Nr ?? s.Nr, r.Agc ?? s.Agc, r.AgcTopDb, r.AutoAgcEnabled);
        return new(s.Mode, s.FilterLowHz, s.FilterHighHz, s.FilterPresetName,
            s.Nr, s.Agc, s.AgcTopDb, s.AutoAgcEnabled);
    }

    /// <summary>The engine's view of the active slice.</summary>
    private (long Hz, RxMode Mode, int Low, int High, string? Preset) ActiveOf(StateDto s)
    {
        if (ActiveRx > 0 && s.Receivers?.FirstOrDefault(r => r.Index == ActiveRx) is { } b)
        {
            var (bl, bh) = FreeDvEdges(b.Mode, b.VfoHz, b.FilterLowHz, b.FilterHighHz);
            return (b.VfoHz, b.Mode, bl, bh, b.FilterPresetName);
        }
        var (lo, hi) = FreeDvEdges(s.Mode, s.VfoHz, s.FilterLowHz, s.FilterHighHz);
        return (s.VfoHz, s.Mode, lo, hi, s.FilterPresetName);
    }

    /// <summary>Point the VFO, band, mode, filter and AF at a slice (0..3).</summary>
    public async Task MakeActiveAsync(int rx)
    {
        if (!IsConnected || rx < 0 || (rx != 0 && !SliceOn(rx))) return;
        // Until the engine's state arrives, mode and filter are still the other
        // slice's: wheel tuning on a panadapter waits for this to finish.
        _switchingRx++;
        try
        {
            bool changing = rx != ActiveRx;
            ActiveRx = rx;
            _lastActive[SlicePan(rx)] = rx;
            // The meter follows the active slice: drop the other one's reading and peak.
            if (changing) (SignalDbm, PeakDbm, SUnits) = (-140, -140, "S0");
            // Show the new slice's last known frequency straight away, so
            // nothing reads the other one's under the new slice's name.
            if (changing && _slices[rx] is { } known)
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

    /// <summary>More than 0 while MakeActiveAsync is fetching the new active slice's state.</summary>
    private int _switchingRx;

    // ---- adding and removing slices ---------------------------------------

    // The layout's rules for which slice comes on and off next: the slices
    // come and go in any order; only B2 needs B1 (RX2) on, and B1 comes off
    // after B2.

    /// <summary>The slice Add Slice would turn on, or -1 at the ceiling: the
    /// first that is off of A2, B1 (which opens RX2's panadapter), B2.</summary>
    public int NextSlice()
    {
        foreach (int i in LayoutOrder)
            if (i != 0 && i < MaxSlicesAllowed && !SliceOn(i) && SliceMayComeOn(i)) return i;
        return -1;
    }

    /// <summary>A second slice needs its pan's first slice on (B2 needs B1;
    /// A1 is always on).</summary>
    private bool SliceMayComeOn(int i)
    {
        int first = PanPrimary(SlicePan(i));
        return !IsSecondSlice(i) || first == 0 || SliceOn(first);
    }

    /// <summary>Slice target (which is off), if it may come on now; -1 past
    /// the radio's ceiling or while it waits for its pan's first slice.</summary>
    private int SliceTowards(int target) => target < MaxSlicesAllowed && SliceMayComeOn(target) ? target : -1;

    /// <summary>The slice that has to come off before rx can (B2 before B1),
    /// or -1 when rx itself may come off now.</summary>
    private int SliceOffBefore(int rx) =>
        rx == PanPrimary(1) && SliceOn(PanSecondary(1)) ? PanSecondary(1) : -1;

    /// <summary>Slice rx is on and may come off now (A1 never does).</summary>
    public bool MayRemoveSlice(int rx) => rx > 0 && SliceOn(rx) && SliceOffBefore(rx) < 0;

    /// <summary>The second slice of a pan the panadapter menu can add there
    /// (A2, or B2 while B1 is on and the radio has a receiver for it), or -1.</summary>
    public int SecondSliceToAdd(int pan)
    {
        int i = PanSecondary(pan);
        return i >= 0 && i < MaxSlicesAllowed && !SliceOn(i) && SliceMayComeOn(i) ? i : -1;
    }

    /// <summary>RX2's panadapter can be turned on (its first slice).</summary>
    public bool CanTurnOnPanB => SliceTowards(PanPrimary(1)) >= 0;

    /// <summary>The label of the slice Add Slice would turn on, or "" at the ceiling.</summary>
    public string NextSliceLabel => NextSlice() is >= 0 and int next ? SliceLabel(next) : "";
    public bool CanAddSlice => IsConnected && NextSlice() >= 0;

    /// <summary>Add the next slice at a frequency, in the active slice's mode
    /// and filter, and make it active. The radio's receiver ceiling wins over
    /// the ask: the status line says so.</summary>
    public Task AddSliceAsync(long hz) => TurnOnSliceAsync(NextSlice(), hz);

    /// <summary>Add a particular slice (the panadapter menu's A2 or B2).</summary>
    public Task AddSliceAsync(int slice, long hz) => TurnOnSliceAsync(slice, hz);

    /// <summary>Slice i runs on its pan's first slice's stream, with no DDC of its own.</summary>
    public static bool SharesStream(int i) => SharedSlices && IsSecondSlice(i);

    /// <summary>The slice whose stream (and so input) slice i hears.</summary>
    private static int StreamOf(int i) => SharesStream(i) ? PanPrimary(SlicePan(i)) : i;

    /// <summary>Turn slice i on at a frequency, in the active slice's mode and
    /// filter, and make it active; -1 or a slice past the radio's ceiling puts
    /// the reason on the status line instead.</summary>
    private async Task TurnOnSliceAsync(int i, long hz)
    {
        if (!IsConnected) return;
        if (i < 0 || i >= MaxSlicesAllowed)
        {
            Status = SliceCount >= MaxSlices
                ? "Four slices is as many as this app offers."
                : TwoReceiversOnly
                    ? "This radio has two receivers, RX1 and RX2: no room for a second slice on either."
                    : i >= 0 ? $"This radio offers {_maxReceivers} receivers: no room for {SliceLabel(i)}."
                    : $"This radio offers {_maxReceivers} receivers, and all are on.";
            return;
        }
        if (SliceOn(i)) return;
        // The pan's first slice sets its zoom; a second slice starts there too.
        int zoom = EngineZoom(SlicePan(i));
        bool shares = SharesStream(i);
        int source = StreamOf(i);
        // A slice on another's stream has no input of its own, and has to
        // start inside that stream's span: else on the pan's first slice.
        byte? adc = shares ? null : WantsJack(i) ? (byte)1 : (byte)0;
        if (shares && !InsideSpan(source, hz)) hz = RxHz(source);
        // The new slice comes on as loud as the one being listened to, not at
        // 0 dB whatever the others are set to.
        double af = _sliceAfRows[ActiveRx]?.Value ?? _slices[ActiveRx]?.AfGainDb ?? 0;
        await Try(async () =>
        {
            bool rx2WasOn = SliceOn(PanPrimary(1));
            if (shares) _sliceComingOn = i;
            try
            {
                await _client!.SetReceiverAsync(i, new ReceiverSetRequest(
                    Enabled: true, VfoHz: hz, Mode: Mode, FilterLowHz: FilterLowHz, FilterHighHz: FilterHighHz,
                    AfGainDb: af, FilterPresetName: Filters.FirstOrDefault(f => f.IsSelected)?.SlotName,
                    ZoomLevel: zoom, Agc: new AgcConfig(_agcMode), AutoAgcEnabled: AgcAuto, AdcSource: adc,
                    SourceReceiver: shares ? source : null));
                if (shares && !await SharingTookAsync(i, source, rx2WasOn)) return;
            }
            finally { _sliceComingOn = -1; }
            _slices[i] = (_slices[i] ?? new ReceiverDto(i, true, adc ?? 0, hz, Mode, FilterLowHz, FilterHighHz,
                Filters.FirstOrDefault(f => f.IsSelected)?.SlotName, af, _sampleRateHz))
                with { Enabled = true, VfoHz = hz, AfGainDb = af, SourceReceiver = shares ? source : null };
            // The engine has it on now: count it here rather than waiting for
            // the poll, so the views and MakeActiveAsync know it at once.
            RecountSlices();
            SyncSliceAfRows();
            SetSliceAfLocal(i, af);
            await MakeActiveAsync(i);
            Status = $"{SliceLabel(i)} on, {hz / 1e6:0.000000} MHz.";
        });
    }

    /// <summary>After asking for slice i on source's stream: did the engine
    /// do that? An engine without shared streams turns on a receiver with a
    /// DDC of its own instead (and RX2 with it): that is undone, and this
    /// connection goes back to one slice per panadapter. A refusal leaves
    /// things as they were. Either way the status line says so.</summary>
    private async Task<bool> SharingTookAsync(int i, int source, bool rx2WasOn)
    {
        var s = await _client!.GetStateAsync();
        var row = s.Receivers?.FirstOrDefault(r => r.Index == i);
        if (row is { Enabled: true, SourceReceiver: int got } && got == source) return true;
        if (row is { Enabled: true })
        {
            await _client.SetReceiverAsync(i, new ReceiverSetRequest(Enabled: false));
            if (row.SourceReceiver is null)
            {
                if (!rx2WasOn) await _client.SetReceiverAsync(PanPrimary(1), new ReceiverSetRequest(Enabled: false));
                _sharingUnsupported = true;
                s = await _client.GetStateAsync();
                UpdateSlices(s);
                Status = $"This engine can't feed a slice from another receiver's stream, so {SliceLabel(i)} stays off.";
                return false;
            }
            s = await _client.GetStateAsync();
        }
        UpdateSlices(s);
        Status = $"{SliceLabel(i)} couldn't be turned on: the engine refused it.";
        return false;
    }

    /// <summary>Remove a slice, if it may come off now (B1 only once B2 is
    /// off); RemovePanBAsync turns RX2 off with both its slices.</summary>
    public async Task RemoveSliceAsync(int rx)
    {
        if (!IsConnected || rx <= 0 || !SliceOn(rx)) return;
        if (SliceOffBefore(rx) is >= 0 and int first)
        {
            Status = $"{SliceLabel(first)} came on after {SliceLabel(rx)}: it comes off first.";
            return;
        }
        await TurnOffSliceAsync(rx);
    }

    /// <summary>Turn slice rx off, moving transmit and the controls to A1 first.</summary>
    private async Task TurnOffSliceAsync(int rx)
    {
        // Turning a receiver off makes the engine move transmit to RX1 at
        // once, even mid-transmission, so this has to be checked first.
        if (IsTransmitting && TxRx == rx)
        {
            Status = $"Unkey before turning {SliceLabel(rx)} off.";
            return;
        }
        await Try(async () =>
        {
            // TX back to A1 first; if the engine refuses, the slice stays on.
            if (TxRx == rx && !await MoveTxAsync(0)) return;
            if (ActiveRx == rx) await MakeActiveAsync(0);
            await _client!.SetReceiverAsync(rx, new ReceiverSetRequest(Enabled: false));
            _slices[rx] = _slices[rx] is { } old ? old with { Enabled = false } : null;
            // RX2 off takes B2 with it, in the engine too.
            if (!TwoReceiversOnly && rx == PanPrimary(1) && PanSecondary(1) is int b2 && _slices[b2] is { Enabled: true } on)
            {
                _slices[b2] = on with { Enabled = false };
                if (ActiveRx == b2) await MakeActiveAsync(0);
                if (TxRx == b2) TxRx = 0;
            }
            RecountSlices();
            if (TxRx == rx) TxRx = 0;
            Status = $"{SliceLabel(rx)} off.";
        });
    }

    /// <summary>Turn RX2 off: both its slices come off, B2 first.</summary>
    public async Task RemovePanBAsync()
    {
        if (!IsConnected || !ShowPanadapterB) return;
        while (ShowPanadapterB)
        {
            int rx = SliceOn(PanSecondary(1)) ? PanSecondary(1) : PanPrimary(1);
            await RemoveSliceAsync(rx);
            if (SliceOn(rx)) return;   // refused (keyed): leave the rest on
        }
    }

    /// <summary>The ✕ on a panadapter chip: the slice itself when it may come
    /// off now, otherwise the pan's whole stack.</summary>
    public Task RemoveSliceOrPanAsync(int rx) =>
        MayRemoveSlice(rx) ? RemoveSliceAsync(rx) : RemovePanBAsync();

    /// <summary>Transmit on a slice: its frequency, mode and filter.</summary>
    public async Task TransmitOnAsync(int rx)
    {
        if (!IsConnected) return;
        if (!SliceOn(rx))
        {
            Status = $"{SliceLabel(rx)} isn't on.";
            return;
        }
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
            Status = $"Transmit on {SliceLabel(rx)}.";
        });
    }

    /// <summary>Move transmit to a slice unless the radio is keyed: the engine
    /// checks too, in case it was keyed after the app last looked. False, with
    /// the reason on the status line, if it refused.</summary>
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
                ? $"Transmit stays where it is: {SliceLabel(rx)} isn't on."
                : "Unkey before moving transmit.";
            return false;
        }
    }

    public async Task SetSliceMuteAsync(int rx, bool muted)
    {
        await Try(async () =>
        {
            await _client!.SetReceiverMuteAsync(rx, muted);
            if ((uint)rx < MaxSlices)
            {
                _mute[rx] = muted;
                _sliceAfRows[rx]?.SetMuted(muted);
            }
        });
    }

    public bool IsSliceMuted(int rx) => rx is >= 0 and < MaxSlices && _mute[rx];

    // ---- panadapter zoom and LO -------------------------------------------

    // ---- staying inside the span (shared streams) --------------------------

    // Each pan's stream centre (receiver 0's and 1's DDC), 0 until known: from
    // the engine's VFO messages, receiver 0's from the state poll as well, and
    // receiver 1's from its display frames, which are centred on it.
    private readonly long[] _streamCentre = new long[2];

    /// <summary>How far from its stream's centre a dial can be tuned and still
    /// be received well, and how far a pan's picture can be dragged.</summary>
    private long SpanReach => (long)(_sampleRateHz * 0.43);

    // The engine silences a shared-stream slice past this, so the mark agrees with the audio.
    private long EngineSpanLimit => (long)(_sampleRateHz * 0.45);

    /// <summary>A stream's centre has moved (or is known for the first time).</summary>
    private void NoteStreamCentre(int rx, long hz)
    {
        if ((uint)rx < 2 && hz > 0) _streamCentre[rx] = hz;
    }

    /// <summary>A frequency a slice on stream's stream can be tuned to: inside
    /// the reach, or anywhere while the centre isn't known yet.</summary>
    private bool InsideSpan(int stream, long hz) =>
        (uint)stream >= 2 || _streamCentre[stream] <= 0 || _sampleRateHz <= 0
        || Math.Abs(hz - _streamCentre[stream]) <= SpanReach;

    /// <summary>A shared-stream slice whose dial is outside its stream's span:
    /// the engine keeps it silent there.</summary>
    public bool SliceOutOfSpan(int rx)
    {
        if (!SharesStream(rx) || !SliceOn(rx) || _slices[rx] is not { } r) return false;
        if (r.OutOfSpan) return true;
        long centre = _streamCentre[StreamOf(rx)];
        if (centre <= 0 || _sampleRateHz <= 0) return false;
        // The engine measures from the dial's carrier side in CW, as the panadapter does.
        long dial = RxHz(rx) + CwShift(rx == ActiveRx ? Mode : r.Mode);
        return Math.Abs(dial - centre) > EngineSpanLimit;
    }

    private readonly bool[] _outOfSpanShown = new bool[MaxSlices];

    private string OutOfSpanText(int rx) =>
        $"{SliceLabel(rx)} is outside RX{SlicePan(rx) + 1}'s span: silent until RX{SlicePan(rx) + 1} covers it again.";

    /// <summary>Say once when a shared-stream slice leaves its span, and take
    /// that back when it returns; the chips and panadapter tags follow.</summary>
    private void CheckOutOfSpan()
    {
        bool changed = false;
        for (int i = 0; i < MaxSlices; i++)
        {
            bool now = SliceOutOfSpan(i);
            if (now == _outOfSpanShown[i]) continue;
            _outOfSpanShown[i] = now;
            changed = true;
            string text = OutOfSpanText(i);
            if (now) Status = text;
            else if (Status == text) Status = "";
        }
        if (changed) OnPropertyChanged(RxReadouts);
    }

    // The status line said the active slice is pinned at its span's edge:
    // not again while it stays there.
    private bool _pinnedSaid;

    /// <summary>Keep a tuning request for the active slice inside its stream's
    /// span when it runs on another slice's stream; the first time it stops
    /// at the edge the status line says why.</summary>
    private long KeepInsideSpan(long hz)
    {
        int stream = StreamOf(ActiveRx);
        if (!SharesStream(ActiveRx) || InsideSpan(stream, hz))
        {
            _pinnedSaid = false;
            return hz;
        }
        long centre = _streamCentre[stream];
        long kept = Math.Clamp(hz, centre - SpanReach, centre + SpanReach);
        if (!_pinnedSaid)
        {
            _pinnedSaid = true;
            string pan = $"RX{SlicePan(ActiveRx) + 1}";
            Status = $"{SliceLabel(ActiveRx)} stays inside {pan}'s span: tune {pan} to go further.";
        }
        return kept;
    }

    private Task SyncSecondaryZoom(int pan)
    {
        int secondary = PanSecondary(pan);
        int want = EngineZoom(pan);
        if (!SliceOn(secondary) || _slices[secondary]?.ZoomLevel == want || !IsConnected) return Task.CompletedTask;
        return Try(() => _client!.SetReceiverAsync(secondary, new ReceiverSetRequest(ZoomLevel: want)));
    }

    /// <summary>Remember a filter this side just sent, so the panadapter
    /// chips show it before the next poll refreshes the cached copies.</summary>
    private void NoteFilterSent(int rx, int low, int high, string? preset)
    {
        if ((uint)rx < MaxSlices && _slices[rx] is { } r)
            _slices[rx] = r with { FilterLowHz = low, FilterHighHz = high, FilterPresetName = preset };
        OnPropertyChanged(RxReadouts);
    }

    /// <summary>Preset label per mode for the panadapter chips, filled from
    /// the engine as each mode's presets are fetched.</summary>
    private readonly Dictionary<RxMode, IReadOnlyDictionary<string, string>> _filterLabels = [];
    private readonly HashSet<RxMode> _fetchingLabels = [];

    private void EnsureFilterLabels(RxMode mode)
    {
        if (_filterLabels.ContainsKey(mode) || !_fetchingLabels.Add(mode)) return;
        _ = FetchFilterLabelsAsync(mode);
    }

    private async Task FetchFilterLabelsAsync(RxMode mode)
    {
        try
        {
            var presets = await _client!.GetFilterPresetsAsync(mode);
            _filterLabels[mode] = presets.Where(p => FilterSlotNames.Contains(p.SlotName))
                .ToDictionary(p => p.SlotName, p => p.Label);
            OnPropertyChanged(RxReadouts);
        }
        catch (Exception) { }
        finally { _fetchingLabels.Remove(mode); }
    }

    private int _sampleRateHz;
    private readonly Latest<long>?[] _panSenders = new Latest<long>?[2];

    /// <summary>How long a drag rests at the end of the span before the dial recentres.</summary>
    private static readonly TimeSpan PanHold = TimeSpan.FromMilliseconds(700);

    private const int PanStepMs = 35;
    private DispatcherTimer? _panHold;
    private int _panRx;
    private long _panWanted, _panShown;
    private bool _panReleased;

    /// <summary>A panadapter dragged: move that pan's display centre, not its
    /// dial. The pan's first slice owns the display; a drag reaching the edge
    /// of what it captures stops there. With CTUN on, a drag reaching the
    /// limit moves the active slice's dial to the middle of the display after
    /// a short pause: the picture stays put and the drag has room to carry
    /// on. A locked dial or a keyed radio doesn't move. The engine ignores
    /// this while keyed.</summary>
    public void PanTo(int pan, long centerHz)
    {
        if (!IsConnected) return;
        _panReleased = false;
        int primary = PanPrimary(pan);
        long dial = primary == ActiveRx ? VfoHz : _slices[primary]?.VfoHz ?? VfoHz;
        long reach = SpanReach;
        long shown = Math.Clamp(centerHz, dial - reach, dial + reach);
        // At most one move per engine tick (33 ms): the radio retunes at once but
        // the engine re-aligns the audio only on its tick, so a fast drag sending
        // at mouse rate stacked several retunes per tick and the audio warbled.
        (_panSenders[pan] ??= new Latest<long>(async hz =>
        {
            await _client!.SetReceiverLoAsync(primary, hz);
            await Task.Delay(PanStepMs);
        })).Send(shown);

        (_panRx, _panWanted, _panShown) = (pan, centerHz, shown);
        if (shown != centerHz && pan == ActivePan && CtunOn && !VfoLocked && !IsTransmitting)
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
            if (!IsConnected || _panRx != ActivePan || !CtunOn || VfoLocked || IsTransmitting) return;
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
    /// of view. Each panadapter centres on its first slice.</summary>
    private async Task CenterOnDialAsync(int pan)
    {
        // 2-TONE / PS CAL on a pan's second slice (A2, B2): the picture stays
        // centred on that slice's TX frequency for the run, zoom steps included.
        if (_imdPan == pan && _imdCentreHz is { } txHz)
        {
            await _client!.SetReceiverLoAsync(PanPrimary(pan), txHz);
            return;
        }
        // RX1's display keeps the dial where its Centre slider puts it (centred
        // with CTUN off — the offset only means something with CTUN on).
        if (pan == 0) await _client!.SetDialPositionAsync(EffectiveDialPositionOf(0) / 100.0);
        else if (ShowPanadapterB)
            await _client!.SetReceiverLoAsync(PanPrimary(1), ActiveRx == PanPrimary(1) ? VfoHz : _slices[PanPrimary(1)]?.VfoHz ?? VfoHz);
    }

    /// <summary>A click or wheel on a panadapter tunes the slice already
    /// active on that pan, else the pan's first slice: the pan shouldn't
    /// steal the focus from its second slice.</summary>
    public int TuneTargetFor(int pan) => ActivePan == pan && SliceOn(ActiveRx) ? ActiveRx : PanPrimary(pan);

    /// <summary>A click or wheel on a slice's own panadapter: make that slice
    /// active first, then tune it. One arriving while the active slice is
    /// still being switched is dropped: the VFO may still hold the other
    /// slice's frequency, and steps from it would land on the wrong band.</summary>
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
