using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleHPSDR.Dsp;
using SimpleHPSDR.Engine;
using Zeus.Contracts;

namespace SimpleHPSDR;

/// <summary>The PureSignal settings a profile keeps. The learned correction
/// isn't one of them: that belongs to the moment it was measured.</summary>
public sealed record TxPureSignalSettings(
    bool ExternalFeedback, bool AutoAttenuate, int FeedbackAttenDb,
    double MoxDelaySec, double LoopDelaySec, double AmpDelayNs, double HwPeak);

/// <summary>The TX Audio page and PureSignal settings, saved together under a name.</summary>
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
    int? ReleaseDelayMs = null,   // null in profiles saved before it existed: leave as is
    TxPureSignalSettings? PureSignal = null,   // likewise
    ParametricEqSettings? ParametricEq = null) // null: the graphic EQ
{
    public const string DefaultName = "Default";

    /// <summary>PTT release delay to start with: covers the ~210 ms the TX
    /// processing holds with CFC at FFT 16384, with margin (user-tested).</summary>
    public const int DefaultReleaseDelayMs = 275;

    /// <summary>The starting point, from DefaultTxProfile.json: the operator's
    /// tested setup. It holds no PureSignal settings, as those depend on the
    /// station, so loading it leaves them alone.</summary>
    public static TxAudioProfile Default => TxProfileStore.BuiltInDefault;
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

    /// <summary>The built-in profile.</summary>
    public static TxAudioProfile BuiltInDefault { get; } = ReadBuiltIn();

    private static TxAudioProfile ReadBuiltIn()
    {
        using var stream = typeof(TxProfileStore).Assembly.GetManifestResourceStream("DefaultTxProfile.json")!;
        return JsonSerializer.Deserialize<TxAudioProfile>(stream, Json)!;
    }

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
