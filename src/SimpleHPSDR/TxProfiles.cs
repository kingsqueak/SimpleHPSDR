using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleHPSDR.Dsp;
using SimpleHPSDR.Engine;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR;

/// <summary>The TX Audio page of Config, saved under a name. Nothing else: older
/// profiles' PureSignal settings are ignored on load and dropped on the next save.</summary>
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
    ParametricEqSettings? ParametricEq = null, // null: the graphic EQ
    bool? TxFilterFollowsRx = null)            // null in older profiles: leave as is
{
    public const string DefaultName = "Default";

    /// <summary>PTT release delay to start with: covers the ~210 ms the TX
    /// processing holds with CFC at FFT 16384, with margin (user-tested).</summary>
    public const int DefaultReleaseDelayMs = 275;

    /// <summary>The starting point, from DefaultTxProfile.json: the operator's tested setup.</summary>
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

    /// <summary>File extension for a profile shared between stations (plain JSON inside).</summary>
    public const string ShareExtension = "shtx";

    /// <summary>Write a profile to share with another station. Only TX audio
    /// settings are in it: nothing about the radio, its power or calibration.</summary>
    public static async Task ExportAsync(string name, Stream stream)
    {
        var profile = Get(name) ?? throw new InvalidOperationException($"Profile \"{name}\" could not be read.");
        await JsonSerializer.SerializeAsync(stream, profile, Json);
    }

    /// <summary>Add a profile from another station under a name that's free
    /// ("Default" and names in use get " (2)", " (3)"…). Returns the name.</summary>
    public static async Task<string> ImportAsync(Stream stream)
    {
        TxAudioProfile? p;
        try { p = await JsonSerializer.DeserializeAsync<TxAudioProfile>(stream, Json); }
        catch (JsonException) { p = null; }
        if (p is null || string.IsNullOrWhiteSpace(p.Name) || p.Leveling is null || p.Equalizer is null
            || p.Cfc is null || p.NoiseGate is null || p.PhaseRotator is null)
            throw new InvalidDataException("That file isn't a SimpleHPSDR TX profile, or it's damaged.");
        string baseName = p.Name.Trim(), name = baseName;
        for (int n = 2; IsDefault(name) || File.Exists(FileFor(name)); n++) name = $"{baseName} ({n})";
        Save(p with { Name = name });
        return name;
    }

    private static TxAudioProfile? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<TxAudioProfile>(File.ReadAllText(path), Json) : null; }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }
}
