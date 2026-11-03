using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Engine;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>A transmit audio source: a computer input device or one of the radio's jacks.</summary>
public sealed record MicSource(string Label, TxAudioSource Source, string? DeviceId)
{
    public override string ToString() => Label;
}

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
    [ObservableProperty] private MicSource? _selectedMicSource;
    [ObservableProperty] private bool _micBoost;
    [ObservableProperty] private bool _micBoostAvailable;

    public ObservableCollection<MicSource> MicSources { get; } = [];
    public bool IsTransmitting => MoxOn || TunOn;
    public double PowerScaleWatts { get; private set; } = 100;

    private void SetPowerScale(int ratedWatts)
    {
        PowerScaleWatts = ratedWatts > 0 ? ratedWatts : 100;
        OnPropertyChanged(nameof(PowerScaleWatts));
    }

    private void WireTransmit(StationClient client)
    {
        _driveSender = new Latest<int>(p => client.SetDriveAsync(p));
        _tuneDriveSender = new Latest<int>(p => client.SetTuneDriveAsync(p));
        _micGainSender = new Latest<int>(db => client.SetMicGainAsync(db));

        // Key state from any source: our buttons, the radio's PTT, TCI, timeout.
        client.MoxState += f => Dispatcher.UIThread.Post(() =>
        {
            _applyingTx = true;
            MoxOn = f.MoxOn;
            TunOn = f.TunOn;
            _applyingTx = false;
            if (!IsTransmitting) ResetTxMeters();
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
            _latestMicDbfs = db;
            if (Interlocked.Exchange(ref _micScheduled, 1) == 0)
                Dispatcher.UIThread.Post(() =>
                {
                    _micScheduled = 0;
                    // Fast attack, slow release.
                    double k = _latestMicDbfs > MicLevelDbfs ? 0.7 : 0.15;
                    MicLevelDbfs += (Math.Max(_latestMicDbfs, -60) - MicLevelDbfs) * k;
                });
        };
    }

    private async Task LoadTransmitAsync(StateDto s)
    {
        _applyingTx = true;
        try
        {
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
        var client = _client!;
        MicSources.Clear();
        MicSource? current = null;
        try
        {
            var devices = await client.GetAudioDevicesAsync();
            foreach (var d in devices.Inputs.DistinctBy(d => d.Id))
            {
                var m = new MicSource($"Computer: {d.Name}", TxAudioSource.Host, d.Id);
                MicSources.Add(m);
            }
            var radio = await client.GetRadioAudioAsync();
            if (radio.HasOnboardCodec)
            {
                MicSources.Add(new MicSource("Radio: mic jack", TxAudioSource.RadioMic, null));
                if (radio.HasRadioLineIn) MicSources.Add(new MicSource("Radio: line in", TxAudioSource.RadioLineIn, null));
                if (radio.HasBalancedXlr) MicSources.Add(new MicSource("Radio: XLR", TxAudioSource.RadioBalancedXlr, null));
            }

            current = radio.Source == TxAudioSource.Host
                ? MicSources.FirstOrDefault(m => m.Source == TxAudioSource.Host && m.DeviceId == devices.InputDeviceId)
                  ?? MicSources.FirstOrDefault(m => m.Source == TxAudioSource.Host
                                                    && devices.Inputs.Any(d => d.Id == m.DeviceId && d.IsDefault))
                : MicSources.FirstOrDefault(m => m.Source == radio.Source);
            _applyingTx = true;
            MicBoost = radio.MicBoost;
        }
        catch (Exception ex)
        {
            Status = $"Could not read audio sources: {ex.Message}";
        }
        finally
        {
            SelectedMicSource = current;
            MicBoostAvailable = current?.Source == TxAudioSource.RadioMic;
            _applyingTx = false;
        }
    }

    partial void OnMoxOnChanged(bool value)
    {
        if (_applyingTx || !IsConnected) return;
        _ = KeyAsync(() => _client!.SetMoxAsync(value), value);
    }

    partial void OnTunOnChanged(bool value)
    {
        if (_applyingTx || !IsConnected) return;
        _ = KeyAsync(() => _client!.SetTunAsync(value), value);
    }

    // If the engine refuses to key, show why and put the button back.
    private async Task KeyAsync(Func<Task> send, bool on)
    {
        try
        {
            await send();
        }
        catch (Exception ex)
        {
            Status = $"Transmit refused: {ex.Message}";
            _applyingTx = true;
            if (on) { MoxOn = false; TunOn = false; }
            _applyingTx = false;
        }
    }

    partial void OnDrivePctChanged(double value)
    {
        if (!_applyingTx && IsConnected) _driveSender?.Send((int)Math.Round(value));
    }

    partial void OnTunePctChanged(double value)
    {
        if (!_applyingTx && IsConnected) _tuneDriveSender?.Send((int)Math.Round(value));
    }

    partial void OnMicGainDbChanged(double value)
    {
        if (!_applyingTx && IsConnected) _micGainSender?.Send((int)Math.Round(value));
    }

    partial void OnSelectedMicSourceChanged(MicSource? value)
    {
        MicBoostAvailable = value?.Source == TxAudioSource.RadioMic;
        if (_applyingTx || !IsConnected || value is null) return;
        _ = Try(() => ApplyMicSourceAsync(value, MicBoost));
    }

    partial void OnMicBoostChanged(bool value)
    {
        if (_applyingTx || !IsConnected || SelectedMicSource is not { } m) return;
        _ = Try(() => ApplyMicSourceAsync(m, value));
    }

    private async Task ApplyMicSourceAsync(MicSource m, bool boost)
    {
        var radio = await _client!.GetRadioAudioAsync();
        if (m.Source == TxAudioSource.Host)
            await _client.SetHostInputAsync(m.DeviceId);
        if (radio.HasOnboardCodec)
            await _client.SetRadioAudioAsync(new AudioFrontEndSetRequest(
                m.Source, m.Source == TxAudioSource.RadioMic && boost, radio.MicBias, radio.LineInGain));
    }

    private void UpdateTxMeters(TxMetersV2Frame f)
    {
        FwdWatts = Math.Max(0, f.FwdWatts);
        SwrText = f.Swr is > 0.9f and < 100 ? $"{f.Swr:0.0}:1" : "—";
        AlcText = $"{f.AlcGr:0} dB";
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
        if (_client is null) return;
        try { await _client.SetMoxAsync(false); } catch (Exception) { }
        try { await _client.SetTunAsync(false); } catch (Exception) { }
    }

    // Shutdown runs on the UI thread or a signal thread; run the HTTP calls on
    // the pool so their continuations can't deadlock against this wait.
    private void UnkeyForShutdown() =>
        Task.Run(UnkeyAsync).Wait(TimeSpan.FromSeconds(2));
}
