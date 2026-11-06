using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

public enum CwKey { Straight, Dit, Dah }

/// <summary>
/// Keyboard keying from the CW window: space is a straight key, the arrows are
/// paddles. Off until ticked, each run. Between elements transmit is held for
/// the hang time so the relays don't follow every element. The engine's only
/// hold is MOX, and a held MOX puts the mic on the air, so the hold mutes the
/// mic first and proves it took. If anything fails the mic stays muted, and
/// its real level comes back from settings at the next connect.
/// </summary>
public sealed partial class MainViewModel
{
    private const int MaxKeyDownMs = 3000;   // the engine lifts the key after this regardless
    private const string KeyerJobText = "<keyer>";

    [ObservableProperty] private bool _cwKeyboardOn;
    [ObservableProperty] private string _cwKeyboardNote = "";

    private TciKeyer? _keyer;
    private const int MicMutedDb = -96;   // the engine's floor; the slider stops well above it
    private bool _straightDown, _ditDown, _dahDown, _paddleRunning, _lastWasDit, _keyboardHold;
    private DispatcherTimer? _cwHangTimer;

    private bool AnyKeyDown => _straightDown || _ditDown || _dahDown;
    private bool TextGoingOut => CwSending && CwSendingText != KeyerJobText;
    private bool KeyboardAllowed => CwKeyboardOn && IsConnected && ModeIsCw && !TextGoingOut;

    partial void OnCwKeyboardOnChanged(bool value)
    {
        CwKeyboardNote = "";
        if (!value) ReleaseCwKeys();
    }

    /// <summary>A key went down in the CW window. False if it isn't for us.</summary>
    public bool CwKeyDown(CwKey key)
    {
        if (!KeyboardAllowed) return false;
        switch (Paddle(key))
        {
            case CwKey.Straight:
                if (_straightDown) return true;   // auto-repeat
                _straightDown = true;
                _ = StraightDownAsync();
                break;
            case CwKey.Dit:
                _ditDown = true;
                _ = RunPaddlesAsync();
                break;
            case CwKey.Dah:
                _dahDown = true;
                _ = RunPaddlesAsync();
                break;
        }
        return true;
    }

    public void CwKeyUp(CwKey key)
    {
        switch (Paddle(key))
        {
            case CwKey.Straight when _straightDown:
                _straightDown = false;
                _ = SendKeyUpAsync();
                StartCwHang(StraightHangMs);
                break;
            case CwKey.Dit: _ditDown = false; break;
            case CwKey.Dah: _dahDown = false; break;
        }
    }

    // Reverse paddles applies to the arrows as it does to a real paddle.
    private CwKey Paddle(CwKey key) => !CwPaddleReverse ? key : key switch
    {
        CwKey.Dit => CwKey.Dah,
        CwKey.Dah => CwKey.Dit,
        _ => key,
    };

    /// <summary>The window lost focus or closed, a text box took the keys, the
    /// mode changed: everything up, and no hang.</summary>
    public void ReleaseCwKeys()
    {
        bool wasKeyed = AnyKeyDown || _keyboardHold;
        _straightDown = _ditDown = _dahDown = false;
        _cwHangTimer?.Stop();
        if (!wasKeyed) return;
        _ = SendKeyUpAsync();
        if (_keyboardHold) MoxOn = false;   // OnMoxOnChanged restores the mic
    }

    // Mute before keying, and read it back: without proof MOX never goes on.
    private async Task MuteMicForHoldAsync()
    {
        _settings.CwHeldMicDb = (int)Math.Round(MicGainDb);
        _settings.Save();
        await _client!.SetMicGainAsync(MicMutedDb);
        if ((await _client.GetStateAsync()).MicGainDb != MicMutedDb)
            throw new InvalidOperationException("the mic didn't mute");
        await Task.Delay(60);   // let the gain reach the DSP
    }

    // After MOX is down, so the mic can't come back on air.
    private async Task RestoreMicAfterHoldAsync()
    {
        if (_settings.CwHeldMicDb is not int db || _client is not { } client) return;
        await Task.Delay(150);
        if (_keyboardHold) return;   // keying started again meanwhile
        try
        {
            await client.SetMicGainAsync(db);
            _settings.CwHeldMicDb = null;
            _settings.Save();
        }
        catch (Exception ex) { Status = $"Mic gain not restored, it comes back on connect: {ex.Message}"; }
    }

    private async Task SendKeyUpAsync()
    {
        try { if (_keyer is { } k) await k.UpAsync(); }
        catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    // The keyer needs the TCI server, and the hold in place before the first element.
    private async Task<bool> BeginKeyingAsync()
    {
        _cwHangTimer?.Stop();
        if (_keyboardHold) return true;
        if (MoxOn || TunOn)
        {
            CwKeyboardNote = "Keyboard keying can't start while MOX or TUNE is on.";
            return false;
        }
        if (!CwBreakIn)
        {
            CwKeyboardNote = "Keyboard keying needs Break-in on.";
            return false;
        }
        try
        {
            var tci = await _client!.GetTciStatusAsync();
            if (!tci.CurrentlyEnabled)
            {
                CwKeyboardNote = "Keyboard keying needs the TCI server: turn it on in Config > CAT & TCI.";
                return false;
            }
            if (_keyer?.Port != tci.CurrentPort)
            {
                _keyer?.Dispose();
                _keyer = new TciKeyer(tci.CurrentPort);
            }
            int generation = Volatile.Read(ref _txGeneration);
            await MuteMicForHoldAsync();
            _keyboardHold = true;
            _applyingTx = true;
            MoxOn = true;
            _applyingTx = false;
            if (!await SendKeyOnAsync(() => _client!.SetMoxAsync(true), generation))
            {
                ReleaseCwKeys();
                return false;
            }
            CwKeyboardNote = "";
            return true;
        }
        catch (Exception ex)
        {
            CwKeyboardNote = $"Keyboard keying failed: {ex.Message}";
            if (_keyboardHold) ReleaseCwKeys();
            else _ = RestoreMicAfterHoldAsync();
            return false;
        }
    }

    private async Task StraightDownAsync()
    {
        if (!await BeginKeyingAsync() || !_straightDown)
        {
            _straightDown = false;
            StartCwHang();
            return;
        }
        await DownAsync(MaxKeyDownMs);
        if (!_straightDown) await SendKeyUpAsync();   // released during the send
    }

    private async Task DownAsync(int ms)
    {
        try { await _keyer!.DownAsync(ms); }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            CwKeyboardNote = $"Keyboard keying failed: {ex.Message}";
            ReleaseCwKeys();
        }
    }

    // Holding one paddle repeats its element; holding both alternates.
    private async Task RunPaddlesAsync()
    {
        if (_paddleRunning) return;
        _paddleRunning = true;
        try
        {
            if (!await BeginKeyingAsync()) { _ditDown = _dahDown = false; return; }
            while ((_ditDown || _dahDown) && KeyboardAllowed)
            {
                bool dit = _ditDown && _dahDown ? !_lastWasDit : _ditDown;
                _lastWasDit = dit;
                int unit = 1200 / Math.Max(5, CwWpm);
                int length = dit ? unit : unit * 3;
                await DownAsync(length);
                await Task.Delay(length + unit);
            }
        }
        finally
        {
            _paddleRunning = false;
            StartCwHang();
        }
    }

    // Hand keying leaves longer gaps than the arrows, which are timed.
    private int StraightHangMs => Math.Max(CwHangMs * 2, 700);

    private void StartCwHang(int? hangMs = null)
    {
        if (!_keyboardHold || AnyKeyDown) return;
        if (_cwHangTimer is null)
        {
            _cwHangTimer = new DispatcherTimer();
            _cwHangTimer.Tick += (_, _) =>
            {
                _cwHangTimer!.Stop();
                if (!AnyKeyDown && _keyboardHold) MoxOn = false;
            };
        }
        _cwHangTimer.Stop();
        _cwHangTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, hangMs ?? CwHangMs));
        _cwHangTimer.Start();
    }
}
