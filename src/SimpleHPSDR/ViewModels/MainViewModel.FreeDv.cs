using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Engine;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// FreeDV digital voice: RADE V1, hosted in the engine. Choosing FreeDV in the
/// Mode list selects RADE (with your callsign for its end-of-over ID and the mic
/// shaping from Config) and then sets the radio to mode FreeDV; the engine
/// decodes received speech in place of the demodulated audio and encodes the
/// mic on transmit. Leaving the mode deselects RADE. A box on the waterfall
/// shows sync, SNR, the station heard and the mic level while transmitting.
/// The classic modes (700D, 700E, 1600) came from Zeus's plugin and aren't here.
/// </summary>
public sealed partial class MainViewModel
{
    public const string FreeDvLabel = "FreeDV";
    private bool _radeSelected;
    private DispatcherTimer? _radeTimer;

    [ObservableProperty] private bool _freeDvOn;
    [ObservableProperty] private string _freeDvHeadline = "";
    [ObservableProperty] private string _freeDvDetail = "";
    [ObservableProperty] private double _freeDvMicDb = -60;
    [ObservableProperty] private bool _freeDvMicClip;

    /// <summary>FreeDV's sideband, as the engine (and the FreeDV community) use it:
    /// LSB below 10 MHz, USB from 10 MHz up, and USB on 60 m.</summary>
    public static bool FreeDvLowerAt(long hz) =>
        hz < 10_000_000 && !(hz >= 5_250_000 && hz <= 5_450_000);

    /// <summary>The engine re-signs FreeDV's filter from the dial when it applies it,
    /// but its state can still carry a sign from the mode before; show the side in use.</summary>
    internal static (int Low, int High) FreeDvEdges(RxMode mode, long hz, int low, int high)
    {
        if (mode != RxMode.FreeDv) return (low, high);
        int a = Math.Abs(low), b = Math.Abs(high);
        if (a > b) (a, b) = (b, a);
        return FreeDvLowerAt(hz) ? (-b, -a) : (a, b);
    }

    /// <summary>After connecting: offer FreeDV in the Mode list when the engine has RADE.</summary>
    private async Task LoadFreeDvAsync()
    {
        try
        {
            var rade = await _client!.GetRadeAsync();
            bool listed = Modes.Any(c => c.Value is RxMode.FreeDv);
            if (rade.Available && !listed)
            {
                int at = Modes.ToList().FindIndex(c => c.Value is RxMode.DIGU);
                Modes.Insert(at < 0 ? Modes.Count : at + 1,
                    new Choice(FreeDvLabel, RxMode.FreeDv, c => { _ = SelectModeAsync(RxMode.FreeDv); }));
                Mark(Modes, Mode);
            }
            if (Mode == RxMode.FreeDv) await SelectRadeAsync(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException) { }
    }

    /// <summary>RADE on (callsign and mic shaping sent) or off.</summary>
    private async Task SelectRadeAsync(bool on)
    {
        if (_client is not { } client) return;
        if (on)
        {
            string? call = null;
            try { call = (await client.GetOperatorAsync()).Callsign is { Length: > 0 } c ? c : null; } catch (Exception) { }
            await client.SelectRadeAsync(true, call);
            await SendRadeSettingsAsync(client);
            _radeSelected = true;
            if (call is null) Status = "FreeDV: set your callsign in Config > Station so it's sent at the end of each over.";
        }
        else
        {
            _radeSelected = false;
            await client.SelectRadeAsync(false, null);
        }
    }

    // ---- the FreeDV panel (right-click Mode while it shows FreeDV) ----

    private Latest<int>? _radeSender;
    private int _radeSends;

    private Task SendRadeSettingsAsync(StationClient client)
    {
        var s = _settings;
        return Task.WhenAll(
            client.SetRadeMicAsync(new StationClient.RadeMic(s.RadeMicAgc, s.RadeMicAgcTargetDb,
                s.RadeMicEq, s.RadeMicBassDb, s.RadeMicMidDb, s.RadeMicTrebleDb)),
            client.SetRadeTuningAsync(new StationClient.RadeTuning(s.RadeRxSquelch, s.RadeRxHoldMs, s.RadeRxMinSnrDb,
                s.RadeMicGate, s.RadeMicGateOpenDb, s.RadeMicGateHangMs, s.RadeTxLevelDb)));
    }

    /// <summary>Saved at once, sent to the engine (latest wins while dragging).</summary>
    private void SetRade(Action<Settings> change, string name)
    {
        change(_settings);
        _settings.Save();
        OnPropertyChanged(name);
        if (!IsConnected || _client is null) return;
        _radeSender ??= new Latest<int>(_ => SendRadeSettingsAsync(_client!));
        _radeSender.Send(++_radeSends);
    }

    public bool RadeRxSquelch { get => _settings.RadeRxSquelch; set => SetRade(s => s.RadeRxSquelch = value, nameof(RadeRxSquelch)); }
    public double RadeRxHoldMs { get => _settings.RadeRxHoldMs; set => SetRade(s => s.RadeRxHoldMs = Math.Round(value / 10) * 10, nameof(RadeRxHoldMs)); }
    public double RadeRxMinSnrDb { get => _settings.RadeRxMinSnrDb; set { SetRade(s => s.RadeRxMinSnrDb = Math.Round(value), nameof(RadeRxMinSnrDb)); OnPropertyChanged(nameof(RadeMinSnrText)); } }
    public string RadeMinSnrText => RadeRxMinSnrDb <= -10 ? "off" : $"{RadeRxMinSnrDb:0} dB";
    public bool RadeMicGate { get => _settings.RadeMicGate; set => SetRade(s => s.RadeMicGate = value, nameof(RadeMicGate)); }
    public double RadeMicGateOpenDb { get => _settings.RadeMicGateOpenDb; set => SetRade(s => s.RadeMicGateOpenDb = Math.Round(value), nameof(RadeMicGateOpenDb)); }
    public double RadeMicGateHangMs { get => _settings.RadeMicGateHangMs; set => SetRade(s => s.RadeMicGateHangMs = Math.Round(value / 10) * 10, nameof(RadeMicGateHangMs)); }
    public double RadeTxLevelDb { get => _settings.RadeTxLevelDb; set => SetRade(s => s.RadeTxLevelDb = Math.Round(value), nameof(RadeTxLevelDb)); }
    public bool RadeMicAgc { get => _settings.RadeMicAgc; set => SetRade(s => s.RadeMicAgc = value, nameof(RadeMicAgc)); }
    public double RadeMicAgcTargetDb { get => _settings.RadeMicAgcTargetDb; set => SetRade(s => s.RadeMicAgcTargetDb = Math.Round(value), nameof(RadeMicAgcTargetDb)); }
    public bool RadeMicEq { get => _settings.RadeMicEq; set => SetRade(s => s.RadeMicEq = value, nameof(RadeMicEq)); }
    public double RadeMicBassDb { get => _settings.RadeMicBassDb; set => SetRade(s => s.RadeMicBassDb = Math.Round(value), nameof(RadeMicBassDb)); }
    public double RadeMicMidDb { get => _settings.RadeMicMidDb; set => SetRade(s => s.RadeMicMidDb = Math.Round(value), nameof(RadeMicMidDb)); }
    public double RadeMicTrebleDb { get => _settings.RadeMicTrebleDb; set => SetRade(s => s.RadeMicTrebleDb = Math.Round(value), nameof(RadeMicTrebleDb)); }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ResetFreeDvSettings()
    {
        var d = new Settings();
        RadeRxSquelch = d.RadeRxSquelch; RadeRxHoldMs = d.RadeRxHoldMs; RadeRxMinSnrDb = d.RadeRxMinSnrDb;
        RadeMicGate = d.RadeMicGate; RadeMicGateOpenDb = d.RadeMicGateOpenDb; RadeMicGateHangMs = d.RadeMicGateHangMs;
        RadeTxLevelDb = d.RadeTxLevelDb; RadeMicAgc = d.RadeMicAgc; RadeMicAgcTargetDb = d.RadeMicAgcTargetDb;
        RadeMicEq = d.RadeMicEq; RadeMicBassDb = 0; RadeMicMidDb = 0; RadeMicTrebleDb = 0;
    }

    /// <summary>Before the engine is asked for mode FreeDV (it needs RADE selected).</summary>
    private async Task<bool> PrepareFreeDvAsync()
    {
        try
        {
            await SelectRadeAsync(true);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException)
        {
            Status = $"FreeDV isn't available: {ex.Message}";
            return false;
        }
    }

    /// <summary>The mode changed (any way): RADE and the status box follow.</summary>
    private void FollowFreeDvMode(RxMode mode)
    {
        bool on = mode == RxMode.FreeDv;
        if (on == FreeDvOn) return;
        FreeDvOn = on;
        if (on)
        {
            FreeDvHeadline = "FreeDV (RADE)";
            FreeDvDetail = "Listening for a RADE signal";
            _radeTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, async (_, _) => await PollRadeAsync());
            _radeTimer.Start();
            // Set to FreeDV some other way (a TCI or CAT program, the engine's
            // own restore): make sure RADE carries the callsign and mic settings.
            if (!_radeSelected) _ = Try(() => SelectRadeAsync(true));
        }
        else
        {
            _radeTimer?.Stop();
            // However the mode was left, RADE goes off with it.
            _ = Try(() => SelectRadeAsync(false));
        }
    }

    private async Task PollRadeAsync()
    {
        if (!IsConnected || _client is not { } client) { _radeTimer?.Stop(); return; }
        StationClient.RadeStatus r;
        try { r = await client.GetRadeAsync(); }
        catch (Exception) { return; }
        if (IsTransmitting)
        {
            FreeDvHeadline = "FreeDV (RADE): transmitting";
            FreeDvMicDb = Math.Clamp(r.TxMicLevelDb, -60, 0);
            FreeDvMicClip = r.TxMicClip;
            FreeDvDetail = r.TxMicClip ? "Mic too loud: turn it down" : $"Mic {r.TxMicLevelDb:0} dB";
            return;
        }
        FreeDvMicClip = false;
        FreeDvHeadline = r.Synced ? "FreeDV (RADE): in sync" : "FreeDV (RADE)";
        var parts = new List<string>();
        parts.Add(r.Synced ? $"SNR {r.SnrDb:0} dB" : "Listening for a RADE signal");
        if (r.RxText is { Length: > 0 } heard) parts.Add($"heard {heard.Trim()}");
        FreeDvDetail = string.Join(" · ", parts);
    }
}

public static class FreeDvText
{
    /// <summary>Mic bar: green, red when clipping.</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter MicBrush =
        new Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Media.IBrush>(clip =>
            clip ? Avalonia.Media.Brushes.Red : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3DD68C")));
}
