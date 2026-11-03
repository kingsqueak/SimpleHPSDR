using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zeus.Contracts;

namespace SimpleHPSDR.Engine;

public sealed record FilterPreset(string SlotName, string Label, int LowHz, int HighHz);

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
    public event Action<float>? SignalDbm;
    public event Action<VfoStateFrame>? Vfo;
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

    public Task SetVfoAsync(long hz) => PostAsync("api/vfo", new VfoSetRequest(hz));
    public Task SetModeAsync(RxMode mode) => PostAsync("api/mode", new ModeSetRequest(mode));
    public Task SetFilterAsync(int low, int high, string? preset) =>
        PostAsync("api/filter", new FilterSetRequest(low, high, preset));
    public Task SetAfGainAsync(double db) => PostAsync("api/rx/afGain", new RxAfGainSetRequest(db));
    public Task SetAttenuatorAsync(int db) => PostAsync("api/attenuator", new AttenuatorSetRequest(db));
    public Task SetPreampAsync(bool on) => PostAsync("api/preamp", new PreampSetRequest(on));
    public Task SetMuteAsync(bool muted) => PostAsync("api/audio/mute", new { muted });
    public Task SetZoomAsync(int level) => PostAsync("api/rx/zoom", new { level });

    public async Task<FilterPreset[]> GetFilterPresetsAsync(RxMode mode) =>
        await _http.GetFromJsonAsync<FilterPreset[]>($"api/filter/presets?mode={mode}", Json) ?? [];

    private async Task PostAsync<T>(string path, T body, CancellationToken ct = default)
    {
        using var r = await _http.PostAsJsonAsync(path, body, Json, ct);
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
                    Display?.Invoke(DisplayFrame.Deserialize(msg));
                    break;
                case MsgType.RxMetersV2:
                    var m = RxMetersV2Frame.Deserialize(msg);
                    if (m.RxId == 0) SignalDbm?.Invoke(m.SignalAv);
                    break;
                case MsgType.RxMeter:
                    SignalDbm?.Invoke(RxMeterFrame.Deserialize(msg).RxDbm);
                    break;
                case MsgType.VfoState:
                    Vfo?.Invoke(VfoStateFrame.Deserialize(msg));
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
