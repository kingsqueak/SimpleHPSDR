using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zeus.Contracts;

namespace SimpleHPSDR.Engine;

public sealed record FilterPreset(string SlotName, string Label, int LowHz, int HighHz, bool IsCustomized = false);
public sealed record AudioDevice(string Id, string Name, bool IsDefault);
public sealed record MonitorState(bool Enabled, bool MonitorOnTransmit, double VolumeDb);
public sealed record AudioDevices(
    string? InputDeviceId, string? OutputDeviceId, AudioDevice[] Inputs, AudioDevice[] Outputs,
    int InputChannels, int? InputChannel, int InputSampleRate, bool OutputOpen, string? Error);
public sealed record NativeAudioStatus(bool OutputOpen, bool Muted, NativeAudioDiagnostics? Diagnostics);
public sealed record NativeAudioDiagnostics(long UnderrunSamplesTotal, int OutputSampleRateHz, int RingDepthSamples);

/// <summary>A connect attempt the engine refused, with its reason.</summary>
public sealed class StationException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// HTTP control and the /ws stream of station protocol v1. Stream events are
/// raised on the receive thread; callers marshal to the UI themselves.
/// </summary>
public sealed class StationClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;
    private readonly Uri _wsUri;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _wsCts;

    public event Action<DisplayFrame>? Display;
    /// <summary>Receiver index and its signal (dBm).</summary>
    public event Action<int, float>? SignalDbm;
    public event Action<VfoStateFrame>? Vfo;
    public event Action<MoxStateFrame>? MoxState;
    public event Action<TxMetersV2Frame>? TxMeters;
    public event Action<float>? MicPeakDbfs;
    public event Action<PsMetersFrame>? PsMeters;
    public event Action<string>? StreamClosed;

    public StationClient(Uri baseUri)
    {
        _http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(10) };
        _wsUri = new Uri($"ws://{baseUri.Authority}/ws");
    }

    // ---- HTTP ----------------------------------------------------------

    public async Task<RadioInfo[]> DiscoverAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<RadioInfo[]>("api/radios", Json, ct) ?? [];

    public async Task<StateDto> GetStateAsync(CancellationToken ct = default) =>
        (await _http.GetFromJsonAsync<StateDto>("api/state", Json, ct))!;

    public async Task ConnectAsync(string ip, bool protocol2, byte? boardId, int sampleRate, CancellationToken ct)
    {
        var req = new ConnectRequest(ip, sampleRate, BoardId: boardId);
        await PostAsync(protocol2 ? "api/connect/p2" : "api/connect", req, ct);
    }

    /// <summary>Take a radio that another controller owns. For Protocol 2 the
    /// engine also connects; for Protocol 1 it only frees the radio.</summary>
    public Task ReclaimAsync(string ip, bool protocol2, byte? boardId, int sampleRate, CancellationToken ct) =>
        PostAsync("api/radios/reclaim",
            new { endpoint = ip, protocol = protocol2 ? "P2" : "P1", sampleRate, boardId }, ct);

    public Task DisconnectAsync(bool protocol2) =>
        PostAsync(protocol2 ? "api/disconnect/p2" : "api/disconnect", new { });

    /// <summary>Ignore the region band plan for transmit. The engine keeps its
    /// equipment, fault and ownership checks either way.</summary>
    public Task SetBandPlanGuardIgnoredAsync(bool ignore) =>
        PostAsync("api/bands/guard", new BandGuardSetRequest(ignore));

    // ---- transmit ----
    public Task SetMoxAsync(bool on) => PostAsync("api/tx/mox", new MoxSetRequest(on));
    public Task SetTunAsync(bool on) => PostAsync("api/tx/tun", new TunSetRequest(on));
    public Task SetDriveAsync(int percent) => PostAsync("api/tx/drive", new DriveSetRequest(percent));
    public Task SetTuneDriveAsync(int percent) => PostAsync("api/tx/tune-drive", new TuneDriveSetRequest(percent));
    public Task SetMicGainAsync(int db) => PostAsync("api/mic-gain", new MicGainSetRequest(db));
    public Task SetTwoToneAsync(bool on) => PostAsync("api/tx/twotone", new TwoToneSetRequest(on));

    // ---- TX audio chain ----
    /// <summary>Set the TX passband from positive low/high cuts. The engine
    /// stores the signed pair as given, so lower-sideband modes must be sent
    /// as (-high, -low) or the filter lands on the wrong sideband.</summary>
    public Task SetTxFilterAsync(RxMode mode, int lowCutHz, int highCutHz)
    {
        bool lowerSideband = mode is RxMode.LSB or RxMode.DIGL or RxMode.CWL;
        return PostAsync("api/tx-filter", lowerSideband
            ? new TxFilterSetRequest(-highCutHz, -lowCutHz)
            : new TxFilterSetRequest(lowCutHz, highCutHz));
    }
    /// <summary>Hold the radio keyed this long after PTT release (any source)
    /// while the audio still in the TX pipeline is flushed out.</summary>
    public Task SetTxTailDelayAsync(int ms) => PostAsync("api/tx/tail-delay", new TxTailDelaySetRequest(ms));
    public Task SetTxLevelingAsync(TxLevelingConfig cfg) => PostAsync("api/tx/leveling", new TxLevelingSetRequest(cfg));
    public Task SetLevelerMaxGainAsync(double db) => PostAsync("api/tx/leveler-max-gain", new LevelerMaxGainSetRequest(db));
    public Task SetTxEqAsync(LegacyTxEqConfig cfg) => PostAsync("api/tx/legacy-eq", cfg);
    public Task SetCfcAsync(CfcConfig cfg) => PostAsync("api/tx/cfc", new CfcSetRequest(cfg));
    public Task SetDexpAsync(DexpConfig cfg) => PostAsync("api/tx/dexp", new DexpSetRequest(cfg));
    public Task SetPhaseRotatorAsync(TxPhaseRotatorConfig cfg) =>
        PostAsync("api/tx/phase-rotator", new TxPhaseRotatorSetRequest(cfg));

    // ---- TX monitor ----
    // The engine demodulates the post-TXA signal (after EQ, leveler,
    // compressor/CFC, ALC and TX filter) back to audio on the native output.
    // "Enabled" is the preview while receiving; MonitorOnTransmit is while keyed.
    public async Task<MonitorState> GetMonitorAsync() =>
        (await _http.GetFromJsonAsync<MonitorState>("api/tx-audio-suite/preview", Json))!;

    public async Task SetMonitorAsync(bool preview, bool onTransmit)
    {
        using var r = await _http.PutAsJsonAsync("api/tx-audio-suite/preview",
            new { enabled = preview, monitorOnTransmit = onTransmit }, Json);
        await ThrowIfFailed(r);
    }

    public async Task SetMonitorVolumeAsync(double db)
    {
        using var r = await _http.PutAsJsonAsync("api/tx-audio-suite/monitor-volume", new { volumeDb = db }, Json);
        await ThrowIfFailed(r);
    }

    // ---- PureSignal ----
    public Task SetPsAsync(bool enabled, bool auto = true, bool single = false) =>
        PostAsync("api/tx/ps", new PsControlSetRequest(enabled, auto, single));
    public Task SetPsAdvancedAsync(PsAdvancedSetRequest req) => PostAsync("api/tx/ps/advanced", req);
    public Task SetPsFeedbackSourceAsync(PsFeedbackSource source) =>
        PostAsync("api/tx/ps/feedback-source", new PsFeedbackSourceSetRequest(source));
    public Task SetPsFeedbackAttenuationAsync(int db) =>
        PostAsync("api/tx/ps/feedback-attenuation", new PsFeedbackAttenuationSetRequest(db));
    public Task SetPsMonitorAsync(bool on) => PostAsync("api/tx/ps/monitor", new PsMonitorSetRequest(on));
    public Task ResetPsAsync() => PostAsync("api/tx/ps/reset", new { });

    public async Task<AntennaSettingsDto> GetAntennasAsync() =>
        (await _http.GetFromJsonAsync<AntennaSettingsDto>("api/radio/antenna", Json))!;

    public async Task SetAntennaAsync(AntennaSetRequest req)
    {
        using var r = await _http.PutAsJsonAsync("api/radio/antenna", req, Json);
        await ThrowIfFailed(r);
    }

    // ---- TCI and CAT servers (TCP ones apply on engine restart) ----
    public async Task<TciStatus> GetTciStatusAsync() =>
        (await _http.GetFromJsonAsync<TciStatus>("api/tci/status", Json))!;
    public async Task<TciStatus> SetTciConfigAsync(TciRuntimeConfig cfg) =>
        await PostForAsync<TciStatus>("api/tci/config", cfg);
    public async Task<TciTestResult> TestTciPortAsync(string bind, int port) =>
        await PostForAsync<TciTestResult>("api/tci/test", new TciTestRequest(bind, port));

    public async Task<CatStatus> GetCatStatusAsync() =>
        (await _http.GetFromJsonAsync<CatStatus>("api/cat/status", Json))!;
    public async Task<CatStatus> SetCatConfigAsync(CatRuntimeConfig cfg) =>
        await PostForAsync<CatStatus>("api/cat/config", cfg);
    public async Task<CatTestResult> TestCatPortAsync(string bind, int port) =>
        await PostForAsync<CatTestResult>("api/cat/test", new CatTestRequest(bind, port));

    public async Task<CatSerialStatus> GetCatSerialStatusAsync() =>
        (await _http.GetFromJsonAsync<CatSerialStatus>("api/cat/serial/status", Json))!;
    public async Task SetCatSerialConfigAsync(CatSerialConfig cfg)
    {
        using var r = await _http.PutAsJsonAsync("api/cat/serial/config", cfg, Json);
        await ThrowIfFailed(r);
    }

    private async Task<T> PostForAsync<T>(string path, object body)
    {
        using var r = await _http.PostAsJsonAsync(path, body, Json);
        await ThrowIfFailed(r);
        return (await r.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public async Task<PaSettingsDto> GetPaSettingsAsync() =>
        (await _http.GetFromJsonAsync<PaSettingsDto>("api/pa-settings", Json))!;

    public async Task SetPaSettingsAsync(PaSettingsSetRequest req)
    {
        using var r = await _http.PutAsJsonAsync("api/pa-settings", req, Json);
        await ThrowIfFailed(r);
    }

    public async Task<AudioDevices> GetAudioDevicesAsync() =>
        (await _http.GetFromJsonAsync<AudioDevices>("api/audio/devices", Json))!;

    /// <summary>Choose the host (computer) mic. The engine sets input and
    /// output together, so the current output is passed back unchanged.</summary>
    public async Task SetHostInputAsync(string? inputId)
    {
        var current = await GetAudioDevicesAsync();
        await SetAudioDevicesAsync(inputId, current.OutputDeviceId, null);
    }

    /// <summary>Null device ids mean the system default; a null channel keeps
    /// the current one, -1 mixes all channels, 0.. picks one.</summary>
    public async Task SetAudioDevicesAsync(string? inputId, string? outputId, int? inputChannel)
    {
        using var r = await _http.PutAsJsonAsync("api/audio/devices",
            new { inputDeviceId = inputId, outputDeviceId = outputId, inputChannel }, Json);
        await ThrowIfFailed(r);
    }

    public async Task<NativeAudioStatus> GetNativeAudioStatusAsync() =>
        (await _http.GetFromJsonAsync<NativeAudioStatus>("api/audio/native", Json))!;

    public async Task<AudioFrontEndDto> GetRadioAudioAsync() =>
        (await _http.GetFromJsonAsync<AudioFrontEndDto>("api/radio/audio", Json))!;

    public async Task SetRadioAudioAsync(AudioFrontEndSetRequest req)
    {
        using var r = await _http.PutAsJsonAsync("api/radio/audio", req, Json);
        await ThrowIfFailed(r);
    }

    // Receiver 0 is RX1 (slice A), 1 is RX2 (slice B).
    public Task SetVfoAsync(long hz, int rx = 0) => PostAsync("api/vfo", new VfoSetRequest(hz, rx));
    public Task SetModeAsync(RxMode mode, int rx = 0) => PostAsync("api/mode", new ModeSetRequest(mode, rx));
    public Task SetReceiverAsync(int rx, ReceiverSetRequest req) => PostAsync($"api/receivers/{rx}", req);
    public Task SetReceiverMuteAsync(int rx, bool muted) => PostAsync($"api/receivers/{rx}/mute", new MuteSetRequest(muted));
    /// <summary>Which receiver transmits: TX uses its frequency, mode and filter.</summary>
    public Task SetTxReceiverAsync(int rx) => PostAsync("api/tx/receiver", new TxReceiverSetRequest(rx));
    /// <summary>AGC-T: the AGC's maximum gain. The engine takes this as manual
    /// control, so it turns auto AGC off.</summary>
    public Task SetAgcGainAsync(double topDb) => PostAsync("api/agcGain", new AgcGainSetRequest(topDb));
    /// <summary>AGC mode (speed) for RX1; slice B takes its own via SetReceiverAsync.</summary>
    public Task SetAgcAsync(AgcConfig agc) => PostAsync("api/rx/agc", new AgcSetRequest(agc));
    /// <summary>Auto AGC: the engine adjusts the AGC gain to conditions.</summary>
    public Task SetAutoAgcAsync(bool on) => PostAsync("api/auto-agc", new AutoAgcSetRequest(on));
    /// <summary>Move a receiver's display centre without moving its dial.</summary>
    public Task SetReceiverLoAsync(int rx, long hz) => PostAsync($"api/receivers/{rx}/lo", new RadioLoSetRequest(hz));
    public sealed record SMeterTrim(double OffsetDb);
    /// <summary>The operator's S-meter trim, on top of the board's calibration.
    /// The engine keeps it per radio model.</summary>
    public async Task<double> GetSMeterTrimAsync() =>
        (await _http.GetFromJsonAsync<SMeterTrim>("api/radio/smeter-calibration", Json))!.OffsetDb;
    public Task SetSMeterTrimAsync(double db) => PostAsync("api/radio/smeter-calibration", new { offsetDb = db });
    public Task SetRitAsync(bool? on = null, long? hz = null) => PostAsync("api/rx/rit", new RitSetRequest(on, hz));
    public Task SetXitAsync(bool? on = null, long? hz = null) => PostAsync("api/tx/xit", new XitSetRequest(on, hz));
    public Task SetVfoLockAsync(bool locked) => PostAsync("api/radio/vfo-lock", new VfoLockSetRequest(locked));
    public Task SwapVfosAsync() => PostAsync("api/vfo/swap", new { });
    public Task SetSquelchAsync(SquelchConfig squelch) => PostAsync("api/rx/squelch", new SquelchSetRequest(squelch));
    public Task SetRxLevelerAsync(bool on) => PostAsync("api/rx/leveler", new RxLevelerSetRequest(on));
    public async Task<List<NotchDto>> GetNotchesAsync() =>
        (await _http.GetFromJsonAsync<List<NotchDto>>("api/rx/notches", Json))!;
    /// <summary>The whole notch list. Centres are RF frequencies in Hz.</summary>
    public Task SetNotchesAsync(IReadOnlyList<NotchDto> notches) => PostAsync("api/rx/notches", new NotchListRequest(notches));
    public async Task<List<BandStackEntryDto>> GetBandStackAsync() =>
        (await _http.GetFromJsonAsync<List<BandStackEntryDto>>("api/bands/stack", Json))!;
    public Task AddBandStackAsync(string band, BandStackAddRequest entry) =>
        PostAsync($"api/bands/stack/{Uri.EscapeDataString(band)}", entry);
    public async Task RemoveBandStackAsync(string band, int id) =>
        (await _http.DeleteAsync($"api/bands/stack/{Uri.EscapeDataString(band)}/{id}")).EnsureSuccessStatusCode();
    /// <summary>DUP: keep the receivers in the audio mix while transmitting.</summary>
    public Task SetFullDuplexAsync(bool on) => PostAsync("api/radio/full-duplex-multi-rx", new FullDuplexMultiRxSetRequest(on));
    public Task SetFilterAsync(int low, int high, string? preset, int rx = 0) =>
        PostAsync("api/filter", new FilterSetRequest(low, high, preset, rx));
    public Task SetAfGainAsync(double db) => PostAsync("api/rx/afGain", new RxAfGainSetRequest(db));
    public Task SetAttenuatorAsync(int db) => PostAsync("api/attenuator", new AttenuatorSetRequest(db));
    public Task SetAutoAttAsync(bool on) => PostAsync("api/auto-att", new AutoAttSetRequest(on));
    public Task SetNrAsync(NrConfig nr) => PostAsync("api/rx/nr", new NrSetRequest(nr));
    public Task SetPreampAsync(bool on) => PostAsync("api/preamp", new PreampSetRequest(on));
    public Task SetMuteAsync(bool muted) => PostAsync("api/audio/mute", new { muted });
    /// <summary>Receiver sample rate, which is the panadapter span; changes live.</summary>
    public Task SetSampleRateAsync(int hz) => PostAsync("api/sampleRate", new SampleRateSetRequest(hz));
    public sealed record DisplayTuning(int? RxDisplayFftSize, int WaterfallUpdatePeriod);

    public async Task<DisplayTuning> GetDisplayTuningAsync() =>
        (await _http.GetFromJsonAsync<DisplayTuning>("api/display-settings", Json))!;

    /// <summary>Display FFT size (panadapter/waterfall resolution) and the
    /// waterfall row period in display frames. Null leaves a field alone.</summary>
    public async Task SetDisplayTuningAsync(int? fftSize = null, int? waterfallPeriod = null)
    {
        using var r = await _http.PutAsJsonAsync("api/display-settings",
            new { rxDisplayFftSize = fftSize, waterfallUpdatePeriod = waterfallPeriod }, Json);
        await ThrowIfFailed(r);
    }

    public Task SetZoomAsync(int level) => PostAsync("api/rx/zoom", new { level });
    /// <summary>CTUN: panadapter clicks move only the receive frequency; the
    /// radio's centre (NCO) stays, so the display doesn't jump.</summary>
    public Task SetCtunAsync(bool on) => PostAsync("api/radio/ctun", new CtunSetRequest(on));

    public Task WriteFilterPresetAsync(RxMode mode, string slot, int lowHz, int highHz, string label) =>
        PostAsync("api/filter/presets", new FilterPresetWriteRequest(mode, slot, lowHz, highHz, label));

    public Task ResetFilterPresetAsync(RxMode mode, string slot) =>
        PostAsync("api/filter/presets/reset", new FilterPresetResetRequest(mode, slot));

    public async Task<FilterPreset[]> GetFilterPresetsAsync(RxMode mode) =>
        await _http.GetFromJsonAsync<FilterPreset[]>($"api/filter/presets?mode={mode}", Json) ?? [];

    private async Task PostAsync<T>(string path, T body, CancellationToken ct = default)
    {
        using var r = await _http.PostAsJsonAsync(path, body, Json, ct);
        await ThrowIfFailed(r, ct);
    }

    private static async Task ThrowIfFailed(HttpResponseMessage r, CancellationToken ct = default)
    {
        if (r.IsSuccessStatusCode) return;
        var text = await r.Content.ReadAsStringAsync(ct);
        string message = text;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var e)) message = e.GetString() ?? text;
        }
        catch (JsonException) { }
        throw new StationException(message, r.StatusCode);
    }

    // ---- WebSocket stream ---------------------------------------------

    public async Task OpenStreamAsync()
    {
        CloseStream();
        _wsCts = new CancellationTokenSource();
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(_wsUri, _wsCts.Token);
        // 0x22 level 2: this session wants the display stream (preferred).
        // The engine defines the type byte privately in StreamingHub.
        await _ws.SendAsync(new byte[] { 0x22, 2 },
            WebSocketMessageType.Binary, true, _wsCts.Token);
        // 0x21 off: audio plays natively in the engine, so don't stream PCM here.
        await _ws.SendAsync(new byte[] { (byte)MsgType.AudioStreamRequest, 0 },
            WebSocketMessageType.Binary, true, _wsCts.Token);
        _ = Task.Run(() => ReceiveLoop(_ws, _wsCts.Token));
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[256 * 1024];
        string reason = "closed";
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                int len = 0;
                ValueWebSocketReceiveResult r;
                do
                {
                    if (len == buf.Length) Array.Resize(ref buf, buf.Length * 2);
                    r = await ws.ReceiveAsync(buf.AsMemory(len), ct);
                    len += r.Count;
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Close) break;
                if (r.MessageType == WebSocketMessageType.Binary && len > 0)
                    Dispatch(buf.AsSpan(0, len));
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { reason = ex.Message; }
        StreamClosed?.Invoke(reason);
    }

    private void Dispatch(ReadOnlySpan<byte> msg)
    {
        try
        {
            switch ((MsgType)msg[0])
            {
                case MsgType.DisplayFrame:
                    UiStats.FrameReceived();
                    Display?.Invoke(DisplayFrame.Deserialize(msg));
                    break;
                case MsgType.RxMetersV2:
                    var m = RxMetersV2Frame.Deserialize(msg);
                    SignalDbm?.Invoke(m.RxId, m.SignalAv);
                    break;
                case MsgType.VfoState:
                    Vfo?.Invoke(VfoStateFrame.Deserialize(msg));
                    break;
                case MsgType.MoxState:
                    MoxState?.Invoke(MoxStateFrame.Deserialize(msg));
                    break;
                case MsgType.TxMetersV2:
                    TxMeters?.Invoke(TxMetersV2Frame.Deserialize(msg));
                    break;
                case MsgType.PsMeters:
                    PsMeters?.Invoke(PsMetersFrame.Deserialize(msg));
                    break;
                case MsgType.MicPeak:
                    MicPeakDbfs?.Invoke(MicPeakFrame.Deserialize(msg).PeakDbfs);
                    break;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IndexOutOfRangeException)
        {
            // A frame shape we don't understand; skip it.
        }
    }

    private void CloseStream()
    {
        _wsCts?.Cancel();
        _ws?.Abort();
        _ws?.Dispose();
        _ws = null;
        _wsCts = null;
    }

    public void Dispose()
    {
        CloseStream();
        _http.Dispose();
    }
}
