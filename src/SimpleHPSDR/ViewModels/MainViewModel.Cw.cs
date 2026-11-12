using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>One of the engine's six CW macros.</summary>
public sealed partial class CwMacro(int number, string text, Action changed) : ObservableObject
{
    public int Number { get; } = number;
    [ObservableProperty] private string _text = text;
    public string Label => string.IsNullOrWhiteSpace(Text) ? $"{Number}" : Text.Length > 14 ? Text[..14] + "…" : Text;
    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(Label));
        changed();
    }
}

/// <summary>
/// CW: the engine keeps the settings and does the keying. A paddle or key on
/// the radio works by itself; text only goes out when the operator sends it,
/// and Stop or any unkey drops the rest. Settings are sent as patches.
/// </summary>
public sealed partial class MainViewModel
{
    public static string[] CwKeyerNames { get; } = ["Straight key", "Iambic A", "Iambic B"];

    public bool ModeIsCw => Mode is RxMode.CWL or RxMode.CWU;

    [ObservableProperty] private int _cwWpm = 20;
    [ObservableProperty] private int _cwKeyerIndex = 2;
    [ObservableProperty] private int _cwSidetoneHz = 600;
    [ObservableProperty] private double _cwSidetoneDb = -20;
    [ObservableProperty] private bool _cwBreakIn = true;
    [ObservableProperty] private int _cwHangMs = 300;
    [ObservableProperty] private int _cwWeight = 50;
    [ObservableProperty] private bool _cwPaddleReverse;
    [ObservableProperty] private int _cwFarnsworthWpm;   // 0 is off

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCwTextCommand))] private string _cwSendText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CwStateText))] private bool _cwSending;
    [ObservableProperty] private string _cwSendingText = "";
    [ObservableProperty] private bool _cwDecoderOn;
    [ObservableProperty] private string _cwDecodedText = "";
    [ObservableProperty] private string _cwDecodedInfo = "";
    public ObservableCollection<CwMacro> CwMacros { get; } = [];

    public string CwWpmText => $"{CwWpm} WPM";
    public string CwStateText => CwSending ? "Sending" : "Ready";

    private bool _applyingCw;
    // Set once the engine's CW settings (macros included) have been read; until
    // then a change would send defaults and an empty macro list over them.
    private bool _cwLoaded;
    private int _cwAbortSeq;
    private int _cwSeq;
    private Latest<int>? _cwSender;
    private const int MaxDecodedChars = 4000;

    private async Task LoadCwAsync()
    {
        await Try(async () =>
        {
            var s = await _client!.GetCwSettingsAsync();
            _cwAbortSeq = await _client.GetCwAbortSeqAsync();
            ShowCwSettings(s);
        });
    }

    private void ShowCwSettings(CwSettingsDto s)
    {
        _applyingCw = true;
        CwWpm = s.Wpm;
        CwKeyerIndex = (int)s.KeyerMode;
        CwSidetoneHz = s.SidetoneHz;
        CwSidetoneDb = s.SidetoneGainDb;
        CwBreakIn = s.BreakIn;
        CwHangMs = s.HangMs;
        CwWeight = s.Weight;
        CwPaddleReverse = s.PaddleReverse;
        CwFarnsworthWpm = s.FarnsworthWpm ?? 0;
        // Rows being edited stay; only a change from elsewhere replaces them.
        if (CwMacros.Count != s.Macros.Length || !CwMacros.Select(m => m.Text).SequenceEqual(s.Macros))
        {
            CwMacros.Clear();
            for (int i = 0; i < s.Macros.Length; i++) CwMacros.Add(new CwMacro(i + 1, s.Macros[i], SendCwSettings));
        }
        _applyingCw = false;
        _cwLoaded = true;
        OnPropertyChanged(nameof(CwWpmText));
    }

    private void SendCwSettings()
    {
        if (_applyingCw || !_cwLoaded || !IsConnected) return;
        _cwSender ??= new Latest<int>(_ => Try(() => _client!.SetCwSettingsAsync(new CwSettingsSetRequest(
            Wpm: CwWpm, FarnsworthWpm: CwFarnsworthWpm, Macros: [.. CwMacros.Select(m => m.Text)],
            SidetoneGainDb: CwSidetoneDb, SidetoneHz: CwSidetoneHz, KeyerMode: (CwKeyerMode)CwKeyerIndex,
            BreakIn: CwBreakIn, HangMs: CwHangMs, Weight: CwWeight, PaddleReverse: CwPaddleReverse))));
        _cwSender.Send(++_cwSeq);
    }

    partial void OnCwWpmChanged(int value)
    {
        OnPropertyChanged(nameof(CwWpmText));
        SendCwSettings();
    }
    partial void OnCwKeyerIndexChanged(int value) => SendCwSettings();
    partial void OnCwSidetoneHzChanged(int value) => SendCwSettings();
    partial void OnCwSidetoneDbChanged(double value) => SendCwSettings();
    partial void OnCwBreakInChanged(bool value) => SendCwSettings();
    partial void OnCwHangMsChanged(int value) => SendCwSettings();
    partial void OnCwWeightChanged(int value) => SendCwSettings();
    partial void OnCwPaddleReverseChanged(bool value)
    {
        SendCwSettings();
        _ditDown = _dahDown = false;   // a held arrow changes meaning, so let it go
    }
    partial void OnCwFarnsworthWpmChanged(int value) => SendCwSettings();

    /// <summary>The wheel over the WPM button.</summary>
    public void NudgeCwWpm(int clicks) => CwWpm = Math.Clamp(CwWpm + clicks, 5, 50);

    private bool CanSendCw() => !string.IsNullOrWhiteSpace(CwSendText);

    [RelayCommand(CanExecute = nameof(CanSendCw))]
    private async Task SendCwTextAsync()
    {
        var text = CwSendText.Trim();
        if (await SendCwAsync(text)) CwSendText = "";
    }

    [RelayCommand]
    private Task SendCwMacroAsync(CwMacro? macro) =>
        string.IsNullOrWhiteSpace(macro?.Text) ? Task.CompletedTask : SendCwAsync(macro.Text.Trim());

    private async Task<bool> SendCwAsync(string text)
    {
        if (!IsConnected || !ModeIsCw)
        {
            Status = "Switch to CWL or CWU to send CW.";
            return false;
        }
        try
        {
            await _client!.SendCwAsync(text, _cwAbortSeq);
            return true;
        }
        catch (Exception ex)
        {
            Status = $"CW not sent: {ex.Message}";
            return false;
        }
    }

    [RelayCommand]
    private Task StopCwAsync() => Try(async () => _cwAbortSeq = await _client!.AbortCwAsync());

    [RelayCommand]
    private void ClearCwDecoded()
    {
        _cwDecodedRaw = "";
        CwDecodedText = "";
    }

    /// <summary>The main window's spectrum for a slice: strongest signal between two
    /// frequencies, at least so many dB over the noise, or null.</summary>
    public Func<int, long, long, double, double?>? FindSignalHz { get; set; }

    /// <summary>CWT: tune so the strongest signal in or near the passband lands on the
    /// CW pitch. In CW the dial is the received carrier, so that's tuning onto it.</summary>
    [RelayCommand]
    private void CwTune()
    {
        if (!IsConnected) return;
        if (!ModeIsCw) { Status = "CWT works in CWU or CWL."; return; }
        int shift = CwShift(Mode);
        long lo = Math.Min(FilterLowHz + shift, -250), hi = Math.Max(FilterHighHz + shift, 250);
        if (FindSignalHz?.Invoke(ActiveRx, VfoHz + lo, VfoHz + hi, 10) is not { } hz)
        {
            Status = "CWT: no CW signal in or near the passband.";
            return;
        }
        long delta = (long)Math.Round(hz) - VfoHz;
        if (Math.Abs(delta) < 3) { Status = "CWT: already on the signal."; return; }
        TuneTo(VfoHz + delta);
        Status = $"CWT: moved {delta:+0;-0} Hz onto the signal.";
    }

    partial void OnCwDecoderOnChanged(bool value)
    {
        if (_settings.CwDecoderOn != value)
        {
            _settings.CwDecoderOn = value;
            _settings.Save();
        }
        if (IsConnected) _ = Try(() => _client!.SetCwDecoderAsync(value));
        UpdateDeepCw();
    }

    private void OnCwStatus(CwEngineStatusFrame f)
    {
        _cwAbortSeq = f.AbortSeq;
        CwSending = f.State != CwEngineState.Idle;
        CwSendingText = CwSending ? f.Text : "";
        if (f.Reason is { Length: > 0 } reason) Status = $"CW: {reason}";
    }

    private void OnCwDecoded(CwDecodedTextFrame f)
    {
        if (f.Receiver != ActiveRx) return;
        if (!DeepCwSelected) AppendCwDecoded(f.Text);   // with DeepCW chosen, only its speed and pitch are used
        (_cwInfoWpm, _cwInfoPitch) = (f.Wpm, f.PitchHz);
        ShowCwDecoderInfo();
    }
}
