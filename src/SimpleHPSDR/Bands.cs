namespace SimpleHPSDR;

public sealed record Band(string Name, long LowHz, long HighHz, long DefaultHz)
{
    public bool Contains(long hz) => hz >= LowHz && hz <= HighHz;

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

    public static Band? Find(long hz) => All.FirstOrDefault(b => b.Contains(hz));
}
