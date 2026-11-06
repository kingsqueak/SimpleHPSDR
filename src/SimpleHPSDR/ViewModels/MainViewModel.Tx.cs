using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Engine;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

public sealed partial class MainViewModel
{
    private bool _applyingTx;
    private int _txMeterScheduled;
    private TxMetersV2Frame _latestTx;
    private float _latestMicDbfs = -120;
    private int _micScheduled;
    private Latest<int>? _driveSender;
    private Latest<int>? _tuneDriveSender;
    private Latest<int>? _micGainSender;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsTransmitting))]
    private bool _moxOn;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsTransmitting))]
    private bool _tunOn;
    [ObservableProperty] private double _drivePct;
    [ObservableProperty] private double _tunePct;
    [ObservableProperty] private double _micGainDb;
    [ObservableProperty] private double _micLevelDbfs = -60;
    [ObservableProperty] private double _fwdWatts;
    [ObservableProperty] private string _swrText = "—";
    [ObservableProperty] private string _alcText = "—";
    // "PC" for a computer (or ASIO) input, "HW" for one of the radio's jacks.
    // The source itself is chosen in Configuration > Audio.
    [ObservableProperty] private string _micSourceLabel = "PC";
    [ObservableProperty] private string _micSourceDetail = "";
    public bool IsTransmitting => MoxOn || TunOn;
    public double PowerScaleWatts { get; private set; } = 100;

    /// <summary>Config > Amp's maximum drive: the top of the DRIVE and TUNE sliders.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriveLine), nameof(DriveWattsText), nameof(TuneLine), nameof(TuneWattsText))]
    private int _driveCeilingPct = 100;

    // The engine has already lowered anything above a new maximum; no slider
    // may be open to do it here, so match it, without sending it back.
    private void ApplyDriveCeiling(int ceilingPct)
    {
        DriveCeilingPct = ceilingPct;
        _applyingTx = true;
        if (DrivePct > ceilingPct) DrivePct = ceilingPct;
        if (TunePct > ceilingPct) TunePct = ceilingPct;
        _applyingTx = false;
    }

    // The sliders read 100% at the maximum, whatever it's set to.
    private string OfCeiling(double pct) => $"{pct / Math.Max(1, DriveCeilingPct) * 100:0}%";
    private string Watts(double pct) => $"{pct / 100 * PowerScaleWatts:0} W";

    public string DriveWattsText => Watts(DrivePct);
    public string TuneWattsText => Watts(TunePct);
    public string DriveLine => $"{OfCeiling(DrivePct)}  {Watts(DrivePct)}";
    public string TuneLine => $"{OfCeiling(TunePct)}  {Watts(TunePct)}";

    private void SetPowerScale(int ratedWatts)
    {
        PowerScaleWatts = ratedWatts > 0 ? ratedWatts : 100;
        OnPropertyChanged(nameof(PowerScaleWatts));
        OnPropertyChanged(nameof(DriveWattsText));
        OnPropertyChanged(nameof(TuneWattsText));
        OnPropertyChanged(nameof(DriveLine));
        OnPropertyChanged(nameof(TuneLine));
    }

    private void WireTransmit(StationClient client)
    {
        _driveSender = new Latest<int>(p => client.SetDriveAsync(p));
        _tuneDriveSender = new Latest<int>(p => client.SetTuneDriveAsync(p));
        _micGainSender = new Latest<int>(async db =>
        {
            await _micGate.WaitAsync();
            try { if (_settings.CwHeldMicDb is null) await client.SetMicGainAsync(db); }
            finally { _micGate.Release(); }
        });

        // Key state from any source: our buttons, the radio's PTT, TCI, timeout.
        client.MoxState += f => Dispatcher.UIThread.Post(() =>
        {
            _applyingTx = true;
            MoxOn = f.MoxOn;
            TunOn = f.TunOn;
            _applyingTx = false;
            if (!IsTransmitting) ResetTxMeters();
            UpdatePsStatus();   // also tracks key-down time for PureSignal
            TrackKeyForMonitor();
            TrackKeyForAlert();
        });
        client.TxMeters += f =>
        {
            _latestTx = f;
            if (Interlocked.Exchange(ref _txMeterScheduled, 1) == 0)
                Dispatcher.UIThread.Post(() =>
                {
                    _txMeterScheduled = 0;
                    if (IsTransmitting) UpdateTxMeters(_latestTx);
                });
        };
        client.MicPeakDbfs += db =>
        {
            _micFrameMs = Environment.TickCount64;
            _latestMicDbfs = db;
            if (Interlocked.Exchange(ref _micScheduled, 1) == 0)
                Dispatcher.UIThread.Post(() =>
                {
                    _micScheduled = 0;
                    // Fast attack, slow release.
                    double k = _latestMicDbfs > MicLevelDbfs ? 0.7 : 0.15;
                    MicLevelDbfs += (Math.Max(_latestMicDbfs, -60) - MicLevelDbfs) * k;
                    if (VoxOn) CheckVox();   // as soon as it's heard, not on the next tick
                });
        };
    }

    private async Task LoadTransmitAsync(StateDto s)
    {
        // The app stopped during a CW hold: the engine still has the mic muted.
        if (_settings.CwHeldMicDb is int heldMic)
        {
            await Try(() => _client!.SetMicGainAsync(heldMic));
            s = s with { MicGainDb = heldMic };
            _settings.CwHeldMicDb = null;
            _settings.Save();
        }

        // The engine ships with no PTT release delay, which cuts off the end
        // of every over (the TX processing holds ~210 ms of speech). Give it
        // the app default once, on the first connect.
        if (!_settings.ReleaseDelayDefaulted)
        {
            try
            {
                if (s.TxMoxTailDelayMs == 0)
                    await _client!.SetTxTailDelayAsync(TxAudioProfile.DefaultReleaseDelayMs);
                _settings.ReleaseDelayDefaulted = true;
                _settings.Save();
            }
            catch (Exception ex) { Status = ex.Message; }   // tried again next connect
        }

        _applyingTx = true;
        try
        {
            DriveCeilingPct = s.DriveMaxPct;
            DrivePct = s.DrivePct;
            TunePct = s.TunePct;
            MicGainDb = s.MicGainDb;
            MoxOn = false;
            TunOn = false;
        }
        finally
        {
            _applyingTx = false;
        }
        await LoadMicSourcesAsync();
        try { SetPowerScale((await _client!.GetPaSettingsAsync()).Global.PaMaxPowerWatts); }
        catch (Exception) { }
    }

    private async Task LoadMicSourcesAsync()
    {
        try
        {
            var radio = await _client!.GetRadioAudioAsync();
            if (radio.Source == TxAudioSource.Host)
            {
                var devices = await _client.GetAudioDevicesAsync();
                var name = devices.Backend == "asio"
                    ? devices.AsioInputs?.FirstOrDefault(c => c.Index == devices.AsioInputChannel)?.Name ?? "ASIO"
                    : devices.Inputs.FirstOrDefault(d => d.Id == devices.InputDeviceId)?.Name
                           ?? devices.Inputs.FirstOrDefault(d => d.IsDefault)?.Name ?? "system default";
                MicSourceLabel = "PC";
                MicSourceDetail = $"Computer mic: {name}";
            }
            else
            {
                MicSourceLabel = "HW";
                MicSourceDetail = radio.Source switch
                {
                    TxAudioSource.RadioMic => "Radio mic jack" + (radio.MicBoost ? ", boost on" : ""),
                    TxAudioSource.RadioLineIn => "Radio line in",
                    TxAudioSource.RadioBalancedXlr => "Radio XLR input",
                    _ => radio.Source.ToString(),
                };
            }
        }
        catch (Exception ex)
        {
            Status = $"Could not read audio source: {ex.Message}";
        }
    }

    partial void OnMoxOnChanged(bool value)
    {
        if (!value) _voxKeyed = false;   // whoever dropped it, VOX starts afresh
        if (!value && _keyboardHold)   // from the hang, the button or the engine
        {
            _ = EndHoldAsync();
            return;
        }
        if (_applyingTx || !IsConnected) return;
        CancelPsCalForOperatorKey();
        if (!value && CwSending) _ = StopCwAsync();   // MOX off also ends queued text
        _ = KeyAsync(() => _client!.SetMoxAsync(value), value);
    }

    partial void OnTunOnChanged(bool value)
    {
        if (_applyingTx || !IsConnected) return;
        CancelPsCalForOperatorKey();
        _ = KeyAsync(() => _client!.SetTunAsync(value), value);
    }

    private bool _momentaryTx;

    /// <summary>The mini window's TX button went down. Only what this press
    /// keyed is unkeyed by its release, so MOX from elsewhere is left alone.</summary>
    public void MomentaryTxDown()
    {
        if (!IsConnected || MoxOn || TunOn) return;
        _momentaryTx = true;
        MoxOn = true;
    }

    public void MomentaryTxUp()
    {
        if (!_momentaryTx) return;
        _momentaryTx = false;
        MoxOn = false;
    }

    // Keying goes out one at a time, and an unkey outranks any key-on still
    // waiting: a slow request must never key the radio after it was told to stop.
    private readonly SemaphoreSlim _txGate = new(1, 1);
    private int _txGeneration;

    /// <summary>Send a key-on, unless an unkey came after it was asked for.</summary>
    private async Task<bool> SendKeyOnAsync(Func<Task> send, int generation)
    {
        await _txGate.WaitAsync();
        try
        {
            if (generation != Volatile.Read(ref _txGeneration)) return false;
            await send();
            return true;
        }
        finally { _txGate.Release(); }
    }

    // If the engine refuses to key, show why and put the button back.
    private async Task KeyAsync(Func<Task> send, bool on)
    {
        try
        {
            if (on)
            {
                if (!await SendKeyOnAsync(send, Volatile.Read(ref _txGeneration))) ClearKeyButtons();
                return;
            }
            bool held = await _txGate.WaitAsync(UnkeyGateWait);
            try { await send(); }
            finally { if (held) _txGate.Release(); }
        }
        catch (Exception ex)
        {
            Status = $"Transmit refused: {ex.Message}";
            if (on) ClearKeyButtons();
        }
    }

    private void ClearKeyButtons()
    {
        _applyingTx = true;
        MoxOn = false;
        TunOn = false;
        TwoToneOn = false;
        _applyingTx = false;
    }

    private static readonly TimeSpan UnkeyGateWait = TimeSpan.FromSeconds(1.5);

    partial void OnDrivePctChanged(double value)
    {
        OnPropertyChanged(nameof(DriveLine));
        OnPropertyChanged(nameof(DriveWattsText));
        if (!_applyingTx && IsConnected) _driveSender?.Send((int)Math.Round(value));
    }

    partial void OnTunePctChanged(double value)
    {
        OnPropertyChanged(nameof(TuneLine));
        OnPropertyChanged(nameof(TuneWattsText));
        if (!_applyingTx && IsConnected) _tuneDriveSender?.Send((int)Math.Round(value));
    }

    partial void OnMicGainDbChanged(double value)
    {
        // While a CW hold has the mic muted, this must not bring it back: it
        // becomes the level to return to.
        if (_settings.CwHeldMicDb is not null)
        {
            if (!_applyingTx) _settings.CwHeldMicDb = (int)Math.Round(value);
            return;
        }
        if (!_applyingTx && IsConnected) _micGainSender?.Send((int)Math.Round(value));
    }

    private void UpdateTxMeters(TxMetersV2Frame f)
    {
        FwdWatts = Math.Max(0, f.FwdWatts) * Math.Pow(10, _settings.PowerMeterTrimDb / 10);
        SwrText = f.Swr is > 0.9f and < 100 ? $"{f.Swr:0.0}:1" : "—";
        AlcText = $"{f.AlcGr:0} dB";
        CheckSwr(f.Swr, Environment.TickCount64);
    }

    private void ResetTxMeters()
    {
        FwdWatts = 0;
        SwrText = "—";
        AlcText = "—";
    }

    /// <summary>Unkey before letting go of the radio. Always sends both,
    /// since the radio's PTT can key without this UI knowing yet.</summary>
    private async Task UnkeyAsync()
    {
        if (_client is not { } client) return;
        Interlocked.Increment(ref _txGeneration);
        try { _psCalCts?.Cancel(); } catch (ObjectDisposedException) { }
        _straightDown = _ditDown = _dahDown = _keyboardHold = false;
        await SendKeyUpAsync();
        _voxKeyed = false;
        bool held = await _txGate.WaitAsync(UnkeyGateWait);
        try
        {
            if (CwSending) try { _cwAbortSeq = await client.AbortCwAsync(); } catch (Exception) { }
            try { await client.SetMoxAsync(false); } catch (Exception) { }
            try { await client.SetTunAsync(false); } catch (Exception) { }
            try { await client.SetTwoToneAsync(false); } catch (Exception) { }
            await RestoreMicAfterHoldAsync();
        }
        finally { if (held) _txGate.Release(); }
    }

    // Shutdown runs on the UI thread or a signal thread; run the HTTP calls on
    // the pool so their continuations can't deadlock against this wait.
    private void UnkeyForShutdown() =>
        Task.Run(UnkeyAsync).Wait(TimeSpan.FromSeconds(3));
}
