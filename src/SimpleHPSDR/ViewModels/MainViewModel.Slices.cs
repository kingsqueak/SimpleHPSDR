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
/// index — A1 and A2 on RX1's panadapter (receivers 0 and 1), B1 and B2 on
/// RX2's (receivers 2 and 3). Receivers must be contiguous, so slices come on
/// in that order and go off in reverse. The active slice is the one the VFO,
/// band, mode, filter, AF and tuning act on; the TX slice is the engine's TX
/// receiver, whose frequency, mode and filter transmit (TX on B1 while
/// listening on A1 is split). All slices play mixed, each can be muted. Each
/// panadapter is drawn from its first slice's DDC stream and zooms as one;
/// the second slice on a pan keeps its own dial, mode, filter, the entire
/// noise block and AGC. DUP keeps the receivers audible while keyed.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The most slices the app offers, whatever the radio allows.</summary>
    public const int MaxSlices = 4;

    /// <summary>Slice i's label: A1, A2 on RX1's panadapter, B1, B2 on RX2's.</summary>
    public static string SliceLabel(int i) => i switch { 0 => "A1", 1 => "A2", 2 => "B1", 3 => "B2", _ => $"RX{i + 1}" };

    /// <summary>The colour a slice's chip, flag and volume row wear.</summary>
    public static string SliceColor(int i) => SliceColors[Math.Clamp(i, 0, SliceColors.Length - 1)];
    private static readonly string[] SliceColors = ["#F25F5C", "#F5A524", "#C084FC", "#38BDF8"];

    /// <summary>Which panadapter a slice sits on: 0 = RX1's, 1 = RX2's.</summary>
    public static int SlicePan(int i) => Math.Clamp(i, 0, MaxSlices - 1) / 2;

    /// <summary>The receiver whose DDC stream and zoom a panadapter shows.</summary>
    public static int PanPrimary(int pan) => Math.Clamp(pan, 0, 1) * 2;

    /// <summary>The pan's second slice, if it is on.</summary>
    public static int PanSecondary(int pan) => PanPrimary(pan) + 1;

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
    private readonly int[] _lastActive = [0, 2];

    public bool SliceOn(int i) => i >= 0 && i < MaxSlices && (_slices[i]?.Enabled ?? false) && i < MaxSlicesAllowed;

    /// <summary>The radio's receiver ceiling (protocol/board), clamped to what the app shows.</summary>
    public int MaxSlicesAllowed => Math.Min(MaxSlices, Math.Max(2, _maxReceivers));

    private int _maxReceivers = 2;

    [ObservableProperty] private bool _fullDuplex;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSliceB))] [NotifyPropertyChangedFor(nameof(ShowPanadapterB))]
    [NotifyPropertyChangedFor(nameof(ShowAbBadges))] [NotifyPropertyChangedFor(nameof(Rx2Active))]
    [NotifyPropertyChangedFor(nameof(Rx2Idle))] [NotifyPropertyChangedFor(nameof(ActiveName))]
    [NotifyPropertyChangedFor(nameof(AbOn2))]
    private int _sliceCount = 1;

    /// <summary>Any slice beyond A1.</summary>
    public bool HasSliceB => SliceCount > 1;

    /// <summary>Slice B1 is on, so RX2's panadapter shows.</summary>
    public bool ShowPanadapterB => SliceCount > 2;

    // Two panadapters: RX1's holds A1/A2, RX2's B1/B2, opening when B1 comes
    // on. The RX1 and RX2 buttons beside the frequency pick the pan's slice.
    public bool Rx1Active => ActivePan == 0;
    public bool Rx2Active => ActivePan == 1 && ShowPanadapterB;
    /// <summary>RX2's pan is on but the controls are on RX1's.</summary>
    public bool Rx2Idle => ShowPanadapterB && ActivePan == 0;
    /// <summary>The slice badges beside the frequency: the active pan's two slices.</summary>
    public bool ShowAbBadges => SliceCount > 1;

    // The A1/A2 (or B1/B2) badges: the active pan's slices. The active one is
    // blue; the transmitting one wears the red edge.
    public string AbLabel1 => SliceLabel(ActivePan * 2);
    public string AbLabel2 => SliceLabel(ActivePan * 2 + 1);
    public bool AbOn2 => SliceOn(ActivePan * 2 + 1);
    public bool AbActive1 => ActiveRx == ActivePan * 2 && TxRx != ActiveRx;
    public bool AbActive2 => ActiveRx == ActivePan * 2 + 1 && TxRx != ActiveRx;
    public bool AbTx1 => TxRx == ActivePan * 2;
    public bool AbTx2 => TxRx == ActivePan * 2 + 1;
    /// <summary>The badge row's slice for one of its two buttons.</summary>
    public int BadgeSlice(int n) => Math.Clamp(ActivePan * 2 + n, 0, MaxSlices - 1);

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
        var parts = new List<string>(4);
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
    /// it isn't (which adds the slices the engine needs below it). The pan's
    /// most recently active slice takes the controls.</summary>
    public async Task SelectRxAsync(int pan)
    {
        if (!IsConnected) return;
        int first = PanPrimary(pan);
        while (IsConnected && SliceCount <= first && SliceCount < MaxSlicesAllowed)
        {
            int before = SliceCount;
            await AddSliceAsync(VfoHz);
            if (SliceCount == before) return;   // refused: the status line says why
        }
        if (!SliceOn(first)) return;   // the radio refused (its ceiling)
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

    // The pan under the volume sliders: RX1's panadapter towards one ear,
    // RX2's towards the other. The engine keeps it only while it runs, so
    // it's sent on every connect.
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
            if (pan == 0) return "Pan: RX1 and RX2 in both ears. Slide to split them left and right.";
            string where = Math.Abs(pan) >= 1 ? "" : $" ({Math.Abs(pan) * 100:0}%)";
            return pan < 0
                ? $"Pan: RX1 left, RX2 right{where}"
                : $"Pan: RX1 right, RX2 left{where}";
        }
    }

    private void SendRxPan()
    {
        if (!IsConnected) return;
        _rxPanSender ??= new Latest<double>(pan => _client?.SetRxPanAsync(pan) ?? Task.CompletedTask);
        _rxPanSender.Send(EngineRxPan);
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
        for (int i = 0; i < SliceCount; i++)
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
            marks.Add(new SliceMark(i, hz, low, high, SliceLabel(i), i == ActiveRx, TxRx == i, _mute[i]));
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
        _maxReceivers = Math.Clamp(s.MaxReceivers, 2, MaxSlices);
        int count = 1;
        for (int i = 0; i < MaxSlices; i++)
        {
            _slices[i] = s.Receivers?.FirstOrDefault(r => r.Index == i);
            if (_slices[i] is { } r)
            {
                _mute[i] = r.Muted;
                _sliceAfRows[i]?.SetMuted(r.Muted);
                if (r.Enabled && i < MaxSlicesAllowed) count = i + 1;
            }
        }
        SliceCount = Math.Clamp(count, 1, MaxSlicesAllowed);
        if (ActiveRx >= SliceCount) _ = MakeActiveAsync(0);
        OnPropertyChanged(RxReadouts);
        FullDuplex = s.FullDuplexMultiRxEnabled;
        // The control bank shows the active slice's AGC and noise block;
        // the other slices' live on their panadapter chips.
        var view = RxSettingsOf(s, ActiveRx);
        UpdateAgc(view);
        UpdateNoise(view);
        UpdateOperating(s);
        for (int i = 0; i < SliceCount; i++)
            if (_slices[i] is { } r) EnsureFilterLabels(r.Mode);
        _ = FollowRxFilterAsync(s);
        // Follow the engine's slice AF unless the operator just moved a slider.
        if (DateTime.UtcNow - _sliceAfMovedAt > TimeSpan.FromSeconds(2))
            for (int i = 0; i < SliceCount; i++)
                if (_slices[i] is { } r) SetSliceAfLocal(i, r.AfGainDb);
        SyncSliceAfRows();
        // Each panadapter zooms as one: the second slice on a pan is kept at
        // the pan's zoom, and RX2's pan follows its first slice's engine zoom.
        if (SliceOn(PanSecondary(0))) SyncSecondaryZoom(0);
        if (ShowPanadapterB) FollowEngineZoomB(_slices[PanPrimary(1)]?.ZoomLevel ?? 1);
        if (SliceOn(PanSecondary(1))) SyncSecondaryZoom(1);
        TxRx = SliceCount > 1 ? Math.Clamp(s.TxReceiverIndex, 0, SliceCount - 1) : 0;
        // A TX slice that isn't the active one is only seen here: follow its
        // band and mode. The active slice's own changes come through VfoHz and
        // Mode, and while it's being switched VfoHz can still be the other's.
        if (TxRx != ActiveRx) FollowTxSliceForPs();
        SyncSliceInputs();
        CheckAttSource();
    }

    partial void OnTxRxChanged(int value) => FollowTxSliceForPs();

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
        if (!IsConnected || rx >= SliceCount || rx < 0) return;
        // Until the engine's state arrives, mode and filter are still the other
        // slice's: wheel tuning on a panadapter waits for this to finish.
        _switchingRx++;
        try
        {
            bool changing = rx != ActiveRx;
            ActiveRx = rx;
            _lastActive[SlicePan(rx)] = rx;
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

    /// <summary>The label of the slice Add Slice would turn on, or "" at the ceiling.</summary>
    public string NextSliceLabel => SliceCount < MaxSlicesAllowed ? SliceLabel(SliceCount) : "";
    public bool CanAddSlice => IsConnected && SliceCount < MaxSlicesAllowed;

    /// <summary>Add the next slice at a frequency, in the active slice's mode
    /// and filter, and make it active. Slices come on in order (A2, then B1
    /// which opens RX2's panadapter, then B2) because the engine enables
    /// receivers contiguously. The radio's receiver ceiling wins over the ask:
    /// the status line says so.</summary>
    public async Task AddSliceAsync(long hz)
    {
        if (!IsConnected) return;
        int i = SliceCount;
        if (i >= MaxSlicesAllowed)
        {
            Status = SliceCount >= MaxSlices
                ? "Four slices is as many as this app offers."
                : $"This radio offers {_maxReceivers} receivers, and all are on.";
            return;
        }
        // The pan's first slice sets its zoom; a second slice starts there too.
        int zoom = EngineZoom(SlicePan(i));
        byte adc = WantsJack(i) ? (byte)1 : (byte)0;
        await Try(async () =>
        {
            await _client!.SetReceiverAsync(i, new ReceiverSetRequest(
                Enabled: true, VfoHz: hz, Mode: Mode, FilterLowHz: FilterLowHz, FilterHighHz: FilterHighHz,
                AfGainDb: 0, FilterPresetName: Filters.FirstOrDefault(f => f.IsSelected)?.SlotName,
                ZoomLevel: zoom, Agc: new AgcConfig(_agcMode), AutoAgcEnabled: AgcAuto, AdcSource: adc));
            _slices[i] = (_slices[i] ?? new ReceiverDto(i, true, adc, hz, Mode, FilterLowHz, FilterHighHz,
                Filters.FirstOrDefault(f => f.IsSelected)?.SlotName, 0, _sampleRateHz)) with { Enabled = true, VfoHz = hz };
            // The engine has it on now: count it here rather than waiting for
            // the poll, or MakeActiveAsync would refuse a slice it doesn't know.
            SliceCount = i + 1;
            SyncSliceAfRows();
            SetSliceAfLocal(i, 0);
            await MakeActiveAsync(i);
            Status = $"{SliceLabel(i)} on, {hz / 1e6:0.000000} MHz.";
        });
    }

    /// <summary>Remove a slice. Slices go off in reverse order, so only the
    /// top one comes off; turning RX2 off takes its pan's slices with it.</summary>
    public async Task RemoveSliceAsync(int rx)
    {
        if (!IsConnected || rx <= 0 || rx >= SliceCount) return;
        if (rx != SliceCount - 1)
        {
            Status = $"{SliceLabel(SliceCount - 1)} came on after {SliceLabel(rx)}: it comes off first.";
            return;
        }
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
            SliceCount = rx;
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
            int before = SliceCount;
            await RemoveSliceAsync(SliceCount - 1);
            if (SliceCount == before) return;   // refused (keyed): leave the rest on
        }
    }

    /// <summary>The ✕ on a panadapter chip: the slice itself when it's the top
    /// one, otherwise the pan's whole stack (only the top slice comes off
    /// directly).</summary>
    public Task RemoveSliceOrPanAsync(int rx) =>
        rx == SliceCount - 1 ? RemoveSliceAsync(rx) : RemovePanBAsync();

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
        long reach = (long)(_sampleRateHz * 0.43);
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
