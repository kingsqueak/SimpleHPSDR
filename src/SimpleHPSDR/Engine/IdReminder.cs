using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SimpleHPSDR.Engine;

/// <summary>
/// The ID timer's sound. It plays through the computer's default output with
/// the system's own player, apart from the engine, so it can't reach the
/// transmit path. Built-in beeps are written to a small WAV once.
/// </summary>
public static class IdReminder
{
    private const int Rate = 48000;
    private const double RampSeconds = 0.005;
    // 0 Hz is a rest.
    private static readonly (double Hz, double Seconds)[] Tones = [(440, 0.1), (0, 0.1), (440, 0.1), (800, 0.2)];

    private static string BeepPath => Path.Combine(EngineHost.DataDir, "id-beeps.wav");

    /// <summary>Play the chosen file, or the beeps if there isn't one, at a
    /// volume of 0 to 100. Null if it played, otherwise why it didn't.</summary>
    public static Task<string?> PlayAsync(string? wavPath, int volumePercent) => Task.Run(() =>
    {
        var path = !string.IsNullOrEmpty(wavPath) && File.Exists(wavPath) ? wavPath : BeepFile();
        double volume = Math.Clamp(volumePercent, 0, 100) / 100.0;
        return OperatingSystem.IsWindows() ? PlayWindows(path, volume) : PlayWithTool(path, volume);
    });

    // Written each time, so a change to the tones can't leave an old file.
    private static string BeepFile()
    {
        int ramp = (int)(RampSeconds * Rate);
        var samples = new List<short>();
        foreach (var (hz, seconds) in Tones)
        {
            int length = (int)(seconds * Rate);
            for (int i = 0; i < length; i++)
            {
                double edge = Math.Min(1, Math.Min(i, length - 1 - i) / (double)ramp);   // no clicks
                samples.Add((short)(short.MaxValue * edge * Math.Sin(2 * Math.PI * hz * i / Rate)));
            }
        }
        using var w = new BinaryWriter(File.Create(BeepPath));
        int bytes = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var s in samples) w.Write(s);
        return BeepPath;
    }

    // The players are tried in turn. aplay, the last resort, has no volume.
    private static string? PlayWithTool(string path, double volume)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        (string Tool, string[] Args)[] players = OperatingSystem.IsMacOS()
            ? [("afplay", ["-v", volume.ToString(inv), path])]
            :
            [
                ("paplay", [$"--volume={(int)(volume * 65536)}", path]),
                ("pw-play", [$"--volume={volume.ToString(inv)}", path]),
                ("aplay", ["-q", path]),
            ];
        foreach (var (tool, args) in players)
        {
            try
            {
                var start = new ProcessStartInfo(tool)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                foreach (var a in args) start.ArgumentList.Add(a);
                using var p = Process.Start(start);
                if (p is null) continue;
                p.WaitForExit();
                if (p.ExitCode == 0) return null;
            }
            catch (System.ComponentModel.Win32Exception) { }   // not installed
        }
        return $"Couldn't play it with {string.Join(", ", players.Select(p => p.Tool))}.";
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySoundW(string sound, IntPtr module, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutSetVolume(IntPtr device, uint volume);

    private static string? PlayWindows(string path, double volume)
    {
        const uint Filename = 0x20000, NoDefault = 0x2;   // waits until it finishes
        uint level = (uint)(volume * 0xFFFF);
        waveOutSetVolume(IntPtr.Zero, level | level << 16);   // left and right
        return PlaySoundW(path, IntPtr.Zero, Filename | NoDefault) ? null : "Windows couldn't play that file.";
    }
}
