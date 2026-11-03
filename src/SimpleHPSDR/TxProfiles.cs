using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleHPSDR.Engine;
using Zeus.Contracts;

namespace SimpleHPSDR;

/// <summary>Everything on the TX Audio page, saved together under a name.</summary>
public sealed record TxAudioProfile(
    string Name,
    int LowCutHz,
    int HighCutHz,
    TxLevelingConfig Leveling,
    double LevelerMaxGainDb,
    LegacyTxEqConfig Equalizer,
    CfcConfig Cfc,
    DexpConfig NoiseGate,
    TxPhaseRotatorConfig PhaseRotator,
    int? ReleaseDelayMs = null)   // null in profiles saved before it existed: leave as is
{
    public const string DefaultName = "Default";

    /// <summary>PTT release delay to start with: covers the ~210 ms the TX
    /// processing holds with CFC at FFT 16384, with margin (user-tested).</summary>
    public const int DefaultReleaseDelayMs = 275;

    /// <summary>The starting point: 80-3000 Hz, leveler at 3 dB, CFC on with
    /// 3 dB pre-compression and flat bands; everything else the engine's
    /// defaults (compressor, EQ, noise gate and phase rotator off).</summary>
    public static TxAudioProfile Default { get; } = new(
        DefaultName, 80, 3000,
        new TxLevelingConfig() with { LevelerEnabled = true }, 3.0,
        LegacyTxEqConfig.Default,
        CfcConfig.Default with { Enabled = true, PreCompDb = 3.0 },
        DexpConfig.Default, new TxPhaseRotatorConfig(),
        DefaultReleaseDelayMs);
}

/// <summary>
/// TX audio profiles as JSON files in the app data folder (tx-profiles/).
/// The station engine has no profile store of its own, so these belong to
/// SimpleHPSDR; "Default" is built in and can't be overwritten.
/// </summary>
public static class TxProfileStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string Dir
    {
        get
        {
            var dir = Path.Combine(EngineHost.DataDir, "tx-profiles");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string FileFor(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return Path.Combine(Dir, safe + ".json");
    }

    /// <summary>Default first, then saved profiles by name.</summary>
    public static List<string> Names()
    {
        var saved = Directory.EnumerateFiles(Dir, "*.json")
            .Select(Load)
            .OfType<TxAudioProfile>()
            .Select(p => p.Name)
            .Where(n => !IsDefault(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        return [TxAudioProfile.DefaultName, .. saved];
    }

    public static bool IsDefault(string name) =>
        string.Equals(name, TxAudioProfile.DefaultName, StringComparison.OrdinalIgnoreCase);

    public static TxAudioProfile? Get(string name) =>
        IsDefault(name) ? TxAudioProfile.Default : Load(FileFor(name));

    public static void Save(TxAudioProfile profile)
    {
        if (IsDefault(profile.Name)) throw new InvalidOperationException("The Default profile can't be overwritten.");
        File.WriteAllText(FileFor(profile.Name), JsonSerializer.Serialize(profile, Json));
    }

    public static void Delete(string name)
    {
        if (!IsDefault(name)) File.Delete(FileFor(name));
    }

    private static TxAudioProfile? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<TxAudioProfile>(File.ReadAllText(path), Json) : null; }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }
}
