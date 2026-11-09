using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Engine;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>IMD meter, in the PS panel. While 2-TONE transmits, the main
/// panadapter's spectrum (averaged, as the engine sends it, not stretched) goes
/// to the engine's IMD calculator twice a second. Starting 2-TONE from the
/// button zooms in so the tones and products are far enough apart, with the
/// dial centred; stopping it puts the zoom and dial position back. The last
/// IMD3 with PureSignal off and with it correcting are kept for comparison.</summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] private string _imdText = "";
    [ObservableProperty] private string _imdCompareText = "";
    public bool ImdShown => TwoToneOn || ImdText.Length > 0;

    private double[]? _imdAvg;
    private long _imdCenterHz;
    private float _imdHzPerPixel;
    private DateTime _imdLastSent;
    private bool _imdBusy;
    private double? _imd3PsOff, _imd3PsOn;
    private double _twoToneF1 = 700, _twoToneF2 = 1900;
    private double? _zoomBeforeImd;

    partial void OnImdTextChanged(string value) => OnPropertyChanged(nameof(ImdShown));

    private void FollowTwoToneTones(StateDto s)
    {
        if (s.TwoToneFreq1 > 0) _twoToneF1 = s.TwoToneFreq1;
        if (s.TwoToneFreq2 > 0) _twoToneF2 = s.TwoToneFreq2;
    }

    /// <summary>Every main-panadapter frame: average it while a two-tone is on
    /// the air, and send the average for measuring every half second.</summary>
    private void FeedImd(DisplayFrame f)
    {
        if (f.RxId != 0 || !TwoToneOn || !IsTransmitting || !f.BodyFlags.HasFlag(DisplayBodyFlags.PanValid)) return;
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
        var req = new ImdRequest([.. _imdAvg.Select(d => (double?)d)], _imdAvg.Length, _imdCenterHz, _imdHzPerPixel,
            Math.Abs(_twoToneF2 - _twoToneF1));
        bool psCorrecting = PsLocked, psOff = !PsOn;
        _ = Task.Run(async () =>
        {
            ImdReading? r = null;
            try { r = await _client.MeasureImdAsync(req); }
            catch (Exception) { }
            Dispatcher.UIThread.Post(() =>
            {
                _imdBusy = false;
                if (r is not null && TwoToneOn) ShowImd(r, psCorrecting, psOff);
            });
        });
    }

    private void ShowImd(ImdReading r, bool psCorrecting, bool psOff)
    {
        if (!r.Ok || r.Imd3 is not { } i3)
        {
            ImdText = "IMD: " + (r.Reason ?? "no reading");
            return;
        }
        ImdText = r.Imd5 is { } i5 ? $"IMD3 −{i3.Dbc:0} dBc · IMD5 −{i5.Dbc:0} dBc" : $"IMD3 −{i3.Dbc:0} dBc";
        if (psCorrecting) _imd3PsOn = i3.Dbc;
        else if (psOff) _imd3PsOff = i3.Dbc;
        ImdCompareText = (_imd3PsOff, _imd3PsOn) switch
        {
            ({ } off, { } on) => $"IMD3 with PS off −{off:0}, on −{on:0} dBc: PureSignal " +
                                 (on >= off ? $"improves it by {on - off:0} dB" : $"makes it {off - on:0} dB worse"),
            ({ } off, null) => $"PS off: IMD3 −{off:0} dBc. Run it again with PS on to compare.",
            (null, { } on) => $"PS on: IMD3 −{on:0} dBc. Run it again with PS off to compare.",
            _ => "",
        };
    }

    /// <summary>Operator starts 2-TONE: zoom in so the tones are well apart and
    /// the 5th-order products are on screen, with the dial in the middle.</summary>
    private async Task ZoomForImdAsync()
    {
        if (_sampleRateHz <= 0 || _client is null) return;
        double spacing = Math.Max(100, Math.Abs(_twoToneF2 - _twoToneF1));
        double outer = Math.Max(Math.Abs(_twoToneF1), Math.Abs(_twoToneF2)) + 2 * spacing;
        double span = Math.Max(2.4 * outer, 10_000);
        double zoom = Math.Clamp(_sampleRateHz / span, 1, MaxZoom);
        _zoomBeforeImd ??= _zoomFine;
        ImdText = "IMD: measuring…";
        if (Math.Abs(DialPosition) >= 0.5) await Try(() => _client.SetDialPositionAsync(0));
        SetZoomFine(zoom, persist: false);
    }

    /// <summary>2-TONE stopped, however: zoom and dial position as they were.</summary>
    private void RestoreAfterImd()
    {
        _imdAvg = null;
        if (_zoomBeforeImd is not { } z) return;
        _zoomBeforeImd = null;
        SetZoomFine(z, persist: false);
        if (Math.Abs(DialPosition) >= 0.5) SendDialPosition();
    }
}
