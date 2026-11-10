// Decode WAV files (mono 16-bit, 48 kHz) with the station-engine's CwDecoderCore,
// fed in 4096-sample blocks like CwDecoderService does. Prints one line per file:
// <name>\t<seconds of CPU>\t<text>
using System.Diagnostics;
using System.Text;
using SimpleHPSDR.Server;

foreach (var path in args)
{
    var (rate, pcm) = ReadWav(path);
    var core = new CwDecoderCore(rate, 600);
    var sb = new StringBuilder();
    var cpu = Process.GetCurrentProcess().TotalProcessorTime;
    for (int i = 0; i < pcm.Length; i += 4096)
        core.Process(pcm.AsSpan(i, Math.Min(4096, pcm.Length - i)), s => sb.Append(s.Text));
    // Flush: a couple of seconds of silence lets the last word commit.
    var quiet = new float[4096];
    for (int i = 0; i < rate * 3 / 4096; i++) core.Process(quiet, s => sb.Append(s.Text));
    var used = Process.GetCurrentProcess().TotalProcessorTime - cpu;
    Console.WriteLine($"{Path.GetFileName(path)}\t{used.TotalSeconds:0.000}\t{sb.ToString().Replace('\n', ' ')}");
}

static (int Rate, float[] Pcm) ReadWav(string path)
{
    var b = File.ReadAllBytes(path);
    int rate = BitConverter.ToInt32(b, 24);
    int p = 12;
    while (p + 8 <= b.Length)
    {
        string id = Encoding.ASCII.GetString(b, p, 4);
        int len = BitConverter.ToInt32(b, p + 4);
        if (id == "data")
        {
            var pcm = new float[len / 2];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = BitConverter.ToInt16(b, p + 8 + i * 2) / 32768f;
            return (rate, pcm);
        }
        p += 8 + len + (len & 1);
    }
    throw new InvalidDataException(path);
}
