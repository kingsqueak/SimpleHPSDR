using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// A Ulanzi Dial as a tuning knob and buttons (see DialLink). It is mapped
/// like a G2 front panel (G2Panels, product Dial), so it has the same actions
/// and the same page in Config. Its one knob has three jobs, chosen with the
/// top buttons, and a fourth while it is held down.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly DialLink _dial = new();
    private bool _dialWired;
    private int _dialJob;   // 0 to 2

    [ObservableProperty] private string _dialStatus = "";

    private void StartDial()
    {
        if (!_settings.DialOn) return;
        if (!_dialWired)
        {
            _dialWired = true;
            _dial.StatusChanged += s => Dispatcher.UIThread.Post(() => DialStatus = s);
            _dial.Button += (code, ev) => Dispatcher.UIThread.Post(() =>
            {
                StopDialGlide();   // any button stops a glide
                PanelButton(G2Panels.Dial, code, ev);
            });
            _dial.Turned += (held, dir) =>
            {
                long at = Environment.TickCount64;   // when it arrived, not when the UI got to it
                Dispatcher.UIThread.Post(() => DialTurned(held, dir, at));
            };
        }
        _dial.Start();
    }

    private readonly Queue<long> _dialTurns = new();   // when the last detents came, ms
    private int _dialTurnDir;
    private double _dialRate;        // detents a second, smoothed

    /// <summary>One detent. Tuning speeds up with the dial: a slow turn is one
    /// step a detent, a spin many, and a spin let go glides on like a flick of
    /// the panadapter. The other jobs stay one for one.</summary>
    private void DialTurned(bool held, int dir, long at)
    {
        int code = held ? 4 : _dialJob + 1;
        var action = KnobAction(G2Panels.Dial, code);
        if (action is not (PanelKnobAction.Tune or PanelKnobAction.TuneFast or PanelKnobAction.TuneB))
        {
            StopDialGlide();
            PanelKnob(G2Panels.Dial, code, dir);
            return;
        }
        int times = DialSpeedUp(dir, at);
        PanelKnob(G2Panels.Dial, code, dir * times);
        // Ready to glide from here if this turns out to be the last detent.
        _glideCode = code;
        _glideV = times >= GlideFromTimes ? dir * _dialRate * times : 0;
        _glideLast = at;
        _glideOwed = 0;
        _gliding = false;
        _glideTimer ??= MakeGlideTimer();
        _glideTimer.Start();
    }

    // Bluetooth delivers detents in bunches (two 16 ms apart, then a 70 ms
    // gap, at an even turn), so the speed is counted over a window and
    // smoothed, not taken from the gap between two.
    private const int DialWindowMs = 300;

    private int DialSpeedUp(int dir, long at)
    {
        // A change of direction, or a pause, starts again from slow.
        if (dir != _dialTurnDir || (_dialTurns.Count > 0 && at - _dialTurns.Last() > DialWindowMs))
        {
            _dialTurns.Clear();
            _dialRate = 0;
        }
        _dialTurnDir = dir;
        _dialTurns.Enqueue(at);
        while (_dialTurns.Count > 0 && at - _dialTurns.Peek() > DialWindowMs) _dialTurns.Dequeue();
        double now = _dialTurns.Count * 1000.0 / DialWindowMs;
        _dialRate = _dialRate <= 0 ? now : 0.5 * _dialRate + 0.5 * now;
        return DialMultiplier(_dialRate);
    }

    /// <summary>1 up to about 17 detents a second (a careful turn), then
    /// steeply: 2 at 20, 4 at 29, 8 at 43, 16 at 63, 20 at most.</summary>
    internal static int DialMultiplier(double detentsPerSecond) =>
        (int)Math.Clamp(Math.Round(Math.Pow(detentsPerSecond / 13.5, 1.8)), 1, 20);

    // ---- gliding on after a spin ----
    private const int GlideFromTimes = 2;      // only a turn fast enough to be multiplied glides
    private const int GlideAfterMs = 90;       // no detent for this long: the dial was let go
    private const double GlideFriction = 1.67; // slowing, 1/s: glides v / 1.67 steps in all (half as far again as at 2.5)
    private const double GlideStopSpeed = 4;   // steps a second where it stops
    private DispatcherTimer? _glideTimer;
    private double _glideV, _glideOwed;        // steps a second (signed), and the fraction not yet tuned
    private long _glideLast;
    private int _glideCode;
    private bool _gliding;

    private DispatcherTimer MakeGlideTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) => GlideStep();
        return timer;
    }

    private void StopDialGlide()
    {
        _glideV = 0;
        _gliding = false;
        _glideTimer?.Stop();
    }

    private void GlideStep()
    {
        long now = Environment.TickCount64;
        if (!_gliding)
        {
            if (now - _glideLast < GlideAfterMs) return;   // still being turned
            _gliding = true;
            _glideLast = now - 30;
        }
        if (Math.Abs(_glideV) < GlideStopSpeed || !IsConnected || VfoLocked || IsTransmitting)
        {
            StopDialGlide();
            return;
        }
        double dt = Math.Min((now - _glideLast) / 1000.0, 0.1);
        _glideLast = now;
        _glideOwed += _glideV * dt;
        _glideV *= Math.Exp(-GlideFriction * dt);
        int steps = (int)Math.Truncate(_glideOwed);
        if (steps == 0) return;
        _glideOwed -= steps;
        PanelKnob(G2Panels.Dial, _glideCode, steps);
    }

    /// <summary>After Config turns it on or off.</summary>
    public void RestartDial()
    {
        _dial.Stop();
        DialStatus = "";
        StartDial();
    }

    private void SetDialJob(int job)
    {
        _dialJob = Math.Clamp(job, 0, 2);
        Status = $"Dial: {G2Panels.Describe(KnobAction(G2Panels.Dial, _dialJob + 1)).ToLowerInvariant()}";
    }
}
