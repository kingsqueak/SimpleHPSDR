using SimpleHPSDR.Contracts;

namespace SimpleHPSDR;

/// <summary>
/// Ready-made SimpleStudio chains, from Patch Prime's built-in voice chains
/// (PatchPrimeMac, PPShared/Presets.swift). The effects SimpleStudio doesn't
/// have (noise suppression, ducker, delay) are left out. Parameters a preset
/// doesn't name take the catalogue's default.
/// </summary>
public static class StudioPresets
{
    private const double Bell = 0, LowShelf = 1, HighShelf = 2, LowCut = 3;

    public sealed record Preset(string Name, IReadOnlyList<(string Type, Dictionary<string, double> Params)> Effects);

    private static (string, Dictionary<string, double>) Eq(params (int Band, double Type, double Hz, double GainDb, double Q)[] bands)
    {
        var p = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (n, type, hz, gain, q) in bands)
        {
            p[$"b{n}On"] = 1;
            p[$"b{n}Type"] = type;
            p[$"b{n}Freq"] = hz;
            p[$"b{n}Gain"] = gain;
            p[$"b{n}Q"] = q;
        }
        return (StudioCatalogue.Eq, p);
    }

    private static (string, Dictionary<string, double>) Fx(string type, params (string Name, double Value)[] values) =>
        (type, values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal));

    private static (string, Dictionary<string, double>) Limiter() =>
        Fx(StudioCatalogue.Limiter, ("ceiling", -1), ("truePeak", 1));

    public static IReadOnlyList<Preset> All { get; } =
    [
        // Spoken word for a podcast: cleaned up, even, and at -16 LUFS.
        new("Podcast voice",
        [
            Eq((1, LowCut, 80, 0, 0.707), (2, Bell, 250, -2, 1.0), (4, Bell, 3000, 2, 0.8), (6, HighShelf, 12000, 1.5, 0.707)),
            Fx(StudioCatalogue.Expander, ("threshold", -50), ("ratio", 2), ("range", 15)),
            Fx(StudioCatalogue.Compressor, ("amount", 50)),
            Fx(StudioCatalogue.DeEsser),
            Fx(StudioCatalogue.Leveler, ("target", -16)),
            Limiter(),
        ]),
        // Present and steady, as for a mic over a stream.
        new("Streaming voice",
        [
            Eq((1, LowCut, 90, 0, 0.707), (4, Bell, 3500, 2.5, 0.8)),
            Fx(StudioCatalogue.Compressor, ("amount", 60)),
            Fx(StudioCatalogue.DeEsser),
            Limiter(),
        ]),
        // Fans, traffic, a keyboard: a gate first.
        new("Noisy room",
        [
            Fx(StudioCatalogue.Gate, ("threshold", -45), ("range", 30)),
            Eq((1, LowCut, 100, 0, 0.707), (2, Bell, 400, -2, 1.0)),
            Fx(StudioCatalogue.Compressor, ("amount", 40)),
            Fx(StudioCatalogue.DeEsser),
            Limiter(),
        ]),
        // Close and big, like a radio presenter: more low end, presence and compression.
        new("Radio voice",
        [
            Eq((1, LowCut, 70, 0, 0.707), (2, LowShelf, 150, 3, 0.707), (4, Bell, 3000, 3, 0.7), (6, HighShelf, 10000, 2, 0.707)),
            Fx(StudioCatalogue.Expander, ("threshold", -48), ("ratio", 2), ("range", 15)),
            Fx(StudioCatalogue.Compressor, ("amount", 75)),
            Fx(StudioCatalogue.DeEsser, ("threshold", -32)),
            Fx(StudioCatalogue.Leveler, ("target", -16), ("speed", 3)),
            Limiter(),
        ]),
        // Just the basics, lightly: rumble out, a little compression, a ceiling.
        new("Natural voice",
        [
            Eq((1, LowCut, 80, 0, 0.707)),
            Fx(StudioCatalogue.Compressor, ("amount", 25)),
            Fx(StudioCatalogue.DeEsser, ("threshold", -28), ("range", 6)),
            Limiter(),
        ]),
    ];

    public static Preset? Find(string name) => All.FirstOrDefault(p => p.Name == name);
}
