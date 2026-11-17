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
            _dial.Button += (code, ev) => Dispatcher.UIThread.Post(() => PanelButton(G2Panels.Dial, code, ev));
            _dial.Turned += (held, dir) => Dispatcher.UIThread.Post(() =>
                PanelKnob(G2Panels.Dial, held ? 4 : _dialJob + 1, dir));
        }
        _dial.Start();
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
