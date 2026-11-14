using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// The ANAN G2 front panel through Saturn's p2app (see G2PanelLink): the
/// original G2 panel (GPIO v1 or v2) and the G2V2 / G2 Ultra panel. Runs only
/// while connected to a G2 / G2-1K (the engine says whether it supports the
/// panel's CAT port), so every other radio is untouched. Works with
/// SimpleHPSDR on the G2's own Pi beside p2app, or on another computer.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly G2PanelLink _panel = new();
    private bool _panelWired;

    /// <summary>4 = original G2 panel, 5 = G2V2 / Ultra, 0 = none seen yet.</summary>
    [ObservableProperty] private int _g2PanelProduct;
    [ObservableProperty] private string _g2PanelStatus = "";

    private void WirePanel()
    {
        if (_panelWired) return;
        _panelWired = true;
        _panel.PanelIdentified += p => Dispatcher.UIThread.Post(() =>
        {
            G2PanelProduct = p;
            G2PanelStatus = $"Connected: {G2Panels.Name(p)}.";
        });
        _panel.ConnectedChanged += on => Dispatcher.UIThread.Post(() =>
            G2PanelStatus = on ? (G2PanelProduct > 0 ? $"Connected: {G2Panels.Name(G2PanelProduct)}." : "p2app connected.")
                : _panel.IsListening ? "Waiting for p2app to connect." : "Off.");
        _panel.VfoSteps += n => Dispatcher.UIThread.Post(() => PanelVfo(n));
        _panel.Encoder += (e, n) => Dispatcher.UIThread.Post(() => PanelKnob(e, n));
        _panel.Button += (b, ev) => Dispatcher.UIThread.Post(() => PanelButton(b, ev));
        _panel.Answer = PanelAnswer;
    }

    /// <summary>After connecting: listen first, then announce the port.</summary>
    private async Task StartG2PanelAsync()
    {
        if (!_settings.G2PanelOn || _client is not { } client || _radio is null) return;
        try
        {
            var cat = await client.GetG2CatPortAsync();
            if (!cat.Supported) { G2PanelStatus = ""; return; }
            WirePanel();
            IPAddress.TryParse(_radio.Ip, out var radioIp);
            _panel.Start(_settings.G2PanelPort, radioIp);
            await client.SetG2CatPortAsync(_panel.Port);
            G2PanelStatus = "Waiting for p2app to connect.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException
                                       or System.Net.Sockets.SocketException)
        {
            _panel.Stop();
            G2PanelStatus = $"Front panel not started: {ex.Message}";
        }
    }

    /// <summary>Before disconnecting: announce 0, give p2app a moment, then stop
    /// (p2app can crash if the listener goes first).</summary>
    private async Task StopG2PanelAsync()
    {
        if (!_panel.IsListening) return;
        try { if (_client is { } c) await c.SetG2CatPortAsync(0); } catch (Exception) { }
        await Task.Delay(100);
        _panel.Stop();
        G2PanelProduct = 0;
        G2PanelStatus = "";
    }

    /// <summary>Quitting: the same order, without touching the display.</summary>
    private void StopG2PanelForShutdown()
    {
        if (!_panel.IsListening) return;
        if (_client is { } c)
            try { Task.Run(() => c.SetG2CatPortAsync(0)).Wait(TimeSpan.FromSeconds(1)); } catch (Exception) { }
        Thread.Sleep(100);
        _panel.Stop();
    }

    /// <summary>Restart it after Config changes (on/off, port).</summary>
    public async Task RestartG2PanelAsync()
    {
        await StopG2PanelAsync();
        if (IsConnected) await StartG2PanelAsync();
    }

    // ---- what the panel asks for its LEDs ----------------------------

    private string? PanelAnswer(string cmd)
    {
        // Read on a socket thread: plain field reads, no UI work.
        switch (cmd)
        {
            case "ZZXV":
            {
                int s = 0;
                if (RitOn) s |= 0x01;
                if (VfoLocked) s |= 0x06;                       // A and B lock
                if (SliceBEnabled && TxRx != ActiveRx) s |= 0x08; // split
                if (CtunOn) s |= 0x10;
                if (MoxOn) s |= 0x40;
                if (TunOn) s |= 0x80;
                if (XitOn) s |= 0x100;
                return $"ZZXV{s:D4};";
            }
            case "ZZUT": return $"ZZUT{(TwoToneOn ? 1 : 0)};";
            case "ZZYR": return $"ZZYR{ActiveRx};";
            default: return null;
        }
    }

    // ---- actions --------------------------------------------------------

    private string MapKey(char kind, int code) => $"{kind}{G2PanelProduct}:{code}";

    internal PanelButtonAction ButtonAction(int product, int code)
    {
        if (_settings.G2PanelMap.TryGetValue($"B{product}:{code}", out var s) && Enum.TryParse<PanelButtonAction>(s, out var a)) return a;
        var d = G2Panels.Buttons(product).FirstOrDefault(c => c.Code == code);
        return d is not null && Enum.TryParse<PanelButtonAction>(d.DefaultAction, out var def) ? def : PanelButtonAction.None;
    }

    internal PanelKnobAction KnobAction(int product, int code)
    {
        if (_settings.G2PanelMap.TryGetValue($"E{product}:{code}", out var s) && Enum.TryParse<PanelKnobAction>(s, out var a)) return a;
        var d = G2Panels.Knobs(product).FirstOrDefault(c => c.Code == code);
        return d is not null && Enum.TryParse<PanelKnobAction>(d.DefaultAction, out var def) ? def : PanelKnobAction.None;
    }

    // The VFO knob: a fast turn sends more ticks per message; speed it up a little.
    private void PanelVfo(int ticks)
    {
        if (!IsConnected || VfoLocked) return;
        int n = Math.Abs(ticks);
        int steps = n <= 3 ? n : n + (n - 3) * (n - 3) / 2;
        TuneBySteps(Math.Sign(ticks) * steps);
    }

    private void PanelKnob(int code, int steps)
    {
        if (!IsConnected) return;
        switch (KnobAction(G2PanelProduct, code))
        {
            case PanelKnobAction.AfGain: AfGainDb = Math.Clamp(AfGainDb + steps, -50, 20); break;
            case PanelKnobAction.AfA: SliceAfA = Math.Clamp(SliceAfA + steps, -40, 20); break;
            case PanelKnobAction.AfB: if (SliceBEnabled) SliceAfB = Math.Clamp(SliceAfB + steps, -40, 20); break;
            case PanelKnobAction.AgcTop: AgcTopDb = Math.Clamp(AgcTopDb + steps, 0, 120); break;
            case PanelKnobAction.Drive: DrivePct = Math.Clamp(DrivePct + steps, 0, DriveCeilingPct); break;
            case PanelKnobAction.MicGain: MicGainDb = Math.Clamp(MicGainDb + steps, -20, 40); break;
            case PanelKnobAction.Rit: NudgeRit(steps); break;
            case PanelKnobAction.Xit: NudgeXit(steps); break;
            case PanelKnobAction.FilterHigh: DragFilter(FilterLowHz, FilterHighHz + steps * 50); break;
            case PanelKnobAction.FilterLow: DragFilter(FilterLowHz + steps * 50, FilterHighHz); break;
            case PanelKnobAction.Attenuation: if (AttOnRx2Jack || !PreampOn) AttenDb = Math.Clamp(AttenDb + steps, 0, 31); break;
            case PanelKnobAction.TuneB: if (SliceBEnabled) _ = TuneSliceByStepsAsync(1, steps); break;
        }
    }

    /// <summary>A panel button asked to close SimpleHPSDR (the main window does it).</summary>
    public event Action? QuitRequested;

    private void PanelButton(int code, int ev)
    {
        var action = ButtonAction(G2PanelProduct, code);
        // Quit only on a long press, so a stray tap can't close the radio.
        if (action == PanelButtonAction.Quit)
        {
            if (ev == 2) QuitRequested?.Invoke();
            return;
        }
        if (!IsConnected || ev != 1) return;   // act on the press
        switch (action)
        {
            case PanelButtonAction.Mox: MoxOn = !MoxOn; break;
            case PanelButtonAction.Tune: TunOn = !TunOn; break;
            case PanelButtonAction.TwoTone: TwoToneOn = !TwoToneOn; break;
            case PanelButtonAction.Ctun: ToggleCtunCommand.Execute(null); break;
            case PanelButtonAction.Lock: ToggleVfoLockCommand.Execute(null); break;
            case PanelButtonAction.Mute: Muted = !Muted; break;
            case PanelButtonAction.MuteA: _ = MuteSliceAsync(0); break;
            case PanelButtonAction.MuteB: _ = MuteSliceAsync(1); break;
            case PanelButtonAction.BandUp: Step(Bands, +1); break;
            case PanelButtonAction.BandDown: Step(Bands, -1); break;
            case PanelButtonAction.ModeUp: Step(Modes, +1); break;
            case PanelButtonAction.ModeDown: Step(Modes, -1); break;
            case PanelButtonAction.FilterWider: StepFilter(-1); break;
            case PanelButtonAction.FilterNarrower: StepFilter(+1); break;
            case PanelButtonAction.FilterDefault:
                if (Filters.FirstOrDefault(f => f.SlotName == DefaultFilterSlot) is { } slot) _ = SelectFilterAsync(slot);
                break;
            case PanelButtonAction.AToB: _ = CopySliceAsync(0, 1); break;
            case PanelButtonAction.BToA: _ = CopySliceAsync(1, 0); break;
            case PanelButtonAction.SwapAB: if (SliceBEnabled) _ = MakeActiveAsync(ActiveRx == 0 ? 1 : 0); break;
            case PanelButtonAction.Split: _ = SplitAsync(); break;
            case PanelButtonAction.Rit: ToggleRitCommand.Execute(null); break;
            case PanelButtonAction.Xit: ToggleXitCommand.Execute(null); break;
            case PanelButtonAction.RitXitClear: ClearRit(); ClearXit(); break;
            case PanelButtonAction.Nb: Toggle(NoiseBlankers); break;
            case PanelButtonAction.Nr: Toggle(NoiseReductions); break;
            case PanelButtonAction.Anf: ToggleAnfCommand.Execute(null); break;
            case >= PanelButtonAction.Band160 and <= PanelButtonAction.Band6:
            {
                string name = action.ToString()["Band".Length..];
                Bands.FirstOrDefault(b => b.Value is Band band && band.Name == name)?.Select.Execute(null);
                break;
            }
        }
    }

    private static void Step(IList<Choice> list, int by)
    {
        if (list.Count == 0) return;
        int i = list.ToList().FindIndex(c => c.IsSelected);
        int next = i < 0 ? 0 : Math.Clamp(i + by, 0, list.Count - 1);
        if (next != i) list[next].Select.Execute(null);
    }

    // Off, or the first real choice when it's off.
    private static void Toggle(IList<Choice> list)
    {
        if (list.Count < 2) return;
        (list[0].IsSelected ? list[1] : list[0]).Select.Execute(null);
    }

    private void StepFilter(int by)
    {
        var list = Filters.ToList();
        int i = list.FindIndex(f => f.IsSelected);
        if (i < 0) i = list.FindIndex(f => f.SlotName == DefaultFilterSlot);
        int next = Math.Clamp(i + by, 0, list.Count - 1);
        if (next >= 0 && next < list.Count) _ = SelectFilterAsync(list[next]);
    }

    private async Task MuteSliceAsync(int rx)
    {
        if (!SliceBEnabled) { Muted = !Muted; return; }
        await SetSliceMuteAsync(rx, !IsSliceMuted(rx));
    }

    private long SliceHz(int rx) => SliceMarks().FirstOrDefault(m => m.Rx == rx)?.Hz ?? VfoHz;

    /// <summary>A > B / B > A: copy the frequency, staying on the slice in use.</summary>
    private async Task CopySliceAsync(int from, int to)
    {
        long hz = SliceHz(from);
        int was = ActiveRx;
        if (!SliceBEnabled)
        {
            if (to == 1) { await AddSliceBAsync(hz, ownPanadapter: false); await MakeActiveAsync(was); }
            return;
        }
        await TuneSliceAsync(to, hz);
        if (ActiveRx != was) await MakeActiveAsync(was);
    }

    /// <summary>Split: transmit on the other slice (adding B on the same frequency if needed).</summary>
    private async Task SplitAsync()
    {
        if (!SliceBEnabled)
        {
            int was = ActiveRx;
            await AddSliceBAsync(VfoHz, ownPanadapter: false);
            await MakeActiveAsync(was);
            await TransmitOnAsync(1);
            return;
        }
        await TransmitOnAsync(TxRx == 0 ? 1 : 0);
    }
}
