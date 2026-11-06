using System.Reflection;
using System.Runtime.InteropServices;

namespace SimpleHPSDR.Engine;

/// <summary>
/// The optional DRM decoder, SimpleDream's bridge to DecDRM. It isn't part of
/// this app: DRM only appears when the library is found. Set SIMPLEHPSDR_DRM_LIB
/// to its path, put it beside the app, or in a development tree have SimpleDream
/// checked out next to this repository. Strings are UTF-8 JSON.
/// </summary>
internal static partial class DrmNative
{
    private const string Lib = "simpledream_native";
    private static readonly Lazy<nint> Handle = new(Find);

    static DrmNative() =>
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(),
            (name, _, _) => name == Lib ? Handle.Value : 0);

    public static bool Available => Handle.Value != 0;

    private static string FileName =>
        OperatingSystem.IsWindows() ? $"{Lib}.dll"
        : OperatingSystem.IsMacOS() ? $"lib{Lib}.dylib" : $"lib{Lib}.so";

    private static IEnumerable<string> Places()
    {
        if (Environment.GetEnvironmentVariable("SIMPLEHPSDR_DRM_LIB") is { Length: > 0 } env) yield return env;
        yield return Path.Combine(AppContext.BaseDirectory, FileName);
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            yield return Path.Combine(d.FullName, "SimpleDream", "native", "target", "release", FileName);
    }

    private static nint Find() =>
        Places().Where(File.Exists).Select(p => NativeLibrary.TryLoad(p, out var h) ? h : 0)
            .FirstOrDefault(h => h != 0);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sd_new(string config);
    [LibraryImport(Lib)]
    private static partial void sd_free(nint h);
    [LibraryImport(Lib)]
    private static unsafe partial void sd_push_iq(nint h, float* iq, int pairs, int rate, double shiftHz, int mirror);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void sd_command(nint h, string cmd);
    [LibraryImport(Lib)]
    private static partial nint sd_status(nint h);
    [LibraryImport(Lib)]
    private static partial nint sd_events(nint h);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sd_schedule_sources(string dir);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sd_schedule(string dir);
    [LibraryImport(Lib)]
    private static partial void sd_string_free(nint p);

    private static string Take(nint p)
    {
        if (p == 0) return "";
        var s = Marshal.PtrToStringUTF8(p) ?? "";
        sd_string_free(p);
        return s;
    }

    public static nint Create(string config) => sd_new(config);
    public static void Destroy(nint h) => sd_free(h);
    public static void Command(nint h, string json) => sd_command(h, json);
    public static string Status(nint h) => Take(sd_status(h));
    public static string Events(nint h) => Take(sd_events(h));
    public static string ScheduleSources(string dir) => Take(sd_schedule_sources(dir));
    public static string Schedule(string dir) => Take(sd_schedule(dir));

    public static unsafe void PushIq(nint h, ReadOnlySpan<float> iq, int rate, double shiftHz, bool mirror)
    {
        fixed (float* p = iq)
            sd_push_iq(h, p, iq.Length / 2, rate, shiftHz, mirror ? 1 : 0);
    }
}
