using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Engine;

public sealed record FilterPreset(string SlotName, string Label, int LowHz, int HighHz, bool IsCustomized = false);
public sealed record AudioDevice(string Id, string Name, bool IsDefault);
public sealed record MonitorState(bool Enabled, bool MonitorOnTransmit, double VolumeDb);
public sealed record AsioDriver(string Id, string Name);
public sealed record AsioChannel(int Index, string Name);
public sealed record AudioDevices(
    string? InputDeviceId, string? OutputDeviceId, AudioDevice[] Inputs, AudioDevice[] Outputs,
    int InputChannels, int? InputChannel, int InputSampleRate, bool OutputOpen, string? Error,
    string Backend = "system", string[]? AvailableBackends = null, AsioDriver[]? AsioDrivers = null,
    string? AsioDriverId = null, AsioChannel[]? AsioInputs = null, AsioChannel[]? AsioOutputs = null,
    int AsioInputWidth = 1, int AsioInputChannel = 0, int AsioOutputChannel = 0,
    int? AsioSampleRate = null, int? AsioBufferFrames = null,
    int? AsioInputLatencyFrames = null, int? AsioOutputLatencyFrames = null, string? AsioError = null,
    uint? AsioBufferMinFrames = null, uint? AsioBufferMaxFrames = null, uint? AsioBufferPreferredFrames = null,
    int? AsioBufferGranularity = null, int AsioRequestedBufferFrames = 0);
/// <summary>Null ASIO fields keep what the engine has.</summary>
public sealed record AudioRouteRequest(
    string? InputDeviceId, string? OutputDeviceId, int? InputChannel, string Backend, string? AsioDriverId,
    int? AsioInputChannel, int? AsioInputWidth, int? AsioOutputChannel, int? AsioBufferFrames);
public sealed record NativeAudioStatus(bool OutputOpen, bool Muted, NativeAudioDiagnostics? Diagnostics);
public sealed record NativeAudioDiagnostics(long UnderrunSamplesTotal, int OutputSampleRateHz, int RingDepthSamples);

/// <summary>A connect attempt the engine refused, with its reason.</summary>
public sealed record PaCalBandFailure(string Band, string Reason);

/// <summary>The engine's PA calibration: State is idle, running, cancelling,
/// completed, partial, cancelled or failed.</summary>
public sealed record PaCalStatus(string State, string? Band, double? TargetWatts, float? MeasuredWatts,
    int CompletedSteps, int TotalSteps, string? Message, List<string>? PassedBands = null,
    List<PaCalBandFailure>? FailedBands = null, bool PendingSave = false);

/// <summary>The engine's FFTW planning: phase Idle, Building, Ready or Failed.</summary>
public sealed record DspPrep(string Phase, string Status)
{
    public bool Ready => Phase == "Ready";
    public bool Failed => Phase == "Failed";
    public bool Building => !Ready && !Failed;
}

public sealed record FirewallStatus(bool Supported, bool CanApply, bool? RulePresent, bool? RuleMatchesProgram)
{
    /// <summary>No rule yet, so adding one is safe.</summary>
    public bool Missing => Supported && CanApply && RulePresent != true;

    /// <summary>The rule names another program. The engine has one rule name and
    /// replaces it, which would take it from that program, so this one is left to the user.</summary>
    public bool OwnedElsewhere => Supported && RulePresent == true && RuleMatchesProgram == false;
}

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
    public event Action<AlertFrame>? Alert;
    public event Action<CwEngineStatusFrame>? CwStatus;
    public event Action<CwDecodedTextFrame>? CwDecoded;
    public event Action<CwSettingsDto>? CwSettings;
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
    /// <summary>The two-tone test signal on or off (it transmits when keyed).</summary>
    public Task SetTwoToneAsync(bool on) => PostAsync("api/tx/twotone", new TwoToneSetRequest(on));

    /// <summary>The engine's own settings backup (a ZIP; PureSignal calibration left out).</summary>
    public async Task<byte[]> ExportEngineBackupAsync()
    {
        using var r = await _http.GetAsync("api/prefs/databases/export");
        await ThrowIfFailed(r);
        return await r.Content.ReadAsByteArrayAsync();
    }

    /// <summary>Stage an engine backup; the engine swaps it in when it next starts.</summary>
    public Task ImportEngineBackupAsync(string path) =>
        PostAsync("api/prefs/databases/import", new { sourcePath = path });

    /// <summary>The engine's two-tone IMD calculator (ported from Thetis): finds
    /// the tones and their 3rd/5th-order products in a spectrum we send it.</summary>
    public Task<ImdReading> MeasureImdAsync(ImdRequest request) =>
        PostForAsync<ImdReading>("api/dsp/imd-measure", request);

    /// <summary>Our engine fork: the PureSignal feedback (coupler) spectrum in
    /// the panadapter's width and span, or null when PS isn't armed.</summary>
    public async Task<float[]?> GetPsFeedbackSpectrumAsync()
    {
        using var r = await _http.GetAsync("api/tx/ps/feedback-spectrum");
        if (r.StatusCode == System.Net.HttpStatusCode.NoContent || !r.IsSuccessStatusCode) return null;
        return (await r.Content.ReadFromJsonAsync<PsSpectrum>(Json))?.Db;
    }

    private sealed record PsSpectrum(float[] Db);

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
    public Task SetRxEqAsync(RxEqConfig cfg) => PostAsync("api/rx/eq", cfg);

    // ---- recorder (engine AudioRecorderService) ----
    public async Task<RecorderStatus> GetRecorderAsync() =>
        (await _http.GetFromJsonAsync<RecorderStatus>("api/recorder", Json))!;
    public Task<RecorderStatus> StartRecordingAsync(string file, bool mic, int receiver) =>
        PostForAsync<RecorderStatus>("api/recorder/record", new RecorderRecordRequest(file, mic ? "mic" : "rx", receiver));
    public Task<RecorderStatus> StopRecordingAsync() => PostForAsync<RecorderStatus>("api/recorder/record/stop", new { });
    public Task<RecorderStatus> PlayRecordingAsync(string file, double gainDb, bool processed, bool local) =>
        PostForAsync<RecorderStatus>("api/recorder/play", new RecorderPlayRequest(file, gainDb, processed, local));
    public Task<RecorderStatus> StopPlaybackAsync() => PostForAsync<RecorderStatus>("api/recorder/play/stop", new { });
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
    // Your callsign (and grid), kept by the engine: sent by RADE at the end of an over.
    public sealed record OperatorIdentity(string Callsign, string Grid);
    public async Task<OperatorIdentity> GetOperatorAsync() =>
        (await _http.GetFromJsonAsync<OperatorIdentity>("api/operator", Json))!;
    public Task SetOperatorAsync(string callsign, string grid) =>
        PostAsync("api/operator", new OperatorIdentity(callsign, grid));

    // FreeDV RADE V1, hosted in the engine. Mode FreeDV + RADE selected = on air.
    public sealed record RadeStatus(bool Available, bool Active, bool Synced, double SnrDb, string? RxText,
        string? LibraryVersion, string Rid, double TxMicLevelDb, bool TxMicClip);
    public sealed record RadeSelect(bool Active, string? TxText);
    public sealed record RadeMic(bool? AgcEnabled, double? AgcTargetDb, bool? EqEnabled, double? EqBassDb,
        double? EqMidDb, double? EqTrebleDb);
    public async Task<RadeStatus> GetRadeAsync() =>
        (await _http.GetFromJsonAsync<RadeStatus>("api/station/rade/status", Json))!;
    public Task<RadeStatus> SelectRadeAsync(bool active, string? callsign) =>
        PostForAsync<RadeStatus>("api/station/rade/select", new RadeSelect(active, callsign));
    public Task<RadeStatus> SetRadeMicAsync(RadeMic mic) =>
        PostForAsync<RadeStatus>("api/station/rade/mic", mic);
    public sealed record RadeTuning(bool RxSquelch, double RxHoldMs, double RxMinSnrDb, bool MicGate,
        double MicGateOpenDb, double MicGateHangMs, double TxLevelDb);
    public Task<RadeTuning> SetRadeTuningAsync(RadeTuning tuning) =>
        PostForAsync<RadeTuning>("api/station/rade/tuning", tuning);

    // ---- antenna analyser (engine VnaService): the radio's own bridge
    // (SWR under ~5 W TUNE), an HL2's FPGA VNA, or a NanoVNA on a serial port.
    public sealed record VnaCapability(bool Available, bool NativeVector, string Board, string Source, string Reason,
        bool RequiresExternalBridge, bool RequiresCalibration, int MaximumPoints);
    public sealed record VnaSource(string Source, string? DeviceId, string? DeviceName, bool Connected, string? Error,
        List<string> Devices);
    public sealed record VnaStatus(bool Running, string? Phase, int PointsReceived, int PointsExpected,
        string? ActiveAntenna, string? ActiveBand, string? Error);
    public sealed record VnaPoint(long FrequencyHz, double RawReal, double RawImaginary, double MagnitudeDb, double PhaseDeg,
        double? Swr, double? ReturnLossDb, double? ResistanceOhms, double? ReactanceOhms);
    public sealed record VnaMetrics(long ResonantFrequencyHz, double? MinimumSwr, double? MaximumReturnLossDb,
        double? ResistanceAtResonanceOhms, double? ReactanceAtResonanceOhms, long? Bandwidth15Hz, long? Bandwidth20Hz,
        long? Bandwidth30Hz, double? EstimatedQ);
    public sealed record VnaSweep(string Id, DateTimeOffset CapturedUtc, string RadioKey, string Board, string Antenna,
        string Band, string Label, long StartHz, long EndHz, int PointCount, string Kind, string? CalibrationId,
        bool Calibrated, VnaMetrics Metrics, List<VnaPoint> Points);
    public sealed record VnaCalibration(string Id, string Name, DateTimeOffset UpdatedUtc, string RadioKey, string Antenna,
        string Band, long StartHz, long EndHz, int PointCount, bool HasThru, bool HasOpen, bool HasShort, bool HasLoad,
        bool ReflectionReady, bool TransmissionReady);
    private sealed record VnaSweepRequest(string Antenna, string Band, long StartHz, long EndHz, int Points, int Kind,
        string? CalibrationId, string? Label, int DriveLevel = 8, bool FixedRxGainHigh = false);
    private sealed record VnaCalibrationRequest(string Name, string Antenna, string Band, long StartHz, long EndHz,
        int Points, int Standard, string? CalibrationId, int DriveLevel = 8, bool FixedRxGainHigh = false);

    public async Task<VnaCapability> GetVnaCapabilityAsync() =>
        (await _http.GetFromJsonAsync<VnaCapability>("api/vna/capability", Json))!;
    public async Task<VnaSource> GetVnaSourceAsync() =>
        (await _http.GetFromJsonAsync<VnaSource>("api/vna/source", Json))!;
    public Task<VnaSource> SetVnaSourceAsync(string source, string? device) =>
        PostForAsync<VnaSource>("api/vna/source", new { source, deviceId = device });
    public async Task<VnaStatus> GetVnaStatusAsync() =>
        (await _http.GetFromJsonAsync<VnaStatus>("api/vna/status", Json))!;
    public async Task<List<VnaSweep>> GetVnaSweepsAsync() =>
        await _http.GetFromJsonAsync<List<VnaSweep>>("api/vna/sweeps", Json) ?? [];
    public async Task<List<VnaCalibration>> GetVnaCalibrationsAsync() =>
        await _http.GetFromJsonAsync<List<VnaCalibration>>("api/vna/calibrations", Json) ?? [];
    public Task CancelVnaAsync() => PostAsync("api/vna/cancel", new { });
    public Task DeleteVnaSweepAsync(string id) => Checked(_http.DeleteAsync($"api/vna/sweeps/{Uri.EscapeDataString(id)}"));
    public Task DeleteVnaCalibrationAsync(string id) => Checked(_http.DeleteAsync($"api/vna/calibrations/{Uri.EscapeDataString(id)}"));

    /// <summary>Runs the whole sweep; returns when it's done (minutes allowed).</summary>
    public async Task<VnaSweep> SweepVnaAsync(string antenna, string band, long startHz, long endHz, int points,
        string? calibrationId, string? label, CancellationToken ct = default)
    {
        using var http = new HttpClient { BaseAddress = _http.BaseAddress, Timeout = TimeSpan.FromMinutes(10) };
        using var r = await http.PostAsJsonAsync("api/vna/sweeps",
            new VnaSweepRequest(antenna, band, startHz, endHz, points, 2, calibrationId, label), Json, ct);
        await ThrowIfFailed(r, ct);
        return (await r.Content.ReadFromJsonAsync<VnaSweep>(Json, ct))!;
    }

    /// <summary>Standard 0 thru, 1 open, 2 short, 3 load.</summary>
    public async Task<VnaCalibration> CaptureVnaCalibrationAsync(string name, string antenna, string band, long startHz,
        long endHz, int points, int standard, string? calibrationId, CancellationToken ct = default)
    {
        using var http = new HttpClient { BaseAddress = _http.BaseAddress, Timeout = TimeSpan.FromMinutes(10) };
        using var r = await http.PostAsJsonAsync("api/vna/calibrations/capture",
            new VnaCalibrationRequest(name, antenna, band, startHz, endHz, points, standard, calibrationId), Json, ct);
        await ThrowIfFailed(r, ct);
        return (await r.Content.ReadFromJsonAsync<VnaCalibration>(Json, ct))!;
    }

    // Palstar HF-AUTO tuner: the HF-Auto program's UDP status, received by the engine.
    public sealed record HfAutoConfig(bool Enabled, string BindAddress, int Port, string ExpectedSenderAddress);
    public sealed record HfAutoTelemetry(bool? Valid, string? FirmwareVersion, string? Mode, int? ModeCode,
        long? DetectedFrequencyHz, int? Capacitor, int? Inductor, int? Antenna, string? SelectedMotor,
        double? DisplayedPowerWatts, bool? PeakPower, int? PowerRange, double? Swr, bool? HasRf, bool? Stored,
        bool? UnsolicitedMessages, bool? TuneComplete, bool? GoodAutoModePower, bool? InvalidFrequency,
        bool? InvalidInductor, bool? InvalidCapacitor, string? RawBytes);
    public sealed record HfAutoStatus(HfAutoConfig Config, bool ListenerActive, bool Connected, DateTime? LastReceivedUtc,
        string? LastRemoteEndpoint, long PacketsReceived, string? Error, HfAutoTelemetry? Telemetry);
    public async Task<HfAutoStatus> GetHfAutoAsync() =>
        (await _http.GetFromJsonAsync<HfAutoStatus>("api/hf-auto/status", Json))!;
    public Task<HfAutoStatus> SetHfAutoAsync(HfAutoConfig config) =>
        PostForAsync<HfAutoStatus>("api/hf-auto/config", config);

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

    /// <summary>The band plans the engine offers.</summary>
    public async Task<List<BandRegion>> GetBandRegionsAsync() =>
        (await _http.GetFromJsonAsync<List<BandRegion>>("api/bands/regions", Json))!;

    public sealed record CurrentBandPlan(string RegionId, BandRegion Region, List<BandSegment> Segments);
    public async Task<CurrentBandPlan> GetBandPlanAsync() =>
        (await _http.GetFromJsonAsync<CurrentBandPlan>("api/bands/current", Json))!;
    public async Task<string> GetBandRegionAsync() =>
        (await _http.GetFromJsonAsync<CurrentBandPlan>("api/bands/current", Json))!.RegionId;

    public Task SetBandRegionAsync(string regionId) =>
        PostAsync("api/bands/current", new BandPlanCurrentSetRequest(regionId));

    public async Task<CwSettingsDto> GetCwSettingsAsync() =>
        (await _http.GetFromJsonAsync<CwSettingsDto>("api/cw/settings", Json))!;

    public async Task SetCwSettingsAsync(CwSettingsSetRequest patch)
    {
        using var r = await _http.PutAsJsonAsync("api/cw/settings", patch, Json);
        await ThrowIfFailed(r);
    }

    private sealed record CwAbort(int AbortSeq);

    public async Task<int> GetCwAbortSeqAsync() =>
        (await _http.GetFromJsonAsync<CwAbort>("api/cw/status", Json))!.AbortSeq;

    /// <summary>Key this text out. The counter makes a send delayed past Stop refuse to key.</summary>
    public Task SendCwAsync(string text, int abortSeq) =>
        PostAsync("api/cw/send", new CwSendRequest(text, ExpectedAbortSeq: abortSeq));

    /// <summary>Stop sending now and drop the queue. Returns the new counter.</summary>
    public async Task<int> AbortCwAsync()
    {
        using var r = await _http.PostAsync("api/cw/abort", null);
        await ThrowIfFailed(r);
        return (await r.Content.ReadFromJsonAsync<CwAbort>(Json))!.AbortSeq;
    }

    /// <summary>Ask the engine for decoded CW on the stream.</summary>
    public async Task SetCwDecoderAsync(bool on)
    {
        if (_ws is { State: WebSocketState.Open } ws)
            await ws.SendAsync(new byte[] { (byte)MsgType.CwDecoderRequest, (byte)(on ? 1 : 0) },
                WebSocketMessageType.Binary, true, CancellationToken.None);
    }

    /// <summary>Whether this radio has a speaker jack the engine can feed.</summary>
    public async Task<bool> GetRadioSpeakerAvailableAsync() =>
        (await _http.GetFromJsonAsync<RadioSpeakerOutputDto>("api/radio/speaker-output", Json))!.Available;

    public async Task SetRadioSpeakerAsync(bool on)
    {
        using var r = await _http.PutAsJsonAsync("api/radio/speaker-output", new RadioSpeakerOutputSetRequest(on), Json);
        await ThrowIfFailed(r);
    }

    /// <summary>The engine's stock PA table for the connected radio.</summary>
    public async Task<PaSettingsDto> GetPaDefaultsAsync() =>
        (await _http.GetFromJsonAsync<PaSettingsDto>("api/pa-settings/defaults", Json))!;

    public async Task<PaCalStatus> GetPaCalibrationAsync() =>
        (await _http.GetFromJsonAsync<PaCalStatus>("api/pa-settings/calibration", Json))!;

    /// <summary>Starts transmitting on every HF band. The engine refuses with a reason if it isn't safe to.</summary>
    public Task StartPaCalibrationAsync(bool amplifierOffConfirmed) =>
        PostAsync("api/pa-settings/calibration", new { amplifierOffConfirmed });

    public async Task CancelPaCalibrationAsync()
    {
        using var r = await _http.DeleteAsync("api/pa-settings/calibration");
        await ThrowIfFailed(r);
    }

    public Task SavePaCalibrationAsync() => PostAsync("api/pa-settings/calibration/save", new { });
    public Task DiscardPaCalibrationAsync() => PostAsync("api/pa-settings/calibration/discard", new { });

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

    /// <summary>Choose the audio system and its devices; the engine reopens
    /// audio and answers with the result, including the new driver's channels.</summary>
    public async Task<AudioDevices> SetAudioRouteAsync(AudioRouteRequest request)
    {
        using var r = await _http.PutAsJsonAsync("api/audio/devices", request, Json);
        await ThrowIfFailed(r);
        return (await r.Content.ReadFromJsonAsync<AudioDevices>(Json))!;
    }

    /// <summary>Open the active ASIO driver's own settings window.</summary>
    public async Task OpenAsioPanelAsync()
    {
        using var r = await _http.PostAsync("api/audio/asio/control-panel", null);
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
    /// <summary>Our engine fork: auto AGC threshold above the noise floor, 0..40 dB.</summary>
    public Task SetAutoAgcOffsetAsync(double db) => PostAsync("api/auto-agc/offset", new AutoAgcOffsetSetRequest(db));
    /// <summary>Move a receiver's display centre without moving its dial.</summary>
    public Task SetReceiverLoAsync(int rx, long hz) => PostAsync($"api/receivers/{rx}/lo", new RadioLoSetRequest(hz));
    /// <summary>Our engine fork: where the dial sits across the display, -0.9..0.9.</summary>
    public Task SetDialPositionAsync(double position) => PostAsync("api/radio/dial-position", new DialPositionSetRequest(position));
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
    public Task SetRxLevelerConfigAsync(bool on, RxLevelerConfig config) =>
        PostAsync("api/rx/leveler/config", new RxLevelerConfigSetRequest(on, config));
    public async Task<List<NotchDto>> GetNotchesAsync() =>
        (await _http.GetFromJsonAsync<List<NotchDto>>("api/rx/notches", Json))!;
    /// <summary>The whole notch list. Centres are RF frequencies in Hz.</summary>
    public Task SetNotchesAsync(IReadOnlyList<NotchDto> notches) => PostAsync("api/rx/notches", new NotchListRequest(notches));
    public async Task<List<BandStackEntryDto>> GetBandStackAsync() =>
        (await _http.GetFromJsonAsync<List<BandStackEntryDto>>("api/bands/stack", Json))!;
    public Task AddBandStackAsync(string band, BandStackAddRequest entry) =>
        PostAsync($"api/bands/stack/{Uri.EscapeDataString(band)}", entry);
    public async Task RemoveBandStackAsync(string band, int id) =>
        await Checked(_http.DeleteAsync($"api/bands/stack/{Uri.EscapeDataString(band)}/{id}"));
    /// <summary>The engine's FFTW planning state, or null if it can't say.</summary>
    public async Task<DspPrep?> GetDspPrepAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync("api/station/dsp-diagnostics", ct));
            var root = doc.RootElement;
            if (!root.TryGetProperty("wdspWisdomPhase", out var phase)) return null;
            return new DspPrep(phase.GetString() ?? "",
                root.TryGetProperty("wdspWisdomStatus", out var status) ? status.GetString() ?? "" : "");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Whether Windows Firewall lets the radio's packets reach the engine.</summary>
    public async Task<FirewallStatus> GetFirewallAsync() =>
        (await _http.GetFromJsonAsync<FirewallStatus>("api/system/windows-firewall", Json))!;

    /// <summary>Add the firewall rule. Windows asks the user to approve it, so
    /// this waits far longer than an ordinary request.</summary>
    public async Task AllowFirewallAsync()
    {
        using var http = new HttpClient { BaseAddress = _http.BaseAddress, Timeout = TimeSpan.FromMinutes(5) };
        using var r = await http.PostAsync("api/system/windows-firewall/allow", null);
        await ThrowIfFailed(r);
    }

    private sealed record EngineInfo(string Version);
    public async Task<string> GetEngineVersionAsync() =>
        (await _http.GetFromJsonAsync<EngineInfo>("api/capabilities", Json))!.Version;

    private sealed record ConnectedBoard(string EffectiveBoard, string? ActiveProtocol);
    /// <summary>The transmit DSP rate: 48 kHz on Protocol 1, otherwise 96 kHz.</summary>
    public async Task<int> GetTxDspRateAsync() =>
        (await _http.GetFromJsonAsync<ConnectedBoard>("api/radio/pa-thermal", Json))!.ActiveProtocol == "P1" ? 48_000 : 96_000;
    /// <summary>The engine's name for the connected board, such as OrionMkII.</summary>
    public async Task<string> GetConnectedBoardAsync() =>
        (await _http.GetFromJsonAsync<ConnectedBoard>("api/radio/pa-thermal", Json))!.EffectiveBoard;

    // ---- options only some radios have ---------------------------------

    public sealed record BoardFlags(bool HasHl2OptionalToggles, bool SupportsAnvelinaDxOc);
    public async Task<BoardFlags> GetBoardFlagsAsync() =>
        (await _http.GetFromJsonAsync<BoardFlags>("api/radio/capabilities", Json))!;

    public async Task<G2OptionsDto> GetG2OptionsAsync() =>
        (await _http.GetFromJsonAsync<G2OptionsDto>("api/radio/g2-options", Json))!;
    public Task SetG2OptionsAsync(bool? dither = null, bool? random = null) =>
        PutAsync("api/radio/g2-options", new G2OptionsSetRequest(dither, random));

    /// <summary>The port the G2's p2app connects to for its front panel (0 = off);
    /// Supported only while connected to a G2 / G2-1K.</summary>
    public async Task<G2CatPortDto> GetG2CatPortAsync() =>
        (await _http.GetFromJsonAsync<G2CatPortDto>("api/radio/g2-cat-port", Json))!;
    public Task SetG2CatPortAsync(int port) => PutAsync("api/radio/g2-cat-port", new G2CatPortSetRequest(port));

    public async Task<Hl2OptionsDto> GetHl2OptionsAsync() =>
        (await _http.GetFromJsonAsync<Hl2OptionsDto>("api/radio/hl2-options", Json))!;
    public Task SetHl2OptionsAsync(bool bandVolts) =>
        PutAsync("api/radio/hl2-options", new Hl2OptionsSetRequest(bandVolts));

    public sealed record Hl2Codec(bool Supported, bool Enabled);
    public async Task<Hl2Codec> GetHl2CodecAsync() =>
        (await _http.GetFromJsonAsync<Hl2Codec>("api/radio/hl2-codec", Json))!;
    public Task SetHl2CodecAsync(bool enabled) =>
        PutAsync("api/radio/hl2-codec", new { Enabled = enabled });

    public async Task<Hl2GpioDto> GetHl2GpioAsync() =>
        (await _http.GetFromJsonAsync<Hl2GpioDto>("api/radio/hl2-gpio", Json))!;
    public Task SetHl2GpioAsync(int bits) =>
        PutAsync("api/radio/hl2-gpio", new Hl2GpioSetRequest(bits));

    // The HL2 IO board (N2ADR): status, per-band antennas, antenna tuner.
    public sealed record Hl2IoBoardStatus(bool Supported, bool Present, byte HardwareVersion, byte FirmwareMajor,
        byte FirmwareMinor, byte Inputs, byte Fault, byte Tuner, string? Error);
    public sealed record Hl2IoBoardBand(string Band, byte RxAntenna, byte TxAntenna);
    public sealed record Hl2IoBoardView(Hl2IoBoardStatus Status, List<Hl2IoBoardBand> Bands, string Tuning, string? TunerError);
    public async Task<Hl2IoBoardView> GetHl2IoBoardAsync() =>
        (await _http.GetFromJsonAsync<Hl2IoBoardView>("api/radio/hl2-io-board", Json))!;
    public Task SetHl2IoBandAsync(Hl2IoBoardBand band) => PutAsync("api/radio/hl2-io-board/antenna", band);
    /// <summary>"tune" (keys a carrier while the tuner asks for RF, up to 15 s), "bypass" or "cancel".</summary>
    public Task<Hl2IoBoardView> Hl2IoCommandAsync(string action) =>
        PostForAsync<Hl2IoBoardView>("api/radio/hl2-io-board/command", new { action });

    public sealed record RadioVariant(string Variant, bool RequiresConfirmation);
    /// <summary>Which radio behind the OrionMkII board ID: they all report the same one.</summary>
    public async Task<RadioVariant> GetRadioVariantAsync() =>
        (await _http.GetFromJsonAsync<RadioVariant>("api/radio/variant", Json))!;
    public async Task SetRadioVariantAsync(OrionMkIIVariant v) =>
        await Checked(_http.PutAsJsonAsync("api/radio/variant", new { variant = v.ToString() }, Json));

    // ---- filters, station hardware and mode options ----
    public Task SetRxFilterWindowAsync(BandpassWindow w) => PostAsync("api/rx/filter-window", new BandpassWindowSetRequest(w));
    public Task SetTxFilterWindowAsync(BandpassWindow w) => PostAsync("api/tx/filter-window", new BandpassWindowSetRequest(w));
    public Task SetRxFilterPhaseAsync(FilterPhaseMode p) => PostAsync("api/rx/filter-phase", new FilterPhaseSetRequest(p));
    public Task SetTxFilterPhaseAsync(FilterPhaseMode p) => PostAsync("api/tx/filter-phase", new FilterPhaseSetRequest(p));
    public async Task<RfFilterSettingsDto> GetRfFiltersAsync() =>
        (await _http.GetFromJsonAsync<RfFilterSettingsDto>("api/radio/rf-filters", Json))!;
    public async Task SetRfFiltersAsync(RfFilterSettingsSetRequest r) =>
        await Checked(_http.PutAsJsonAsync("api/radio/rf-filters", r, Json));

    public sealed record PttInput(bool Enabled);
    public async Task<bool> GetPttInputAsync() =>
        (await _http.GetFromJsonAsync<PttInput>("api/radio/ptt-status", Json))!.Enabled;
    public async Task SetPttInputAsync(bool on) =>
        await Checked(_http.PutAsJsonAsync("api/radio/ptt-status", new PttEnableSetRequest(on), Json));
    public async Task<SerialPttStatus> GetSerialPttAsync() =>
        (await _http.GetFromJsonAsync<SerialPttStatus>("api/radio/serial-ptt", Json))!;
    public async Task SetSerialPttAsync(SerialPttConfig c) =>
        await Checked(_http.PutAsJsonAsync("api/radio/serial-ptt", c, Json));
    // Our engine fork resolves the default radio and workspace to the connected
    // radio's active one, creating it if needed (see ResolveTransverterWorkspace).
    public async Task<TransverterSettingsDto> GetTransverterAsync() =>
        (await _http.GetFromJsonAsync<TransverterSettingsDto>("api/radio/transverter", Json))!;

    /// <summary>Turn transverters on or off for the connected radio, keeping its bands.</summary>
    public async Task SyncTransverterOnAsync(bool on)
    {
        var t = await GetTransverterAsync();
        if (t.Enabled == on) return;
        await SetTransverterAsync(new TransverterSettingsSetRequest(on, t.IfFrequencyHz, t.RfFrequencyHz,
            Bands: t.Bands, ActiveBandId: t.ActiveBandId));
    }

    /// <summary>Make one transverter band the active one, as its band button does.</summary>
    public async Task SelectTransverterBandAsync(int id)
    {
        var t = await GetTransverterAsync();
        if (!t.Enabled || t.ActiveBandId == id) return;
        await SetTransverterAsync(new TransverterSettingsSetRequest(true, t.IfFrequencyHz, t.RfFrequencyHz,
            Bands: t.Bands, ActiveBandId: id));
    }

    public async Task SetTransverterAsync(TransverterSettingsSetRequest r) =>
        await Checked(_http.PutAsJsonAsync("api/radio/transverter", r, Json));
    public Task SetDiversityAsync(DiversitySetRequest r) => PostAsync("api/rx/diversity", r);

    public async Task<AmBroadcastConfig> GetAmBroadcastAsync() =>
        (await _http.GetFromJsonAsync<AmBroadcastConfig>("api/tx/am-broadcast", Json))!;
    public Task SetAmBroadcastAsync(AmBroadcastConfig c) => PostAsync("api/tx/am-broadcast", c);
    public Task SetAmCarrierAsync(int percent) => PostAsync("api/tx/am-carrier-level", new AmCarrierLevelSetRequest(percent));
    public async Task<FmConfig> GetFmAsync() => (await _http.GetFromJsonAsync<FmConfig>("api/fm", Json))!;
    public Task SetFmAsync(FmConfig c) => PostAsync("api/fm", new FmConfigSetRequest(c));
    public async Task<FmStatusDto> GetFmStatusAsync() => (await _http.GetFromJsonAsync<FmStatusDto>("api/fm/status", Json))!;

    /// <summary>Save or load PureSignal's correction, in a file the engine reads and writes.</summary>
    public Task SavePsCorrectionAsync(string path) => PostAsync("api/tx/ps/save", new PsSaveRequest(path));
    public Task RestorePsCorrectionAsync(string path) => PostAsync("api/tx/ps/restore", new PsRestoreRequest(path));

    public sealed record CfcPresetDto(string Name, CfcConfig Config);
    private sealed record CfcPresetList(List<CfcPresetDto> Presets);
    public async Task<List<CfcPresetDto>> GetCfcPresetsAsync() =>
        (await _http.GetFromJsonAsync<CfcPresetList>("api/tx/cfc/presets", Json))!.Presets;
    public async Task SaveCfcPresetAsync(string name, CfcConfig c) =>
        await Checked(_http.PutAsJsonAsync($"api/tx/cfc/presets/{Uri.EscapeDataString(name)}", new CfcSetRequest(c), Json));
    public async Task DeleteCfcPresetAsync(string name) =>
        await Checked(_http.DeleteAsync($"api/tx/cfc/presets/{Uri.EscapeDataString(name)}"));

    // ---- amplifier and calibration ----
    public Task SetPreKeyDelayAsync(int ms) => PostAsync("api/tx/prekey-delay", new TxPreKeyDelaySetRequest(ms));
    public Task SetRxResumeDelayAsync(int ms) => PostAsync("api/tx/rx-resume-delay", new TxRxResumeDelaySetRequest(ms));
    public Task SetTxTimeoutAsync(int seconds) => PostAsync("api/tx/timeout", new TxTimeoutSetRequest(seconds));
    public Task SetDriveMaxAsync(int percent) => PostAsync("api/tx/drive-max", new DriveMaxSetRequest(percent));
    public async Task<RadioSupplyVoltsDto> GetSupplyVoltsAsync() =>
        (await _http.GetFromJsonAsync<RadioSupplyVoltsDto>("api/radio/supply-volts", Json))!;

    public sealed record ExternalReference(bool Enabled, double Ppm);
    public sealed record FrequencyCalibration(double Ppm, ExternalReference ExternalReference);
    public sealed record CalibrationReport(string Outcome, double? OffsetHz, string Message);
    public async Task<FrequencyCalibration> GetFrequencyCalibrationAsync() =>
        (await _http.GetFromJsonAsync<FrequencyCalibration>("api/radio/frequency-calibration", Json))!;
    public Task SetFrequencyPpmAsync(double ppm) => PostAsync("api/radio/frequency-calibration/set", new { ppm });
    public Task ResetFrequencyCalibrationAsync() => PostAsync("api/radio/frequency-calibration/reset", new { });
    public Task SetExternalReferenceAsync(bool enabled, double ppm) =>
        PostAsync("api/radio/frequency-calibration/external-reference", new { enabled, ppm });
    /// <summary>Tunes to WWV, measures its carrier, sets the factor and tunes back. Receive only.</summary>
    public async Task<CalibrationReport> CalibrateOnWwvAsync(double referenceHz)
    {
        using var r = await _http.PostAsync(FormattableString.Invariant($"api/radio/frequency-calibration/calibrate?referenceHz={referenceHz}"), null);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<CalibrationReport>(Json))!;
    }

    public async Task<AdcProtectionConfig> GetAdcProtectionAsync() =>
        (await _http.GetFromJsonAsync<AdcProtectionStatusDto>("api/rx/adc-protection", Json))!.Config;
    public async Task SetAdcProtectionAsync(int maxOffsetDb, int releaseHoldMs) =>
        (await _http.PutAsJsonAsync("api/rx/adc-protection",
            new AdcProtectionSetRequest(MaxOffsetDb: maxOffsetDb, ReleaseHoldMs: releaseHoldMs), Json)).EnsureSuccessStatusCode();

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

    private async Task PutAsync<T>(string path, T body) =>
        await Checked(_http.PutAsJsonAsync(path, body, Json));

    private async Task PostAsync<T>(string path, T body, CancellationToken ct = default)
    {
        using var r = await _http.PostAsJsonAsync(path, body, Json, ct);
        await ThrowIfFailed(r, ct);
    }

    // For requests whose reply isn't used: frees the connection and reports
    // the engine's own reason on failure.
    private static async Task Checked(Task<HttpResponseMessage> call)
    {
        using var r = await call;
        await ThrowIfFailed(r);
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
        int generation = ++_wsGeneration;
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
        _ = Task.Run(() => ReceiveLoop(_ws, _wsCts.Token, generation));
    }

    private const int MaxFrameBytes = 64 * 1024 * 1024;

    // Counts streams opened, so a closing old stream can't report the new one closed.
    private int _wsGeneration;

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct, int generation)
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
                    if (len == buf.Length)
                    {
                        if (buf.Length >= MaxFrameBytes)
                            throw new InvalidDataException("The engine sent an oversized frame.");
                        Array.Resize(ref buf, buf.Length * 2);
                    }
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
        if (generation == Volatile.Read(ref _wsGeneration)) StreamClosed?.Invoke(reason);
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
                case MsgType.CwEngineStatus:
                    CwStatus?.Invoke(CwEngineStatusFrame.Deserialize(msg));
                    break;
                case MsgType.CwDecodedText:
                    CwDecoded?.Invoke(CwDecodedTextFrame.Deserialize(msg));
                    break;
                case MsgType.CwSettings:
                    if (JsonSerializer.Deserialize<CwSettingsDto>(msg[1..], Json) is { } cw) CwSettings?.Invoke(cw);
                    break;
                case MsgType.Alert:
                    Alert?.Invoke(AlertFrame.Deserialize(msg));
                    break;
                case MsgType.MicPeak:
                    MicPeakDbfs?.Invoke(MicPeakFrame.Deserialize(msg).PeakDbfs);
                    break;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IndexOutOfRangeException or System.Text.Json.JsonException or NotSupportedException)
        {
            // A frame shape we don't understand; skip it.
        }
    }

    private void CloseStream()
    {
        _wsCts?.Cancel();
        _ws?.Abort();
        _ws?.Dispose();
        _wsCts?.Dispose();
        _ws = null;
        _wsCts = null;
    }

    public void Dispose()
    {
        CloseStream();
        _http.Dispose();
    }
}

/// <summary>The engine recorder's state (GET /api/recorder).</summary>
public sealed record RecorderStatus(
    string Folder, bool Recording, string? RecordingSource, string? RecordingFile, double RecordingSeconds,
    bool Playing, bool PlayingLocal, string? PlayingFile, double PlayPositionSeconds, double PlayLengthSeconds, string? LastError);
public sealed record RecorderRecordRequest(string File, string Source, int Receiver);
public sealed record RecorderPlayRequest(string File, double GainDb, bool Processed, bool Local);
