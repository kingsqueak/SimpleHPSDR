using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace SimpleHPSDR.Engine;

/// <summary>
/// The ANAN G2's front panel, through Saturn's p2app (laurencebarker/Saturn,
/// sw_projects/P2_app). p2app reads the panel itself, the original G2 panel on
/// the Pi's GPIO and I2C (libgpiod 1 or 2) or the G2V2 / G2 Ultra panel over
/// serial, and sends its controls as Andromeda CAT messages to a TCP port the
/// SDR program announces in Protocol 2 (the engine's g2-cat-port). p2app
/// connects to us; we listen. Messages (terminated by ';'):
///   ZZZUnn; ZZZDnn;    VFO knob steps up / down
///   ZZZEnnn;           encoder*10 + steps (1-9), +500 when turned down
///   ZZZPnnn;           button scan code*10 + event (0 release, 1 press, 2 long)
///   ZZZSppHHsss;       panel: product 04 = original G2 panel, 05 = G2V2 / Ultra
/// p2app also asks ZZXV; ZZUT; ZZYR; to light the panel's LEDs.
/// p2app never retries a failed first connect and can crash if the listener
/// goes away first, so the owner listens before announcing the port and
/// announces 0, then waits, before stopping (see MainViewModel.G2Panel).
/// </summary>
public sealed class G2PanelLink : IDisposable
{
    private readonly object _sync = new();
    private TcpListener? _listener;
    private TcpClient? _peer;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private IPAddress? _radio;

    /// <summary>Raised on a thread-pool thread.</summary>
    public event Action<int>? PanelIdentified;      // product: 4 original G2 panel, 5 G2V2 / Ultra
    public event Action<int>? VfoSteps;             // + up, - down
    public event Action<int, int>? Encoder;         // encoder number, signed steps
    public event Action<int, int>? Button;          // scan code, event (0 release, 1 press, 2 long)
    public event Action<bool>? ConnectedChanged;

    /// <summary>The reply to one of p2app's questions (ZZXV, ZZUT, ZZYR), or null.</summary>
    public Func<string, string?>? Answer { get; set; }

    public bool IsListening { get { lock (_sync) return _listener is not null; } }
    public bool IsConnected { get { lock (_sync) return _peer is not null; } }
    public int Port { get; private set; }

    /// <summary>Listen on all interfaces (p2app connects to this computer's LAN
    /// address); only the radio itself, or this computer, is accepted.</summary>
    public void Start(int port, IPAddress? radio)
    {
        Stop();
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start(1);
        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            _listener = listener;
            _cts = cts;
            _radio = radio;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        _ = AcceptLoopAsync(listener, cts.Token);
    }

    public void Stop()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        lock (_sync)
        {
            listener = _listener;
            cts = _cts;
            _listener = null;
            _cts = null;
        }
        cts?.Cancel();
        try { listener?.Stop(); } catch (SocketException) { }
        DropPeer();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (Exception) { return; }
            var from = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (from is null || !Allowed(from))
            {
                client.Dispose();
                continue;
            }
            DropPeer();
            client.NoDelay = true;
            lock (_sync)
            {
                _peer = client;
                _stream = client.GetStream();
            }
            ConnectedChanged?.Invoke(true);
            _ = ReadLoopAsync(client, ct);
        }
    }

    private bool Allowed(IPAddress from)
    {
        if (from.IsIPv4MappedToIPv6) from = from.MapToIPv4();
        if (IPAddress.IsLoopback(from)) return true;
        IPAddress? radio;
        lock (_sync) radio = _radio;
        if (radio is not null && (IPAddress.IsLoopback(radio) || from.Equals(radio))) return true;
        // SimpleHPSDR on the G2's own Pi, connected by its LAN address.
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(u => u.Address.Equals(from));
        }
        catch (NetworkInformationException) { return false; }
    }

    private async Task ReadLoopAsync(TcpClient client, CancellationToken ct)
    {
        var buffer = new byte[1024];
        var line = new StringBuilder();
        try
        {
            var stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buffer, ct);
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    char c = (char)buffer[i];
                    if (c == ';')
                    {
                        Handle(line.ToString().Trim());
                        line.Clear();
                    }
                    else if (line.Length < 64 && c >= ' ') line.Append(c);
                }
            }
        }
        catch (Exception) { }
        lock (_sync) if (!ReferenceEquals(_peer, client)) return;
        DropPeer();
    }

    private void DropPeer()
    {
        TcpClient? peer;
        lock (_sync)
        {
            peer = _peer;
            _peer = null;
            _stream = null;
        }
        if (peer is null) return;
        peer.Dispose();
        ConnectedChanged?.Invoke(false);
    }

    internal void Handle(string msg)
    {
        if (msg.Length < 4 || !msg.StartsWith("ZZ", StringComparison.Ordinal)) return;
        string cmd = msg[..4], arg = msg[4..];
        int.TryParse(arg, out int v);
        switch (cmd)
        {
            case "ZZZU": if (v > 0) VfoSteps?.Invoke(v); break;
            case "ZZZD": if (v > 0) VfoSteps?.Invoke(-v); break;
            case "ZZZE":
            {
                bool down = v >= 500;
                int rest = down ? v - 500 : v;
                int steps = rest % 10;
                if (steps > 0) Encoder?.Invoke(rest / 10, down ? -steps : steps);
                break;
            }
            case "ZZZP": Button?.Invoke(v / 10, v % 10); break;
            case "ZZZS": if (arg.Length >= 2 && int.TryParse(arg[..2], out int product)) PanelIdentified?.Invoke(product); break;
            default:
                if (arg.Length == 0 && Answer?.Invoke(cmd) is { } reply) Send(reply);
                break;
        }
    }

    private void Send(string text)
    {
        NetworkStream? stream;
        lock (_sync) stream = _stream;
        if (stream is null) return;
        var bytes = Encoding.ASCII.GetBytes(text);
        try { lock (stream) stream.Write(bytes); }
        catch (Exception) { }
    }

    public void Dispose() => Stop();
}
