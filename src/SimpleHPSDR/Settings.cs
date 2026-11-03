using System.Text.Json;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR;

/// <summary>Small JSON settings file in the app data directory.</summary>
public sealed class Settings
{
    public string? LastRadioIp { get; set; }
    public string? LastRadioMac { get; set; }
    public bool LastRadioP2 { get; set; }
    public byte? LastRadioBoardId { get; set; }
    public bool AutoConnect { get; set; }
    public int SampleRate { get; set; } = 192_000;
    public int TuneStepHz { get; set; } = 100;
    /// <summary>Last frequency used on each band, keyed by band name.</summary>
    public Dictionary<string, long> BandMemory { get; set; } = [];

    private static string FilePath => Path.Combine(EngineHost.DataDir, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (JsonException) { }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options)); }
        catch (IOException) { }
    }
}
