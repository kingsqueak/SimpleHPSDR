using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.DeepCw;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// The CW window's decoder choice. Engine is the station-engine's own decoder: instant,
/// best on clean signals. DeepCW is a neural network (see DeepCw/README.md) that copies
/// weak, fading and hand-sent CW far better but prints 2-4 s behind. DeepCW takes the
/// active slice's audio from the engine's TCI server, so that has to be on. The engine
/// decoder keeps running underneath for the WPM and pitch readout.
/// </summary>
public sealed partial class MainViewModel
{
    public static string[] CwDecoderNames { get; } = ["Engine", "DeepCW"];

    [ObservableProperty] private int _cwDecoderIndex;
    private bool DeepCwSelected => CwDecoderIndex == 1;

    private DeepCwDecoder? _deepCw;
    private TciAudio? _deepCwAudio;
    private int _deepCwRx = -1;
    private int _deepCwGeneration;
    private string _deepCwNote = "";

    private void LoadCwDecoderChoice() =>
        CwDecoderIndex = _settings.CwDecoder == "DeepCW" ? 1 : 0;

    partial void OnCwDecoderIndexChanged(int value)
    {
        _settings.CwDecoder = CwDecoderNames[Math.Clamp(value, 0, 1)];
        _settings.Save();
        _deepCwNote = "";
        UpdateDeepCw();
    }

    partial void OnIsConnectedChanged(bool value) => UpdateDeepCw();
    partial void OnActiveRxChanged(int value) => UpdateDeepCw();

    /// <summary>Start, move or stop DeepCW to match the window: on, connected, DeepCW chosen.</summary>
    private void UpdateDeepCw()
    {
        bool want = CwDecoderOn && IsConnected && DeepCwSelected;
        if (want && _deepCw is not null && _deepCwRx == ActiveRx) return;
        StopDeepCw();
        if (want) _ = StartDeepCwAsync(++_deepCwGeneration, ActiveRx);
        else ShowCwDecoderInfo();
    }

    private async Task StartDeepCwAsync(int generation, int rx)
    {
        _deepCwNote = "DeepCW starting…";
        ShowCwDecoderInfo();
        TciStatus_? tci = null;
        try
        {
            var s = await _client!.GetTciStatusAsync();
            tci = new TciStatus_(s.CurrentlyEnabled, s.CurrentPort, s.CurrentBindAddress);
        }
        catch (Exception) { }
        if (generation != _deepCwGeneration) return;
        if (tci is not { Enabled: true })
        {
            _deepCwNote = "DeepCW needs the TCI server: turn it on in Config > CAT & TCI.";
            ShowCwDecoderInfo();
            return;
        }
        var decoder = await Task.Run(() => DeepCwDecoder.TryCreate(out var error) is { } d ? (d, "") : (null, error));
        if (generation != _deepCwGeneration)
        {
            decoder.Item1?.Dispose();
            return;
        }
        if (decoder.Item1 is not { } deepCw)
        {
            _deepCwNote = decoder.Item2;
            ShowCwDecoderInfo();
            return;
        }
        string host = tci.Bind is null or "" or "0.0.0.0" or "::" or "*" ? "127.0.0.1" : tci.Bind;
        var audio = new TciAudio(host, tci.Port, rx);
        audio.AudioReceived += (mono, rate) =>
        {
            if (rate == 48000) deepCw.Push(mono);
        };
        audio.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (generation != _deepCwGeneration) return;
            _deepCwNote = audio.Connected ? "" : $"DeepCW: no TCI audio{(audio.Error.Length > 0 ? $" ({audio.Error})" : "")}";
            ShowCwDecoderInfo();
        });
        deepCw.Decoded += text => Dispatcher.UIThread.Post(() =>
        {
            if (generation == _deepCwGeneration) AppendCwDecoded(text);
        });
        deepCw.Failed += error => Dispatcher.UIThread.Post(() =>
        {
            if (generation != _deepCwGeneration) return;
            _deepCwNote = error;
            ShowCwDecoderInfo();
        });
        (_deepCw, _deepCwAudio, _deepCwRx) = (deepCw, audio, rx);
        _deepCwNote = "";
        ShowCwDecoderInfo();
    }

    private void StopDeepCw()
    {
        ++_deepCwGeneration;
        var (audio, decoder) = (_deepCwAudio, _deepCw);
        (_deepCwAudio, _deepCw, _deepCwRx) = (null, null, -1);
        if (audio is null && decoder is null) return;
        _ = Task.Run(() =>
        {
            audio?.Dispose();
            decoder?.Dispose();
        });
    }

    private void AppendCwDecoded(string text)
    {
        var all = CwDecodedText + text;
        CwDecodedText = all.Length > MaxDecodedChars ? all[^MaxDecodedChars..] : all;
    }

    private int _cwInfoWpm, _cwInfoPitch;

    /// <summary>The line beside Decode: speed and pitch from the engine's decoder, and
    /// DeepCW's state when it's chosen.</summary>
    private void ShowCwDecoderInfo()
    {
        string speed = _cwInfoWpm > 0 ? $"{_cwInfoWpm} WPM" + (_cwInfoPitch > 0 ? $", {_cwInfoPitch} Hz" : "") : "";
        if (!DeepCwSelected || !CwDecoderOn) { CwDecodedInfo = speed; return; }
        CwDecodedInfo = _deepCwNote.Length > 0 ? _deepCwNote : speed.Length > 0 ? $"DeepCW, {speed}" : "DeepCW";
    }

    private sealed record TciStatus_(bool Enabled, int Port, string? Bind);
}
