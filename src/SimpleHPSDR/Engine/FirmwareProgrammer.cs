using System.Net;
using System.Net.Sockets;

namespace SimpleHPSDR.Engine;

/// <summary>The radio's answer to a discovery probe on the programming port.</summary>
public sealed record FirmwareProbe(string Mac, bool Running, byte BoardId, byte FirmwareVersion, byte BetaVersion)
{
    /// <summary>As the radio reports it, e.g. 2.2b15.</summary>
    public string Firmware => $"{FirmwareVersion / 10}.{FirmwareVersion % 10}" + (BetaVersion == 0 ? "" : $"b{BetaVersion}");
}

/// <summary>
/// OpenHPSDR Protocol 2 FPGA programming, UDP to port 1024, as in TAPR's
/// HPSDRProgrammer and deskHPSDR's p2_programmer.c (DL1BZ, GPL-3). Erase
/// (type 4, answered twice with type 3, up to 30 s each), then the .rbf in
/// 256-byte blocks padded with 0xFF (type 5: sequence, type, block count,
/// data), each acknowledged by type 4 with its sequence. The protocol has no
/// read-back: once the erase is done the radio has no gateware until a full
/// program completes. It runs apart from the engine, which must not be
/// connected to the radio.
/// </summary>
public static class FirmwareProgrammer
{
    public const int Port = 1024;
    public const int BlockBytes = 256;

    private const int DiscoverLength = 60, EraseLength = 115, ProgramLength = 265;
    private const byte Discover = 0x02, Erase = 0x04, Program = 0x05;
    private const byte ReplyIdle = 0x02, ReplyRunning = 0x03, ReplyErase = 0x03, ReplyProgram = 0x04;
    private const int Retries = 5;
    private static readonly TimeSpan EraseTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BlockTimeout = TimeSpan.FromSeconds(2);

    public static int BlocksFor(long bytes) => (int)((bytes + BlockBytes - 1) / BlockBytes);

    /// <summary>Ask the radio who it is and whether it's running. Null if it doesn't answer.</summary>
    public static async Task<FirmwareProbe?> ProbeAsync(IPEndPoint radio, int attempts = 3)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var packet = new byte[DiscoverLength];
        packet[4] = Discover;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            // With the radio powered off, the send itself can fail (no route
            // or host down). That's no answer, not an error.
            try { await udp.SendAsync(packet, radio); }
            catch (SocketException) { continue; }
            var reply = await ReceiveAsync(udp, radio, BlockTimeout,
                b => b.Length >= 14 && b[4] is ReplyIdle or ReplyRunning);
            if (reply is not null)
                return new FirmwareProbe(Convert.ToHexString(reply, 5, 6), reply[4] == ReplyRunning, reply[11], reply[13],
                    reply.Length > 23 ? reply[23] : (byte)0);
        }
        return null;
    }

    /// <summary>After a write: wait for the radio to go quiet (two probes in a
    /// row unanswered, so one lost packet doesn't count) and then answer
    /// again, which shows it rebooted. Runs until cancelled.</summary>
    public static async Task<FirmwareProbe> WaitForRestartAsync(IPEndPoint radio, string mac, CancellationToken cancel)
    {
        int silent = 0;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var probe = await ProbeAsync(radio, attempts: 1);
            if (probe is null)
            {
                silent++;
                await Task.Delay(1000, cancel);
            }
            else if (silent >= 2 && string.Equals(probe.Mac, mac, StringComparison.OrdinalIgnoreCase)) return probe;
            else
            {
                silent = 0;
                await Task.Delay(1000, cancel);
            }
        }
    }

    /// <summary>Erase, then program. Throws if the radio stops answering; the
    /// message says whether the erase had already happened.</summary>
    public static async Task WriteAsync(IPEndPoint radio, byte[] rbf, IProgress<(string Text, double Fraction)> progress)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        progress.Report(("Erasing the radio's flash…", 0));
        var erase = new byte[EraseLength];
        erase[4] = Erase;
        await udp.SendAsync(erase, radio);
        for (int reply = 0; reply < 2; reply++)
            if (await ReceiveAsync(udp, radio, EraseTimeout, b => IsReply(b, ReplyErase, 0)) is null)
                throw new TimeoutException("The radio didn't confirm the erase.");

        int blocks = BlocksFor(rbf.Length);
        var packet = new byte[ProgramLength];
        for (int seq = 0; seq < blocks; seq++)
        {
            WriteBe32(packet, 0, (uint)seq);
            packet[4] = Program;
            WriteBe32(packet, 5, (uint)blocks);
            var data = packet.AsSpan(9, BlockBytes);
            data.Fill(0xFF);
            rbf.AsSpan(seq * BlockBytes, Math.Min(BlockBytes, rbf.Length - seq * BlockBytes)).CopyTo(data);

            bool acknowledged = false;
            int block = seq;
            for (int attempt = 0; attempt < Retries && !acknowledged; attempt++)
            {
                await udp.SendAsync(packet, radio);
                acknowledged = await ReceiveAsync(udp, radio, BlockTimeout, b => IsReply(b, ReplyProgram, (uint)block)) is not null;
            }
            if (!acknowledged)
                throw new TimeoutException($"The radio stopped answering at block {seq + 1} of {blocks}.");
            if (seq + 1 == blocks || (seq + 1) % 100 == 0)
                progress.Report(($"Writing block {seq + 1} of {blocks}…", (seq + 1) / (double)blocks));
        }
    }

    private static bool IsReply(byte[] b, byte type, uint seq) =>
        b.Length >= 5 && b[4] == type && ReadBe32(b) == seq;

    // The next datagram from the radio that matches, or null on timeout.
    private static async Task<byte[]?> ReceiveAsync(UdpClient udp, IPEndPoint radio, TimeSpan timeout, Func<byte[], bool> match)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                UdpReceiveResult r;
                // Windows reports an earlier send's ICMP unreachable here, for
                // instance while the radio reboots; that's just no reply yet.
                try { r = await udp.ReceiveAsync(cts.Token); }
                catch (SocketException) { await Task.Delay(50, cts.Token); continue; }
                if (r.RemoteEndPoint.Address.Equals(radio.Address) && match(r.Buffer)) return r.Buffer;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static uint ReadBe32(byte[] b) => (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);

    private static void WriteBe32(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }
}
