using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Engine;
using SimpleHPSDR.Contracts;

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
    /// <summary>The waterfall's range below the transmit peak while keyed, or 0 (receive range).</summary>
    public double TxWaterfallRangeDb => IsTransmitting ? _settings.TxWaterfallRangeDb : 0;
    public double PowerScaleWatts { get; private set; } = 100;

    /// <summary>The top of the DRIVE slider: the amplifier's maximum while Amp
    /// is on, the radio's full power otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriveLine), nameof(DriveWattsText))]
    private int _driveCeilingPct = 100;

    /// <summary>The top of the TUNE slider, likewise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TuneLine), nameof(TuneWattsText))]
    private int _tuneCeilingPct = 100;

    // ---- Amp: an external amplifier is in line ---------------------------
    // The engine holds the switch, the two maximums and a separate set of
    // remembered Drive and Tune levels (per band and per mode) for each state.

    [ObservableProperty] private bool _ampOn;

    /// <summary>The amplifier's maximums (Amp's right-click, and Config > Amp).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AmpDriveMaxText))] private double _ampDriveMaxPct = 100;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AmpTuneMaxText))] private double _ampTuneMaxPct = 100;
    public string AmpDriveMaxText => $"{AmpDriveMaxPct:0}%  {Watts(AmpDriveMaxPct)}";
    public string AmpTuneMaxText => $"{AmpTuneMaxPct:0}%  {Watts(AmpTuneMaxPct)}";
    private Latest<int>? _driveMaxSender, _tuneMaxSender;

    // When a level or maximum was last changed here. The state poll leaves
    // them alone for a moment after, so it can't put back a value the engine
    // hasn't received yet.
    private DateTime _txEditedAt;
    private void NoteTxEdit() => _txEditedAt = DateTime.UtcNow;
    private bool TxEditedLately => DateTime.UtcNow - _txEditedAt < TimeSpan.FromSeconds(2);

    partial void OnAmpDriveMaxPctChanged(double value)
    {
        if (_applyingTx || !IsConnected) return;
        NoteTxEdit();
        (_driveMaxSender ??= new Latest<int>(p => Try(() => _client!.SetDriveMaxAsync(p)))).Send((int)Math.Round(value));
    }

    partial void OnAmpTuneMaxPctChanged(double value)
    {
        if (_applyingTx || !IsConnected) return;
        NoteTxEdit();
        (_tuneMaxSender ??= new Latest<int>(p => Try(() => _client!.SetTuneMaxAsync(p)))).Send((int)Math.Round(value));
    }

    /// <summary>Takes the amplifier switch, the maximums and the slider tops
    /// from the engine, and the Drive and Tune levels when asked (the engine
    /// changes them itself on a band or mode change). Nothing is sent back.</summary>
    private void ApplyTxLimits(StateDto s, bool levelsToo)
    {
        _applyingTx = true;
        try
        {
            int driveTop = s.AmpOn ? Math.Clamp(s.DriveMaxPct, 1, 100) : 100;
            int tuneTop = s.AmpOn ? Math.Clamp(s.TuneMaxPct, 1, 100) : 100;
            AmpOn = s.AmpOn;
            if (levelsToo)
            {
                AmpDriveMaxPct = s.DriveMaxPct;
                AmpTuneMaxPct = s.TuneMaxPct;
            }
            // Raise a slider's top before its value and lower it after, so an
            // open slider never has to cut the value itself.
            if (driveTop > DriveCeilingPct) DriveCeilingPct = driveTop;
            if (tuneTop > TuneCeilingPct) TuneCeilingPct = tuneTop;
            if (levelsToo)
            {
                DrivePct = s.DrivePct;
                TunePct = s.TunePct;
            }
            if (DrivePct > driveTop) DrivePct = driveTop;
            if (TunePct > tuneTop) TunePct = tuneTop;
            DriveCeilingPct = driveTop;
            TuneCeilingPct = tuneTop;
        }
        finally { _applyingTx = false; }
    }

    // The sliders read 100% at their top, whatever it's set to.
    private static string OfCeiling(double pct, int ceiling) => $"{pct / Math.Max(1, ceiling) * 100:0}%";
    private string Watts(double pct) => $"{pct / 100 * PowerScaleWatts:0} W";

    public string DriveWattsText => Watts(DrivePct);
    public string TuneWattsText => Watts(TunePct);
    public string DriveLine => $"{OfCeiling(DrivePct, DriveCeilingPct)}  {Watts(DrivePct)}";
    public string TuneLine => $"{OfCeiling(TunePct, TuneCeilingPct)}  {Watts(TunePct)}";

    // The radio's own rating when the engine gives one (a Brick: 15 W driven
    // through the 100 W gain table, so 100 % drive is 15 W, not 100 W).
    private int? _ratedWatts;

    private void SetPowerScale(int maxPowerWatts)
    {
        int watts = _ratedWatts is int r && r > 0 ? r : maxPowerWatts;
        PowerScaleWatts = watts > 0 ? watts : 100;
        OnPropertyChanged(nameof(PowerScaleWatts));
        OnPropertyChanged(nameof(DriveWattsText));
        OnPropertyChanged(nameof(TuneWattsText));
        OnPropertyChanged(nameof(DriveLine));
        OnPropertyChanged(nameof(TuneLine));
        OnPropertyChanged(nameof(AmpDriveMaxText));
        OnPropertyChanged(nameof(AmpTuneMaxText));
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
            TrackEngineKeyForVox(f.MoxOn || f.TunOn);
            TrackEngineKeyForPs(f.MoxOn || f.TunOn);
            _applyingTx = true;
            MoxOn = f.MoxOn;
            TunOn = f.TunOn;
            _applyingTx = false;
            if (!IsTransmitting) ResetTxMeters();
            UpdatePsStatus();   // also tracks key-down time for PureSignal
            TrackKeyForMonitor();
            TrackKeyForAlert();
            UpdateDrmVolume();
            TrackFmDeviation();
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

        // 0.10.7 shortened the timing defaults. Move values still on the old
        // defaults once; anything the operator chose stays.
        if (!_settings.TimingDefaultsMoved)
        {
            try
            {
                var moved = new List<string>();
                if (s.TxMoxTailDelayMs == TimingDefaults.OldReleaseDelayMs)
                {
                    await _client!.SetTxTailDelayAsync(TimingDefaults.ReleaseDelayMs);
                    moved.Add($"PTT release {TimingDefaults.ReleaseDelayMs} ms");
                }
                if (s.TxPostTxRxMuteDelayMs is TimingDefaults.OldRxResumeDelayMs or TimingDefaults.OlderRxResumeDelayMs)
                {
                    await _client!.SetRxResumeDelayAsync(TimingDefaults.RxResumeDelayMs);
                    moved.Add($"RX resume {TimingDefaults.RxResumeDelayMs} ms");
                }
                if (_settings.VoxDelayMs == TimingDefaults.OldVoxDelayMs)
                {
                    VoxDelayMs = TimingDefaults.VoxDelayMs;
                    moved.Add($"VOX delay {TimingDefaults.VoxDelayMs:0} ms");
                }
                int profiles = TxProfileStore.MoveOldReleaseDelay();
                if (profiles > 0) moved.Add($"{profiles} TX profile{(profiles == 1 ? "" : "s")}");
                _settings.TimingDefaultsMoved = true;
                _settings.Save();
                if (moved.Count > 0)
                    Status = "Shorter transmit timing defaults applied: " + string.Join(", ", moved) + ".";
            }
            catch (Exception ex) { Status = ex.Message; }   // tried again next connect
        }

        ApplyTxLimits(s, levelsToo: true);
        _applyingTx = true;
        try
        {
            MicGainDb = s.MicGainDb;
            MoxOn = false;
            TunOn = false;
        }
        finally
        {
            _applyingTx = false;
        }
        await LoadMicSourcesAsync();
        try { _ratedWatts = (await _client!.GetBoardFlagsAsync()).RatedPowerWatts; }
        catch (Exception) { _ratedWatts = null; }
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
        if (!value) VoxSawUnkey(fromEngine: _applyingTx);
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
        if (_applyingTx || !IsConnected) return;
        NoteTxEdit();
        _driveSender?.Send((int)Math.Round(value));
    }

    partial void OnTunePctChanged(double value)
    {
        OnPropertyChanged(nameof(TuneLine));
        OnPropertyChanged(nameof(TuneWattsText));
        if (_applyingTx || !IsConnected) return;
        NoteTxEdit();
        _tuneDriveSender?.Send((int)Math.Round(value));
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
        if (!FmDeviationShown)
        {
            AlcText = $"{f.AlcGr:0} dB";
            ShowTxLevel(Math.Clamp(f.AlcGr / AlcScaleDb, 0, 1));
        }
        CheckSwr(f.Swr, Environment.TickCount64);
    }

    private void ResetTxMeters()
    {
        FwdWatts = 0;
        SwrText = "—";
        AlcText = "—";
        ClearTxLevel();
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
