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
    /// <summary>Keep PureSignal's HW peak just under the signal's measured
    /// peak: lower it when calibration stalls, raise it when the signal
    /// clearly exceeds it.</summary>
    public bool AutoHwPeak { get; set; } = true;
    /// <summary>The TX audio profile last loaded or saved.</summary>
    public string? LastTxProfile { get; set; }
    /// <summary>Set once the engine's PTT release delay has been given the
    /// app's default, so a deliberate 0 later is left alone.</summary>
    public bool ReleaseDelayDefaulted { get; set; }
    /// <summary>MON and MON TX: the engine forgets them on restart.</summary>
    public bool MonitorPreview { get; set; }
    public bool MonitorOnTransmit { get; set; }
    /// <summary>Panadapter trace range (dB).</summary>
    public double SpecMinDb { get; set; } = -130;
    public double SpecMaxDb { get; set; } = -60;
    /// <summary>Spec Min/Max follow the noise floor.</summary>
    public bool AutoSpectrum { get; set; } = true;
    /// <summary>Spectrum share of the panadapter height (rest is waterfall).</summary>
    public double PanFraction { get; set; } = 0.38;
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
