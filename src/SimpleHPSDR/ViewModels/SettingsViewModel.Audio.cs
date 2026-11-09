using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Engine;
using SimpleHPSDR.Platform;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

public sealed record DeviceOption(string Label, string? Id)
{
    public override string ToString() => Label;
}

public sealed record ChannelOption(string Label, int Value)
{
    public override string ToString() => Label;
}

public sealed record SourceOption(string Label, TxAudioSource Source, bool Available = true)
{
    public override string ToString() => Label;
}

public sealed partial class SettingsViewModel
{
    private AudioFrontEndDto? _radioAudio;

    [ObservableProperty] private DeviceOption? _selectedOutput;
    [ObservableProperty] private DeviceOption? _selectedInput;
    [ObservableProperty] private ChannelOption? _selectedChannel;
    [ObservableProperty] private SourceOption? _selectedTxSource;
    [ObservableProperty] private bool _micBoost;
    [ObservableProperty] private bool _micBias;
    [ObservableProperty] private double _lineInGain;
    [ObservableProperty] private bool _hasRadioAudio;
    [ObservableProperty] private bool _hasMicBias;
    [ObservableProperty] private bool _hasLineIn;
    [ObservableProperty] private string _audioStatus = "";

    public ObservableCollection<DeviceOption> Outputs { get; } = [];
    public ObservableCollection<DeviceOption> Inputs { get; } = [];
    public ObservableCollection<ChannelOption> Channels { get; } = [];
    public ObservableCollection<SourceOption> TxSources { get; } = [];

    /// <summary>Raised after audio settings are saved, so the main window
    /// can refresh its mic picker.</summary>
    public event Action? AudioSaved;

    private sealed record DeviceSelection(string? InputId, string? OutputId, int Channel);
    private DeviceSelection? _devicesWas;

    private DeviceSelection CurrentDevices() =>
        new(SelectedInput?.Id, RadioJackChosen ? _computerOutputId : SelectedOutput?.Id, SelectedChannel?.Value ?? -1);

    private const string RadioJackId = "radio-speaker-jack";
    private bool _radioSpeakerAvailable;
    private string? _computerOutputId;

    private bool RadioJackChosen => SelectedOutput?.Id == RadioJackId;

    private void PopulateAudio(AudioDevices devices, AudioFrontEndDto radio)
    {
        Outputs.Clear();
        Outputs.Add(new DeviceOption("System default", null));
        foreach (var d in devices.Outputs.DistinctBy(d => d.Id)) Outputs.Add(new DeviceOption(d.Name, d.Id));
        bool toRadio = AppSettings?.AudioToRadio == true;
        if (_radioSpeakerAvailable || toRadio) Outputs.Add(new DeviceOption("Radio speaker jack", RadioJackId));
        _computerOutputId = devices.OutputDeviceId;
        SelectedOutput = toRadio ? Outputs[^1] : Outputs.FirstOrDefault(o => o.Id == devices.OutputDeviceId) ?? Outputs[0];

        Inputs.Clear();
        Inputs.Add(new DeviceOption("System default", null));
        foreach (var d in devices.Inputs.DistinctBy(d => d.Id)) Inputs.Add(new DeviceOption(d.Name, d.Id));
        SelectedInput = Inputs.FirstOrDefault(i => i.Id == devices.InputDeviceId) ?? Inputs[0];

        Channels.Clear();
        Channels.Add(new ChannelOption("Mix all channels", -1));
        for (int c = 0; c < devices.InputChannels; c++) Channels.Add(new ChannelOption($"Channel {c + 1}", c));
        SelectedChannel = Channels.FirstOrDefault(c => c.Value == (devices.InputChannel ?? -1)) ?? Channels[0];
        _devicesWas = CurrentDevices();
        PopulateAsio(devices);

        _radioAudio = radio;
        HasRadioAudio = radio.HasOnboardCodec;
        HasMicBias = radio.HasMicBias;
        HasLineIn = radio.HasRadioLineIn;
        TxSources.Clear();
        TxSources.Add(new SourceOption("Computer microphone", TxAudioSource.Host));
        TxSources.Add(new SourceOption("Radio mic jack", TxAudioSource.RadioMic, HasRadioAudio));
        TxSources.Add(new SourceOption("Radio line in", TxAudioSource.RadioLineIn, HasRadioAudio && HasLineIn));
        TxSources.Add(new SourceOption("Radio XLR", TxAudioSource.RadioBalancedXlr, HasRadioAudio && radio.HasBalancedXlr));
        SelectedTxSource = TxSources.FirstOrDefault(s => s.Source == radio.Source) ?? TxSources[0];
        MicBoost = radio.MicBoost;
        MicBias = radio.MicBias;
        LineInGain = radio.LineInGain;
    }

    private async Task RefreshAudioStatusAsync(AudioDevices? devices = null)
    {
        devices ??= await _client.GetAudioDevicesAsync();
        var native = await _client.GetNativeAudioStatusAsync();
        var d = native.Diagnostics;
        AudioStatus = AppSettings?.AudioToRadio == true
            ? "Receive audio plays on the radio's speaker jack. Mon can't play while this is chosen."
            : devices.AsioError is { Length: > 0 } asioErr
            ? $"ASIO: {asioErr}" + (_asioCheck is not null && AsioDriverCheck.Applies(asioErr) ? $" {_asioCheck}" : "")
            : devices.Error is { Length: > 0 } err
            ? $"Audio error: {err}"
            : devices.Backend == AsioBackend
            ? $"ASIO {(devices.AsioSampleRate ?? AsioRateHz) / 1000.0:0.#} kHz, {devices.AsioBufferFrames} frames" +
              (devices.AsioInputLatencyFrames is { } inFrames && devices.AsioOutputLatencyFrames is { } outFrames
                  ? $" · latency in {inFrames * 1000.0 / AsioRateHz:0.#} ms, out {outFrames * 1000.0 / AsioRateHz:0.#} ms"
                  : "")
            : $"Output {(native.OutputOpen ? "open" : "closed")}" +
              (d is null ? "" : $", {d.OutputSampleRateHz / 1000.0:0.#} kHz") +
              $" · mic {devices.InputSampleRate / 1000.0:0.#} kHz, {devices.InputChannels} ch";
    }

    // Computer output can only be turned on or off as the engine starts, so
    // moving to or from the radio's jack restarts it. The radio reconnects.
    private async Task<bool> ApplyRadioJackAsync()
    {
        if (AppSettings is not { } app || RestartEngine is null || RadioJackChosen == app.AudioToRadio) return false;
        app.AudioToRadio = RadioJackChosen;
        app.Save();
        string? output = SelectedOutput?.Id;
        AudioStatus = "Switching the output. The radio reconnects in a few seconds…";
        await RestartEngine();
        if (!app.AudioToRadio && output is not null)
            await _client.SetAudioDevicesAsync(SelectedInput?.Id, output, SelectedChannel?.Value ?? -1);
        _devicesWas = CurrentDevices();
        await RefreshAudioStatusAsync();
        AudioSaved?.Invoke();
        return true;
    }

    // Reopening devices interrupts audio, so only touch them when a device or
    // channel actually changed; the radio's codec settings go separately.
    private async Task ApplyAudioAsync()
    {
        if (await ApplyRadioJackAsync()) return;
        var sel = CurrentDevices();
        var asio = CurrentAsio();
        bool changed = false;
        if (HasAsio && (sel != _devicesWas || asio != _asioWas))
        {
            await ApplyRouteAsync(sel, asio);
            changed = true;
        }
        else if (sel != _devicesWas)
        {
            await _client.SetAudioDevicesAsync(sel.InputId, sel.OutputId, sel.Channel);
            _devicesWas = sel;
            changed = true;
        }
        if (_radioAudio is { HasOnboardCodec: true } was && SelectedTxSource is { } src)
        {
            var req = new AudioFrontEndSetRequest(src.Source, MicBoost, MicBias, (int)Math.Round(LineInGain));
            if (req.Source != was.Source || req.MicBoost != was.MicBoost
                || req.MicBias != was.MicBias || req.LineInGain != was.LineInGain)
            {
                await _client.SetRadioAudioAsync(req);
                _radioAudio = was with
                {
                    Source = req.Source, MicBoost = req.MicBoost, MicBias = req.MicBias, LineInGain = req.LineInGain,
                };
                changed = true;
            }
        }
        if (!changed) return;
        await RefreshAudioStatusAsync();
        AudioSaved?.Invoke();
    }
}
