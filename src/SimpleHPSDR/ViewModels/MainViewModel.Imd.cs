using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Engine;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>IMD meter, in the PS panel. While 2-TONE transmits, the main
/// panadapter's spectrum (averaged, as the engine sends it, not stretched) goes
/// to the engine's IMD calculator twice a second. Starting 2-TONE (or PS CAL)
/// zooms in so the tones and products are far enough apart, with the dial
/// centred — which takes CTUN off for the run, since a frozen NCO can't
/// centre; stopping puts the zoom, dial position and CTUN back. The last
/// IMD3 with PureSignal off and with it correcting are kept for comparison.</summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] private string _imdText = "";
    [ObservableProperty] private string _imdCompareText = "";
    /// <summary>What the reading was taken on: the PS sample or the receiver view.</summary>
    [ObservableProperty] private string _imdSourceText = "";
    public bool ImdShown => TwoToneOn || ImdText.Length > 0;

    private double[]? _imdAvg;
    private long _imdCenterHz;
    private float _imdHzPerPixel;
    private DateTime _imdLastSent;
    private bool _imdBusy;
    // A PureSignal test: 2-TONE started from the button with PS armed resets PS
    // first. Uncorrected = readings 0.6-3 s in; corrected = once settled (last
    // 3 s within 1.5 dB, at least 8 s in). On the 7000DLE with an amplifier it
    // settled after about 13 s: IMD3 -38 to -55 dBc (2026-11-09).
    private DateTime? _imdRunStart;
    private readonly List<(double T, double Imd3)> _imdRun = [];
    private bool _imdSettled;   // once settled, it stays settled for the run
    private static readonly TimeSpan BaselineFrom = TimeSpan.FromSeconds(0.6), BaselineTo = TimeSpan.FromSeconds(3);
    private const double SettleMinSeconds = 8, SettleSpanDb = 1.5;
    private const int SettleReadings = 6;     // 3 s at two a second
    private double _twoToneF1 = 700, _twoToneF2 = 1900;
    private const double MinToneSpacingPixels = 16;
    private double? _zoomBeforeImd;
    private bool? _ctunBeforeImd;

    partial void OnImdTextChanged(string value) => OnPropertyChanged(nameof(ImdShown));

    private void FollowTwoToneTones(StateDto s)
    {
        if (s.TwoToneFreq1 > 0) _twoToneF1 = s.TwoToneFreq1;
        if (s.TwoToneFreq2 > 0) _twoToneF2 = s.TwoToneFreq2;
    }

    /// <summary>Every frame of the TX slice's stream (ImdStream): average it while a two-tone is on
    /// the air, and send the average for measuring every half second.</summary>
    private void FeedImd(DisplayFrame f)
    {
        if (f.RxId != ImdStream || !TwoToneOn || !IsTransmitting || !f.BodyFlags.HasFlag(DisplayBodyFlags.PanValid)) return;
        // PS CAL's 3-5 s bursts run at whatever span is on screen while PS is
        // still adapting: no meaningful IMD there (it once read IMD3 -3 dBc at
        // the full 384 kHz span).
        if (PsCalibrating) return;
        // The tones and their products must be clearly apart on screen, or the
        // tones' own spread is read as distortion.
        if (f.HzPerPixel > 0 && Math.Abs(_twoToneF2 - _twoToneF1) / f.HzPerPixel < MinToneSpacingPixels)
        {
            ImdText = "IMD: zoom in to measure (the 2-TONE button does it)";
            ImdCompareText = ImdSourceText = "";
            return;
        }
        var pan = f.PanDb.Span;
        if (_imdAvg is null || _imdAvg.Length != pan.Length || f.CenterHz != _imdCenterHz || f.HzPerPixel != _imdHzPerPixel)
        {
            _imdAvg = new double[pan.Length];
            for (int i = 0; i < pan.Length; i++) _imdAvg[i] = pan[i];
            _imdCenterHz = f.CenterHz;
            _imdHzPerPixel = f.HzPerPixel;
            _imdLastSent = DateTime.UtcNow;      // let the average settle first
            return;
        }
        for (int i = 0; i < pan.Length; i++) _imdAvg[i] += (pan[i] - _imdAvg[i]) * 0.3;
        if (_imdBusy || DateTime.UtcNow - _imdLastSent < TimeSpan.FromMilliseconds(500) || _client is null) return;
        _imdLastSent = DateTime.UtcNow;
        _imdBusy = true;
        var rxView = _imdAvg.Select(d => (double?)d).ToArray();
        double hzPerPixel = _imdHzPerPixel, spacing = Math.Abs(_twoToneF2 - _twoToneF1);
        long rxCenter = _imdCenterHz, dial = TxSlice().Hz;   // the TX slice's, not the active one's
        bool psArmed = PsOn, psCorrecting = PsLocked;
        var client = _client;
        _ = Task.Run(async () =>
        {
            ImdReading? r = null;
            bool fromSample = false;
            try
            {
                // With PureSignal armed, measure the coupler sample: the amplifier's
                // real output, in the panadapter's span, centred on the transmit
                // frequency. Otherwise the panadapter's own (receiver) view.
                if (psArmed && await client.GetPsFeedbackSpectrumAsync() is { Length: > 16 } fb)
                {
                    _imdSampleAvg = Smooth(_imdSampleAvg, fb);
                    r = await client.MeasureImdAsync(new ImdRequest(
                        [.. _imdSampleAvg.Select(d => (double?)d)], _imdSampleAvg.Length, dial, hzPerPixel, spacing));
                    fromSample = true;
                }
                else
                {
                    _imdSampleAvg = null;
                    r = await client.MeasureImdAsync(new ImdRequest(rxView, rxView.Length, rxCenter, hzPerPixel, spacing));
                }
            }
            catch (Exception) { }
            Dispatcher.UIThread.Post(() =>
            {
                _imdBusy = false;
                if (r is not null && TwoToneOn) ShowImd(r, fromSample, psCorrecting);
            });
        });
    }

    private double[]? _imdSampleAvg;

    // The feedback FFT is fresh twice a second here, so a light average steadies it.
    private static double[] Smooth(double[]? avg, float[] next)
    {
        if (avg is null || avg.Length != next.Length) return [.. next.Select(v => (double)v)];
        for (int i = 0; i < next.Length; i++) avg[i] += (next[i] - avg[i]) * 0.5;
        return avg;
    }

    private void ShowImd(ImdReading r, bool fromSample, bool psCorrecting)
    {
        string where = fromSample ? "amplifier output (PS sample)" : "receiver view";
        if (!r.Ok || r.Imd3 is not { } i3)
        {
            ImdText = "IMD: " + (r.Reason ?? "no reading");
            ImdSourceText = where;
            return;
        }
        ImdText = r.Imd5 is { } i5 ? $"IMD3 −{i3.Dbc:0} dBc · IMD5 −{i5.Dbc:0} dBc" : $"IMD3 −{i3.Dbc:0} dBc";
        ImdSourceText = where;
        if (!fromSample)
        {
            ImdCompareText = PsOn ? "" : "Turn PureSignal on and run 2-TONE to see how much it improves this.";
            return;
        }
        if (_imdRunStart is not { } start) return;   // PS CAL bursts: live reading only
        double t = (DateTime.UtcNow - start).TotalSeconds;
        _imdRun.Add((t, i3.Dbc));
        ImdCompareText = ImdRunSummary(t);
    }

    private static double Median(IEnumerable<double> v)
    {
        var a = v.OrderBy(x => x).ToArray();
        return a.Length == 0 ? double.NaN : a[a.Length / 2];
    }

    private string ImdRunSummary(double t)
    {
        var baseline = _imdRun.Where(x => x.T >= BaselineFrom.TotalSeconds && x.T <= BaselineTo.TotalSeconds).Select(x => x.Imd3).ToList();
        if (baseline.Count == 0) return "PureSignal reset: measuring uncorrected…";
        double off = Median(baseline);
        var last = _imdRun.TakeLast(SettleReadings).Select(x => x.Imd3).ToList();
        _imdSettled |= t >= SettleMinSeconds && last.Count == SettleReadings && last.Max() - last.Min() <= SettleSpanDb;
        if (!_imdSettled)
            return $"Uncorrected IMD3 −{off:0} dBc. PureSignal is settling: keep 2-TONE on (about 15 s).";
        double on = Median(last);
        return $"IMD3 uncorrected −{off:0}, corrected −{on:0} dBc: PureSignal " +
               (on >= off ? $"improves it by {on - off:0} dB." : $"makes it {off - on:0} dB worse.");
    }

    /// <summary>Operator starts 2-TONE or PS CAL: zoom in so the tones are well
    /// apart and the 5th-order products are on screen, with the dial centred.
    /// Centring needs CTUN off — with it on the NCO stays frozen and the zoomed
    /// display sits off to one side — so CTUN goes for the run and comes back
    /// in RestoreAfterImd.</summary>
    private async Task ZoomForImdAsync()
    {
        if (_sampleRateHz <= 0 || _client is null) return;
        double spacing = Math.Max(100, Math.Abs(_twoToneF2 - _twoToneF1));
        double outer = Math.Max(Math.Abs(_twoToneF1), Math.Abs(_twoToneF2)) + 2 * spacing;
        double span = Math.Max(2.4 * outer, 10_000);
        double zoom = Math.Clamp(_sampleRateHz / span, 1, MaxZoom);
        // The panadapter the transmitting slice is on: RX2's for B1/B2.
        int pan = _imdPan ?? (SlicePan(TxRx) == 1 && ShowPanadapterB ? 1 : 0);
        // A TX slice on its pan's first slice's stream is only seen in that
        // stream: out of its reach there is nothing to zoom to or measure.
        if (SharesStream(TxRx) && Math.Abs(TxSlice().Hz - RxHz(PanPrimary(pan))) > SpanReach)
        {
            Status = $"{SliceLabel(TxRx)} is outside RX{pan + 1}'s span: no IMD reading there.";
            return;
        }
        _imdPan = pan;
        _zoomBeforeImd ??= _zoomFine[pan];
        _ctunBeforeImd ??= CtunOn;
        if (CtunOn) CtunOn = false;
        // A TX slice that isn't its pan's first (A2, B2): the pan's picture is
        // the first slice's capture, so centre that on the TX frequency, if it
        // reaches. Otherwise the meter reads the TX slice's own receiver.
        int primary = PanPrimary(pan);
        if (TxRx != primary)
        {
            long txHz = TxSlice().Hz;
            if (Math.Abs(txHz - RxHz(primary)) <= SpanReach) _imdCentreHz = txHz;
            else Status = $"{SliceLabel(TxRx)} is outside {SliceLabel(primary)}'s panadapter: IMD reads {SliceLabel(TxRx)}'s own receiver.";
        }
        await Try(() => CenterOnDialAsync(pan));
        // Stopped while that was in flight: RestoreAfterImd has already put
        // things back, so don't zoom in over it.
        if (_zoomBeforeImd is null) return;
        SetZoomFine(pan, zoom, persist: false);
    }

    /// <summary>The pan zoomed and centred for 2-TONE / PS CAL, while it runs.</summary>
    private int? _imdPan;
    /// <summary>A second-slice TX frequency that pan's picture is centred on
    /// for the run (CenterOnDialAsync honours it).</summary>
    private long? _imdCentreHz;

    /// <summary>The receiver stream the IMD meter reads: the TX pan's picture
    /// when the tones are centred in it, else the TX slice's own receiver.
    /// None (-1) for a slice on another's stream that isn't centred: its
    /// frames are that stream's picture, not centred on the tones.</summary>
    private int ImdStream => _imdPan is { } p && (TxRx == PanPrimary(p) || _imdCentreHz is not null)
        ? PanPrimary(p) : SharesStream(TxRx) ? -1 : TxRx;

    /// <summary>The slice a pan's view places its window around (the views
    /// read it every frame): the TX slice during 2-TONE / PS CAL on that pan,
    /// otherwise the pan's first slice (-1 = the active one, single pan).</summary>
    public int PanDialSlice(int pan) => _imdPan == pan ? TxRx
        : pan == 0 ? (ShowPanadapterB ? PanPrimary(0) : -1) : PanPrimary(1);

    /// <summary>2-TONE / PS CAL stopped, however: zoom, centre, dial position
    /// and CTUN as they were.</summary>
    private void RestoreAfterImd()
    {
        _imdAvg = null;
        _imdRunStart = null;              // the summary stays on screen
        int pan = _imdPan ?? 0;
        bool recentre = _imdCentreHz is not null;
        // Cleared first, so the zoom's own re-centring goes back to normal.
        _imdPan = null;
        _imdCentreHz = null;
        if (_zoomBeforeImd is { } z)
        {
            _zoomBeforeImd = null;
            SetZoomFine(pan, z, persist: false);
        }
        if (_ctunBeforeImd is { } ctun)
        {
            _ctunBeforeImd = null;
            CtunOn = ctun;
        }
        if (recentre && IsConnected) _ = Try(() => CenterOnDialAsync(pan));
        if (Math.Abs(EffectiveDialPositionOf(0)) >= 0.5) SendDialPosition();
    }
}
