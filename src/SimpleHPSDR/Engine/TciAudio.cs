using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;

namespace SimpleHPSDR.Engine;

/// <summary>
/// Receives one receiver's demodulated audio from the local engine's TCI server, for
/// the DeepCW decoder. It only listens: the one way out refuses every command that
/// isn't on the short list below, so nothing here can tune or transmit.
/// </summary>
public sealed class TciAudio : IDisposable
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
        { "audio_start", "audio_stop", "audio_samplerate", "audio_stream_channels" };

    public delegate void AudioHandler(ReadOnlySpan<float> mono, int rate);

    private const int HeaderBytes = 64;
    private const int RxAudioStream = 1;
    private const int Int16Format = 0, Float32Format = 3;
    private const int Rate = 48000;

    private readonly string _host;
    private readonly int _port;
    private readonly int _rx;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private int _channels = 2;   // until the server echoes our request for mono
    private float[] _mono = new float[4096];

    public event Action? Changed;
    public event AudioHandler? AudioReceived;

    public bool Connected { get; private set; }
    public string Error { get; private set; } = "";
    public int Receiver => _rx;

    public TciAudio(string host, int port, int rx)
    {
        (_host, _port, _rx) = (host, port, rx);
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri($"ws://{_host}:{_port}/"), _stop.Token);
                _ws = ws;
                Error = "";
                await ReceiveAsync(ws);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is WebSocketException or IOException or InvalidOperationException)
            {
                Error = e.InnerException?.Message ?? e.Message;
            }
            _ws = null;
            Connected = false;
            Changed?.Invoke();
            try { await Task.Delay(2000, _stop.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReceiveAsync(ClientWebSocket ws)
    {
        var buffer = new byte[1 << 16];
        var used = 0;
        while (ws.State == WebSocketState.Open)
        {
            if (used == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
            var r = await ws.ReceiveAsync(buffer.AsMemory(used), _stop.Token);
            if (r.MessageType == WebSocketMessageType.Close) return;
            used += r.Count;
            if (!r.EndOfMessage) continue;
            if (r.MessageType == WebSocketMessageType.Text)
                await OnTextAsync(Encoding.UTF8.GetString(buffer, 0, used));
            else
                OnBinary(buffer.AsSpan(0, used));
            used = 0;
        }
    }

    private async Task OnTextAsync(string text)
    {
        foreach (var command in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = command.IndexOf(':');
            var verb = (colon < 0 ? command : command[..colon]).ToLowerInvariant();
            var args = colon < 0 ? [] : command[(colon + 1)..].Split(',');
            switch (verb)
            {
                case "ready":
                    await SendAsync($"audio_samplerate:{Rate}");
                    await SendAsync("audio_stream_channels:1");
                    await SendAsync($"audio_start:{_rx}");
                    Connected = true;
                    Changed?.Invoke();
                    break;
                case "audio_stream_channels" when args.Length > 0 && int.TryParse(args[0], out var ch) && ch is 1 or 2:
                    _channels = ch;
                    break;
            }
        }
    }

    private void OnBinary(ReadOnlySpan<byte> frame)
    {
        if (frame.Length <= HeaderBytes) return;
        var receiver = BinaryPrimitives.ReadInt32LittleEndian(frame);
        var rate = BinaryPrimitives.ReadInt32LittleEndian(frame[4..]);
        var format = BinaryPrimitives.ReadInt32LittleEndian(frame[8..]);
        var length = BinaryPrimitives.ReadInt32LittleEndian(frame[20..]);
        var type = BinaryPrimitives.ReadInt32LittleEndian(frame[24..]);
        if (type != RxAudioStream || receiver != _rx || rate <= 0) return;
        var data = frame[HeaderBytes..];
        int available = format switch { Float32Format => data.Length / 4, Int16Format => data.Length / 2, _ => 0 };
        int count = Math.Min(length > 0 ? length : available, available);
        int frames = count / _channels;
        if (frames == 0) return;
        if (_mono.Length < frames) _mono = new float[frames * 2];
        if (format == Float32Format)
        {
            var f = MemoryMarshal.Cast<byte, float>(data)[..count];
            for (int i = 0; i < frames; i++) _mono[i] = f[i * _channels];
        }
        else
            for (int i = 0; i < frames; i++)
                _mono[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * _channels * 2)..]) / 32768f;
        AudioReceived?.Invoke(_mono.AsSpan(0, frames), rate);
    }

    private async Task SendAsync(string command)
    {
        var verb = command.Split(':', 2)[0];
        if (!Allowed.Contains(verb)) throw new InvalidOperationException($"TCI command not allowed: {verb}");
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open }) return;
        await _sendLock.WaitAsync();
        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(command + ";"), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException) { }
        finally { _sendLock.Release(); }
    }

    public void Dispose()
    {
        try { SendAsync($"audio_stop:{_rx}").Wait(500); }
        catch (AggregateException) { }
        _stop.Cancel();
        try { _loop.Wait(1000); }
        catch (AggregateException) { }
        _stop.Dispose();
        _sendLock.Dispose();
    }
}
