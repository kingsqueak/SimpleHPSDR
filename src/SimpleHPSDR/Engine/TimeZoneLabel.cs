using System.Diagnostics;

namespace SimpleHPSDR.Engine;

/// <summary>The local time zone's short name, such as EST. .NET has only the
/// long names, so macOS and Linux ask the system; Windows has no short names,
/// so its long name is cut down to initials.</summary>
public static class TimeZoneLabel
{
    public static string Local()
    {
        var now = DateTimeOffset.Now;
        if (OperatingSystem.IsWindows()) return Initials(now);
        try
        {
            using var p = Process.Start(new ProcessStartInfo("date", "+%Z")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            });
            var zone = p?.StandardOutput.ReadToEnd().Trim();
            if (!string.IsNullOrEmpty(zone)) return zone;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) { }
        return Offset(now);
    }

    // "Eastern Daylight Time" is EDT; "GMT Standard Time" keeps its GMT.
    private static string Initials(DateTimeOffset now)
    {
        var zone = TimeZoneInfo.Local;
        var name = zone.IsDaylightSavingTime(now) ? zone.DaylightName : zone.StandardName;
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return Offset(now);
        if (words[0].All(char.IsUpper)) return words[0];
        return string.Concat(words.Select(w => w[0]));
    }

    private static string Offset(DateTimeOffset now) => now.ToString("zzz");
}
