using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Engine;
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

public sealed record SourceOption(string Label, TxAudioSource Source)
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

    private void PopulateAudio(AudioDevices devices, AudioFrontEndDto radio)
    {
        Outputs.Clear();
        Outputs.Add(new DeviceOption("System default", null));
        foreach (var d in devices.Outputs.DistinctBy(d => d.Id)) Outputs.Add(new DeviceOption(d.Name, d.Id));
        SelectedOutput = Outputs.FirstOrDefault(o => o.Id == devices.OutputDeviceId) ?? Outputs[0];

        Inputs.Clear();
        Inputs.Add(new DeviceOption("System default", null));
        foreach (var d in devices.Inputs.DistinctBy(d => d.Id)) Inputs.Add(new DeviceOption(d.Name, d.Id));
        SelectedInput = Inputs.FirstOrDefault(i => i.Id == devices.InputDeviceId) ?? Inputs[0];

        Channels.Clear();
        Channels.Add(new ChannelOption("Mix all channels", -1));
        for (int c = 0; c < devices.InputChannels; c++) Channels.Add(new ChannelOption($"Channel {c + 1}", c));
        SelectedChannel = Channels.FirstOrDefault(c => c.Value == (devices.InputChannel ?? -1)) ?? Channels[0];
        _devicesWas = new DeviceSelection(SelectedInput.Id, SelectedOutput.Id, SelectedChannel.Value);

        _radioAudio = radio;
        HasRadioAudio = radio.HasOnboardCodec;
        HasMicBias = radio.HasMicBias;
        HasLineIn = radio.HasRadioLineIn;
        TxSources.Clear();
        TxSources.Add(new SourceOption("Computer microphone", TxAudioSource.Host));
        if (HasRadioAudio)
        {
            TxSources.Add(new SourceOption("Radio mic jack", TxAudioSource.RadioMic));
            if (HasLineIn) TxSources.Add(new SourceOption("Radio line in", TxAudioSource.RadioLineIn));
            if (radio.HasBalancedXlr) TxSources.Add(new SourceOption("Radio XLR", TxAudioSource.RadioBalancedXlr));
        }
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
        AudioStatus = devices.Error is { Length: > 0 } err
            ? $"Audio error: {err}"
            : $"Output {(native.OutputOpen ? "open" : "closed")}" +
              (d is null ? "" : $", {d.OutputSampleRateHz / 1000.0:0.#} kHz") +
              $" · mic {devices.InputSampleRate / 1000.0:0.#} kHz, {devices.InputChannels} ch";
    }

    // Reopening devices interrupts audio, so only touch them when a device or
    // channel actually changed; the radio's codec settings go separately.
    private async Task ApplyAudioAsync()
    {
        var sel = new DeviceSelection(SelectedInput?.Id, SelectedOutput?.Id, SelectedChannel?.Value ?? -1);
        bool changed = false;
        if (sel != _devicesWas)
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
