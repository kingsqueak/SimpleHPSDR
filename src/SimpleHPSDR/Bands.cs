using SimpleHPSDR.Contracts;

namespace SimpleHPSDR;

/// <summary>A band button. Transverter bands come from Config > Station > Transverters
/// and are tuned in RF terms (144.2 MHz); the engine converts to the radio's IF.</summary>
public sealed record Band(string Name, long LowHz, long HighHz, long DefaultHz, string? XvtrLabel = null, int XvtrId = -1)
{
    public bool Contains(long hz) => hz >= LowHz && hz <= HighHz;
    public bool Transverter => XvtrId >= 0;
    /// <summary>The button's text: "20", or the transverter's own, such as "2m".</summary>
    public string Label => XvtrLabel ?? Name;

    // Region 2 (Americas) amateur allocations.
    public static readonly Band[] All =
    [
        new("160", 1_800_000, 2_000_000, 1_840_000),
        new("80", 3_500_000, 4_000_000, 3_573_000),
        new("60", 5_330_000, 5_410_000, 5_357_000),
        new("40", 7_000_000, 7_300_000, 7_074_000),
        new("30", 10_100_000, 10_150_000, 10_136_000),
        new("20", 14_000_000, 14_350_000, 14_074_000),
        new("17", 18_068_000, 18_168_000, 18_100_000),
        new("15", 21_000_000, 21_450_000, 21_074_000),
        new("12", 24_890_000, 24_990_000, 24_915_000),
        new("10", 28_000_000, 29_700_000, 28_074_000),
        new("6", 50_000_000, 54_000_000, 50_313_000),
    ];

    /// <summary>The radio's own tuning range.</summary>
    public const long MinHfHz = 10_000, MaxHfHz = 60_000_000;

    /// <summary>The enabled transverter bands, while transverters are on.</summary>
    public static IReadOnlyList<Band> Transverters { get; private set; } = [];

    public static IReadOnlyList<Band> SetTransverters(TransverterSettingsDto? t) =>
        Transverters = t is { Enabled: true }
            ? [.. (t.Bands ?? []).Where(b => b.Enabled && b.EndFrequencyHz > b.BeginFrequencyHz)
                .Select(b => new Band($"XVTR{b.Id}", b.BeginFrequencyHz, b.EndFrequencyHz, b.BeginFrequencyHz,
                    b.ButtonText.Trim() is { Length: > 0 } text ? text : $"{b.BeginFrequencyHz / 1_000_000} MHz", b.Id))]
            : [];

    public static Band? Find(long hz) =>
        Transverters.FirstOrDefault(b => b.Contains(hz)) ?? All.FirstOrDefault(b => b.Contains(hz));

    /// <summary>Within the radio's range, or in an enabled transverter band.</summary>
    public static bool Tunable(long hz) =>
        hz is >= MinHfHz and <= MaxHfHz || Transverters.Any(b => b.Contains(hz));

    /// <summary>A typed number as a frequency: below 100 it's MHz, otherwise kHz, but a
    /// number that lands in a transverter band as MHz is MHz (144.2, 1296.2).</summary>
    public static long? FromTyped(double v, double? unitHz = null)
    {
        if (v <= 0) return null;
        if (unitHz is { } unit) return (long)Math.Round(v * unit);
        long mhz = (long)Math.Round(v * 1e6);
        if (Transverters.Any(b => b.Contains(mhz))) return mhz;
        return v < 100 ? mhz : (long)Math.Round(v * 1e3);
    }

    public static string RangeHint => Transverters.Count == 0
        ? "Enter a frequency from 0.01 to 60 MHz."
        : "Enter a frequency from 0.01 to 60 MHz, or in a transverter band (" +
          string.Join(", ", Transverters.Select(b => $"{b.Label} {b.LowHz / 1e6:0.###}-{b.HighHz / 1e6:0.###} MHz")) + ").";
}
