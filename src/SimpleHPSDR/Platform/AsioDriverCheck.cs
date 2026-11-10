using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SimpleHPSDR.Platform;

/// <summary>
/// When the engine reports that Windows could not create an ASIO driver, it
/// does not say why: the native bridge drops the error code. This repeats the
/// two steps Windows takes, in the app's own process, and reports their codes:
/// loading the driver's DLL (a missing dependency or a 32-bit file shows here)
/// and creating the driver object (a driver that refuses, often because its
/// device is busy or unplugged, shows here). The driver is never started, only
/// created and released. Runs only after a failure, and only on Windows.
/// </summary>
public static class AsioDriverCheck
{
    /// <summary>Start of the engine's message for this failure (native/asio/simplehpsdr_asio.cpp).</summary>
    private const string Marker = "Could not instantiate the selected ASIO driver";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static bool Applies(string? engineMessage) =>
        OperatingSystem.IsWindows() && engineMessage?.Contains(Marker, StringComparison.Ordinal) == true;

    /// <summary>A sentence to add to the engine's message; null when the check
    /// could not run. Never throws.</summary>
    public static Task<string?> DescribeAsync(string driverId)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult<string?>(null);
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // COM needs a thread of its own in a single-threaded apartment, as
        // the engine uses; a driver that hangs only keeps this thread.
        var thread = new Thread(() =>
        {
            try { done.TrySetResult(OperatingSystem.IsWindows() ? Run(driverId) : null); }
            catch (Exception ex) { done.TrySetResult($"Windows check failed: {ex.Message}"); }
        }) { IsBackground = true, Name = "ASIO driver check" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return WithTimeout(done.Task);
    }

    private static async Task<string?> WithTimeout(Task<string?> check) =>
        await Task.WhenAny(check, Task.Delay(Timeout)) == check
            ? await check
            : $"Windows check: the driver did not answer within {Timeout.TotalSeconds:0} seconds.";

    [SupportedOSPlatform("windows")]
    private static string Run(string driverId)
    {
        if (!Guid.TryParse(driverId, out var clsid)) return $"Windows check: {driverId} is not a driver ID.";
        string? path = InprocServerPath(driverId);

        int? loadError = null;
        if (path is not null)
        {
            IntPtr module = LoadLibraryExW(path, IntPtr.Zero, LoadWithAlteredSearchPath);
            if (module == IntPtr.Zero) loadError = Marshal.GetLastPInvokeError();
            else FreeLibrary(module);
        }

        int hr;
        int init = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded);
        try
        {
            // An ASIO driver answers to its own class ID as the interface ID.
            hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref clsid, out var driver);
            if (hr >= 0 && driver != IntPtr.Zero) Marshal.Release(driver);
        }
        finally
        {
            if (init >= 0) CoUninitialize();
        }
        return Describe(path, loadError, hr);
    }

    /// <summary>The sentence for what the two steps returned.</summary>
    internal static string Describe(string? path, int? loadError, int hr)
    {
        var parts = new List<string>();
        if (path is null)
            parts.Add("the driver has no 64-bit file registered");
        else if (loadError is { } err)
            parts.Add($"loading {path} failed with Windows error {err} ({LoadErrorMeaning(err)})");
        else
            parts.Add($"{path} loads");
        parts.Add(hr >= 0
            ? "creating the driver now works, so it was probably busy or still starting; try selecting it again"
            : $"creating the driver returned 0x{hr:X8} ({ResultMeaning(hr)})");
        return "Windows check: " + string.Join("; ", parts) + ".";
    }

    private static string LoadErrorMeaning(int error) => error switch
    {
        5 => "access denied",
        126 => "a file the driver needs is missing; reinstall the driver or the software it came with",
        127 => "the driver needs a newer version of a file it depends on",
        193 => "the file is not a 64-bit driver; install the maker's 64-bit driver",
        1114 => "the driver's own start-up code failed; its device may be unplugged or in use",
        _ => "see Microsoft's system error codes",
    };

    private static string ResultMeaning(int hr) => unchecked((uint)hr) switch
    {
        0x80004005 => "the driver refused to start; often its device is unplugged or another program has it",
        0x80004002 => "the driver does not offer the ASIO interface",
        0x8007000E => "out of memory",
        0x80040154 => "not registered for 64-bit programs",
        0x80040111 => "the driver file does not provide this driver",
        0x80070005 => "access denied",
        0x8007007E => "a file the driver needs is missing",
        0x800700C1 => "the file is not a 64-bit driver",
        0x8007045A => "the driver's own start-up code failed",
        0x800401F9 => "the driver file has no COM entry point",
        _ => "an error code from the driver or Windows",
    };

    [SupportedOSPlatform("windows")]
    private static string? InprocServerPath(string driverId)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            $@"SOFTWARE\Classes\CLSID\{{{driverId.Trim('{', '}')}}}\InprocServer32");
        if (key?.GetValue(null) is not string value) return null;
        value = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        return value.Length == 0 ? null : value;
    }

    private const uint LoadWithAlteredSearchPath = 0x8;
    private const uint CoinitApartmentThreaded = 0x2;
    private const uint ClsctxInprocServer = 0x1;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr result);
}
