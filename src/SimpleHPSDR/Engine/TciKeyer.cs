using System.Net.WebSockets;
using System.Text;

namespace SimpleHPSDR.Engine;

/// <summary>
/// A small TCI client on the local engine, for its keyer command: the engine's
/// only software key. Every key-down carries a duration, so the engine lifts
/// the key by itself even if this app stalls or misses the key-up.
/// </summary>
public sealed class TciKeyer(int port) : IDisposable
{
    private readonly Uri _uri = new($"ws://127.0.0.1:{port}/");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private ClientWebSocket? _ws;

    public int Port { get; } = port;

    private async Task<ClientWebSocket> OpenAsync()
    {
        if (_ws is { State: WebSocketState.Open } open) return open;
        _ws?.Dispose();
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(_uri, _cts.Token);
        _ws = ws;
        _ = Task.Run(() => DrainAsync(ws));
        return ws;
    }

    // The engine reports everything to a TCI client; none of it is needed here.
    private async Task DrainAsync(ClientWebSocket ws)
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                await ws.ReceiveAsync(buf.AsMemory(), _cts.Token);
        }
        catch (Exception) { }   // the next send reconnects or reports it
    }

    private async Task SendAsync(string command)
    {
        await _gate.WaitAsync(_cts.Token);
        try
        {
            var ws = await OpenAsync();
            await ws.SendAsync(Encoding.ASCII.GetBytes(command), WebSocketMessageType.Text, true, _cts.Token);
        }
        finally { _gate.Release(); }
    }

    public Task DownAsync(int maxMs) => SendAsync($"keyer:0,true,{maxMs};");
    public Task UpAsync() => SendAsync("keyer:0,false;");

    public void Dispose()
    {
        _cts.Cancel();
        _ws?.Dispose();
        _gate.Dispose();
    }
}
