using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimpleHPSDR.Stations;

public enum AirState { Off, StartingSoon, EndingSoon, OnAir }

/// <summary>One line of the EiBi schedule, its codes turned into names.</summary>
public sealed record Broadcast(double Khz, int Start, int Stop, DayRule Days, EibiSeasonRule Season,
    string Station, string Language, string Target, string Country, string Site)
{
    private const int Day = 1440;

    /// <summary>End in minutes after the start day's midnight; a stop not after the start is the next day.</summary>
    private int End => Stop > Start ? Stop : Stop + Day;

    public bool RunsOn(DateOnly d) => Season.Allows(d) && Days.Allows(d);

    private bool OnAirAt(DateTime utc)
    {
        var day = DateOnly.FromDateTime(utc);
        var minute = (int)utc.TimeOfDay.TotalMinutes;
        return (RunsOn(day) && minute >= Start && minute < End)
               || (RunsOn(day.AddDays(-1)) && minute + Day >= Start && minute + Day < End);
    }

    public AirState StateAt(DateTime utc, int soonMinutes = 15, int endingMinutes = 10)
    {
        if (OnAirAt(utc))
            return OnAirAt(utc.AddMinutes(endingMinutes)) ? AirState.OnAir : AirState.EndingSoon;
        return OnAirAt(utc.AddMinutes(soonMinutes)) ? AirState.StartingSoon : AirState.Off;
    }
}

/// <summary>Which days a broadcast runs: a weekday mask (bit 0 is Monday), and the
/// month forms EiBi uses, such as the first Saturday or the 15th of September.</summary>
public sealed record DayRule(int Mask, string Text, Func<DateOnly, bool>? Month = null, bool Irregular = false)
{
    public bool Allows(DateOnly d)
    {
        if (Irregular) return true;
        var bit = ((int)d.DayOfWeek + 6) % 7;
        return (Mask >> bit & 1) != 0 && (Month?.Invoke(d) ?? true);
    }
}

/// <summary>When in the broadcast year a line applies: its seasons and validity dates.</summary>
public sealed record EibiSeasonRule(bool Winter = true, bool Summer = true, (int Month, int Day)? From = null, (int Month, int Day)? To = null)
{
    public static readonly EibiSeasonRule Inactive = new(false, false);

    public bool Allows(DateOnly d)
    {
        if (!(EibiSeason.IsWinter(d) ? Winter : Summer)) return false;
        // Both dates are a window that recurs every year, and it may run over New Year.
        if (From is { } a && To is { } b)
        {
            var (from, to, day) = (a.Month * 100 + a.Day, b.Month * 100 + b.Day, d.Month * 100 + d.Day);
            return from <= to ? from <= day && day <= to : day >= from || day <= to;
        }
        return (From is not { } f || d >= EibiSeason.DateOf(d, f.Month, f.Day))
               && (To is not { } t || d <= EibiSeason.DateOf(d, t.Month, t.Day));
    }
}

/// <summary>Broadcast seasons: A from the last Sunday of March, B from the last Sunday of October.</summary>
public static class EibiSeason
{
    private static DateOnly LastSunday(int year, int month)
    {
        var d = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        return d.AddDays(-(int)d.DayOfWeek);
    }

    public static bool IsWinter(DateOnly d) => d < LastSunday(d.Year, 3) || d >= LastSunday(d.Year, 10);

    /// <summary>"a26" or "b26"; January to March belongs to the B season of the year before.</summary>
    public static string Code(DateOnly d)
    {
        var year = d < LastSunday(d.Year, 3) ? d.Year - 1 : d.Year;
        return $"{(IsWinter(d) ? 'b' : 'a')}{year % 100:00}";
    }

    /// <summary>A ddmm date within the season of <paramref name="d"/>: a B season runs over New Year.</summary>
    public static DateOnly DateOf(DateOnly d, int month, int day)
    {
        var start = IsWinter(d) ? (d.Month >= 7 ? d.Year : d.Year - 1) : d.Year;
        var year = IsWinter(d) && month < 7 ? start + 1 : start;
        return new DateOnly(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
    }
}

/// <summary>
/// EiBi's CSV (eibispace.de, by Eike Bierwirth). The rules follow DecDRM's reader, and the
/// code tables come from Dream's station data by way of DecDRM (both GPL-2.0-or-later).
/// </summary>
public static class EibiSchedule
{
    private static readonly string[] DayNames = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];
    private static readonly string[] MonthNames = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    private const int Daily = 0b111_1111;
    /// <summary>Below the long-wave broadcast band EiBi lists navy and time stations.</summary>
    private const double LowestKhz = 148;

    // Lines that aren't listening material. EiBi marks digital voice with the word DIGITAL
    // in the station's name; data stations have a language code or a mode in the name.
    private static readonly HashSet<string> DataLanguages = new(StringComparer.OrdinalIgnoreCase) { "-HF", "-TY" };
    private static readonly Regex DigitalName = new(@"\b(DIGITAL|DRM|RTTY|HFDL|FAX|WEFAX|FSK|PSK|DGPS|NAVTEX|SITOR|STANAG|ALE)\b", RegexOptions.Compiled);

    private sealed record Tables(
        Dictionary<string, string> Languages, Dictionary<string, string> Countries,
        Dictionary<string, string> Targets, Dictionary<string, string> Sites);

    private static readonly Lazy<Tables> Codes = new(LoadTables);

    private static Tables LoadTables()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("eibi-tables.json")!;
        using var doc = JsonDocument.Parse(s);
        Dictionary<string, string> Map(string key) =>
            doc.RootElement.GetProperty(key).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
        return new Tables(Map("languages"), Map("countries"), Map("targets"), Map("sites"));
    }

    public static bool IsDigital(string station, string languageCode) =>
        DataLanguages.Contains(languageCode) || DigitalName.IsMatch(station);

    /// <summary>The non-digital broadcasts in a CSV file's text. Lines that can't be read are skipped.</summary>
    public static List<Broadcast> Parse(string csv)
    {
        var list = new List<Broadcast>(9000);
        foreach (var line in csv.Split('\n'))
        {
            var f = line.TrimEnd('\r').Split(';');
            if (f.Length < 9 || f[0].StartsWith("kHz", StringComparison.OrdinalIgnoreCase)) continue;
            if (ParseRow(f) is { } b) list.Add(b);
        }
        return list;
    }

    private static Broadcast? ParseRow(string[] f)
    {
        if (!double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var khz) || khz < LowestKhz) return null;
        if (IsDigital(f[4], f[5])) return null;
        if (!ParseTimes(f[1], out var start, out var stop)) return null;
        var (days, alternative) = ParseDays(f[2].Trim());
        var season = alternative ? EibiSeasonRule.Inactive : SeasonOf(f[8], f.Length > 9 ? f[9] : "", f.Length > 10 ? f[10] : "");
        return new Broadcast(khz, start, stop, days, season, f[4].Trim(), LanguageNames(f[5]),
            Name(Codes.Value.Targets, f[6]) ?? Name(Codes.Value.Countries, f[6]) ?? f[6],
            Name(Codes.Value.Countries, f[3]) ?? f[3], SiteOf(f[7], f[3]));
    }

    private static string? Name(Dictionary<string, string> table, string code) => table.GetValueOrDefault(code.Trim());

    private static bool ParseTimes(string field, out int start, out int stop)
    {
        (start, stop) = (0, 1440);
        field = field.Trim();
        if (field == "") return true;
        var parts = field.Split('-');
        if (parts.Length != 2 || Minutes(parts[0]) is not { } a || Minutes(parts[1]) is not { } b) return false;
        (start, stop) = (a, b == 0 ? 1440 : b);
        return true;
    }

    private static int? Minutes(string hhmm) =>
        hhmm.Length == 4 && int.TryParse(hhmm, out var v) && v / 100 <= 24 && v % 100 < 60 ? v / 100 * 60 + v % 100 : null;

    private static EibiSeasonRule SeasonOf(string persistence, string from, string stopField)
    {
        var code = int.TryParse(persistence, out var p) ? (p >= 90 ? p - 90 : p) : -1;
        if (code == 8) return EibiSeasonRule.Inactive;
        var (a, b) = (DateOf(from), DateOf(stopField.Split('[')[0]));
        // On codes 1 to 5 a single date only notes when something started.
        if (code is >= 1 and <= 5 && a.HasValue != b.HasValue) (a, b) = (null, null);
        return new EibiSeasonRule(code != 5, code != 4, a, b);
    }

    private static (int Month, int Day)? DateOf(string ddmm)
    {
        ddmm = ddmm.Trim();
        if (ddmm.Length != 4 || !int.TryParse(ddmm, out var v)) return null;
        // ddmm, or mmdd when only that reads as a date.
        static bool Real(int month, int day) => month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(2000, month);
        var (first, second) = (v / 100, v % 100);
        if (Real(second, first)) return (second, first);
        return Real(first, second) ? (first, second) : null;
    }

    private static int? DayIndex(string lower) =>
        lower.Length < 2 ? null : Array.FindIndex(DayNames, n => n.StartsWith(lower, StringComparison.Ordinal)) is >= 0 and var i ? i : null;

    private static List<int>? DayRun(string lower)
    {
        if (DayIndex(lower) is { } one) return [one];
        if (lower.Length < 4 || lower.Length % 2 != 0) return null;
        var run = new List<int>();
        for (var i = 0; i < lower.Length; i += 2)
        {
            if (DayIndex(lower.Substring(i, 2)) is not { } d) return null;
            run.Add(d);
        }
        return run;
    }

    private static int MaskOf(IEnumerable<int> days) => days.Aggregate(0, (m, d) => m | 1 << d);

    private static DayRule? MonthForm(string field)
    {
        var lower = field.ToLowerInvariant();
        if (lower == "") return null;
        if (lower[0] is >= '1' and <= '5' && DayRun(lower[1..].TrimStart('.')) is { Count: <= 2 } nth)
        {
            var n = lower[0] - '0';
            return new DayRule(MaskOf(nth), field, d => (d.Day - 1) / 7 + 1 == n || nth.Count == 2);
        }
        if (lower.StartsWith("last", StringComparison.Ordinal))
        {
            var rest = lower[4..].TrimStart('.');
            int? anchor = int.TryParse(rest, out var v) && v is >= 1 and <= 7 ? v - 1 : DayIndex(rest);
            if (anchor is { } a) return new DayRule(1 << a, field, d => d.Day + 7 > DateTime.DaysInMonth(d.Year, d.Month));
        }
        if (lower.StartsWith("mf-", StringComparison.Ordinal) && int.TryParse(lower[3..], out var upTo) && upTo is >= 1 and <= 31)
            return new DayRule(0b001_1111, field, d => d.Day <= upTo);
        var digits = lower.TakeWhile(char.IsAsciiDigit).Count();
        if (digits is 1 or 2 && Array.IndexOf(MonthNames, lower[digits..].TrimStart('.', ' ')) is >= 0 and var month
            && int.TryParse(lower[..digits], out var day))
            return new DayRule(Daily, field, d => d.Month == month + 1 && d.Day == day);
        if (lower.StartsWith("alt", StringComparison.Ordinal) && DayRun(lower[3..]) is { } alt)
            return new DayRule(MaskOf(alt), field);
        return null;
    }

    private static (DayRule Rule, bool Alternative) ParseDays(string field)
    {
        if (MonthForm(field) is { } form) return (form, false);
        var tokens = new List<int>(); // a day, or -1 for a dash
        var (irregular, daily, alternative) = (false, false, false);
        for (var i = 0; i < field.Length;)
        {
            var c = field[i];
            if (char.IsAsciiDigit(c))
            {
                if (c is >= '1' and <= '7') tokens.Add(c - '1');
                i++;
            }
            else if (char.IsLetter(c))
            {
                var start = i;
                while (i < field.Length && char.IsLetter(field[i])) i++;
                var word = field[start..i].ToLowerInvariant();
                if (word is "irr" or "irreg" or "irregular") irregular = true;
                else if (word == "alt") alternative = true;
                else if (word == "daily") daily = true;
                else if (DayRun(word) is { } run) tokens.AddRange(run);
            }
            else
            {
                if (c == '-') tokens.Add(-1);
                i++;
            }
        }
        var mask = 0;
        for (var k = 0; k < tokens.Count; k++)
        {
            if (tokens[k] < 0) continue;
            if (k + 2 < tokens.Count && tokens[k + 1] < 0 && tokens[k + 2] >= 0)
            {
                for (var d = tokens[k]; ; d = (d + 1) % 7)
                {
                    mask |= 1 << d;
                    if (d == tokens[k + 2]) break;
                }
                k += 2;
            }
            else mask |= 1 << tokens[k];
        }
        if (daily || mask == 0) mask = Daily;
        var text = field == "" ? "daily" : irregular ? "irregular" : field;
        return (new DayRule(mask, text, null, irregular), alternative);
    }

    private static string LanguageNames(string field)
    {
        field = field.Trim();
        if (Name(Codes.Value.Languages, field) is { } name) return name;
        if (!field.Contains(',') && !field.Contains('/')) return field;
        return string.Join(", ", field.Split(',', '/').Select(c => c.Trim()).Where(c => c != "")
            .Select(c => Name(Codes.Value.Languages, c) ?? c));
    }

    private static string SiteOf(string remarks, string home)
    {
        var code = remarks.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var sites = Codes.Value.Sites;
        string country, mark;
        if (code.StartsWith('/'))
        {
            var abroad = code[1..].Split('-', 2);
            (country, mark) = (abroad[0], abroad.Length > 1 ? abroad[1] : "");
        }
        else if (code.IndexOf('-') == 3) (country, mark) = (code[..3], code[4..]);
        else if (code.Length == 3 && sites.ContainsKey($"{code}|")) (country, mark) = (code, "");
        else (country, mark) = (home, code);
        if (sites.TryGetValue($"{country}|{mark}", out var site))
            return country != home && Name(Codes.Value.Countries, country) is { } where ? $"{site} ({where})" : site;
        return sites.TryGetValue($"{home}|{code}", out var own) ? own : code;
    }
}
