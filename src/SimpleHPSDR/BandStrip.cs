using Zeus.Contracts;

namespace SimpleHPSDR;

/// <summary>What a span on the band strip means.</summary>
public enum StripKind
{
    /// <summary>Inside the band plan: you may transmit here (green).</summary>
    Allowed,
    /// <summary>An amateur band the plan keeps for other licence classes (red).</summary>
    NotAllowed,
    /// <summary>Not amateur: broadcast bands, CB, time signals (grey).</summary>
    Other,
}

/// <summary>One coloured span on the strip at the top of the spectrum. Short is
/// the label used when the full one doesn't fit.</summary>
public sealed record StripSpan(long LowHz, long HighHz, string Label, string Short, StripKind Kind);

/// <summary>
/// The band strip: the chosen band plan's segments (Config > Station > Band plan),
/// green where it lets you transmit and red where it keeps the band for another
/// licence class, over grey spans for the broadcast bands and other services.
/// The plans stop at 54 MHz, so for transverters the amateur bands above that come
/// from a short list per ITU region, without licence detail.
/// </summary>
public static class BandStrip
{
    // Not amateur. Drawn first, so an amateur segment that overlaps one wins.
    private static readonly (long Lo, long Hi, string Label, string Short)[] Others =
    [
        (148_500, 283_500, "LW broadcast", "LW"),
        (530_000, 1_700_000, "MW broadcast", "MW"),
        (2_300_000, 2_495_000, "120m broadcast", "120m"),
        (2_495_000, 2_505_000, "WWV", "WWV"),
        (3_200_000, 3_400_000, "90m broadcast", "90m"),
        (3_900_000, 4_000_000, "75m broadcast", "75m"),
        (4_750_000, 5_060_000, "60m broadcast", "60m"),
        (4_995_000, 5_005_000, "WWV", "WWV"),
        (5_900_000, 6_200_000, "49m broadcast", "49m"),
        (7_200_000, 7_450_000, "41m broadcast", "41m"),
        (9_400_000, 9_900_000, "31m broadcast", "31m"),
        (9_995_000, 10_005_000, "WWV", "WWV"),
        (11_600_000, 12_100_000, "25m broadcast", "25m"),
        (13_570_000, 13_870_000, "22m broadcast", "22m"),
        (14_995_000, 15_005_000, "WWV", "WWV"),
        (15_100_000, 15_800_000, "19m broadcast", "19m"),
        (17_480_000, 17_900_000, "16m broadcast", "16m"),
        (18_900_000, 19_020_000, "15m broadcast", "15m"),
        (19_995_000, 20_005_000, "WWV", "WWV"),
        (21_450_000, 21_850_000, "13m broadcast", "13m"),
        (24_995_000, 25_005_000, "WWV", "WWV"),
        (25_670_000, 26_100_000, "11m broadcast", "11m"),
        (26_965_000, 27_405_000, "CB", "CB"),
    ];

    // Amateur bands above the plans' 54 MHz, for transverters.
    private static readonly (long Lo, long Hi, string Label)[] VhfRegion2 =
    [
        (144_000_000, 148_000_000, "2M"), (222_000_000, 225_000_000, "1.25M"),
        (420_000_000, 450_000_000, "70CM"), (902_000_000, 928_000_000, "33CM"),
        (1_240_000_000, 1_300_000_000, "23CM"), (2_300_000_000, 2_310_000_000, "13CM"),
        (2_390_000_000, 2_450_000_000, "13CM"), (3_300_000_000, 3_500_000_000, "9CM"),
        (5_650_000_000, 5_925_000_000, "6CM"), (10_000_000_000, 10_500_000_000, "3CM"),
        (24_000_000_000, 24_250_000_000, "1.2CM"),
    ];
    private static readonly (long Lo, long Hi, string Label)[] VhfRegion1 =
    [
        (70_000_000, 70_500_000, "4M"),
        (144_000_000, 146_000_000, "2M"), (430_000_000, 440_000_000, "70CM"),
        (1_240_000_000, 1_300_000_000, "23CM"), (2_300_000_000, 2_450_000_000, "13CM"),
        (3_400_000_000, 3_410_000_000, "9CM"), (5_650_000_000, 5_850_000_000, "6CM"),
        (10_000_000_000, 10_500_000_000, "3CM"), (24_000_000_000, 24_250_000_000, "1.2CM"),
    ];
    private static readonly (long Lo, long Hi, string Label)[] VhfRegion3 =
    [
        (144_000_000, 148_000_000, "2M"), (430_000_000, 440_000_000, "70CM"),
        (1_240_000_000, 1_300_000_000, "23CM"), (2_300_000_000, 2_450_000_000, "13CM"),
        (5_650_000_000, 5_850_000_000, "6CM"), (10_000_000_000, 10_500_000_000, "3CM"),
        (24_000_000_000, 24_250_000_000, "1.2CM"),
    ];

    public static IReadOnlyList<StripSpan> Build(string regionId, IEnumerable<BandSegment> plan)
    {
        var spans = new List<StripSpan>();
        foreach (var (lo, hi, label, shortLabel) in Others)
            spans.Add(new(lo, hi, label, shortLabel, StripKind.Other));

        long top = 0;
        foreach (var s in plan.OrderBy(s => s.LowHz))
        {
            top = Math.Max(top, s.HighHz);
            // The US plans mark CB as reserved; it's in the grey list already.
            if (s.Allocation == BandAllocation.Reserved
                && s.Label.Contains("no amateur", StringComparison.OrdinalIgnoreCase)) continue;
            var kind = s.Allocation switch
            {
                BandAllocation.Amateur => StripKind.Allowed,
                BandAllocation.Reserved => StripKind.NotAllowed,
                _ => StripKind.Other,
            };
            string label = s.Label.Replace(" — ", " · ");
            spans.Add(new(s.LowHz, s.HighHz, label, ShortLabel(label), kind));
        }

        var vhf = regionId switch
        {
            "IARU_R1" or "G" or "EI" => VhfRegion1,
            "IARU_R3" => VhfRegion3,
            _ => VhfRegion2,
        };
        foreach (var (lo, hi, label) in vhf)
            if (lo >= top) spans.Add(new(lo, hi, label, label, StripKind.Allowed));
        return spans;
    }

    // "40M CW/Digital" -> "40M": the band name, the first word.
    private static string ShortLabel(string label)
    {
        int space = label.IndexOf(' ');
        return space > 0 ? label[..space] : label;
    }
}
