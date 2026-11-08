using System.Text.Json;
using SimpleHPSDR.Dsp;
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
    /// <summary>CW window decoder: "Engine" or "DeepCW".</summary>
    public string CwDecoder { get; set; } = "Engine";
    /// <summary>Put back word spaces the CW decoder missed.</summary>
    public bool CwWordForming { get; set; } = true;
    /// <summary>The release whose update note was closed with ✕.</summary>
    public string? UpdateDismissedVersion { get; set; }
    /// <summary>A click on the panadapter tunes to the nearest whole step.</summary>
    public bool SnapClickToStep { get; set; } = true;
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
    /// <summary>The low starting monitor level has been applied once.</summary>
    public bool MonitorLevelDefaulted { get; set; }
    /// <summary>Panadapter trace range (dB).</summary>
    public double SpecMinDb { get; set; } = -130;
    public double SpecMaxDb { get; set; } = -60;
    /// <summary>Spec Min/Max follow the noise floor.</summary>
    public bool AutoSpectrum { get; set; } = true;
    /// <summary>Spectrum share of the panadapter height (rest is waterfall).</summary>
    public double PanFraction { get; set; } = 0.38;
    /// <summary>Slice B in its own panadapter (true) or on slice A's.</summary>
    public bool SliceBOwnPanadapter { get; set; } = true;
    /// <summary>The band plan has been chosen at first start; the engine keeps it after that.</summary>
    public bool BandPlanDefaulted { get; set; }
    /// <summary>No band plan restrictions at all. The engine still holds a
    /// plan, which PA calibration uses for its test frequencies.</summary>
    public bool GeneralCoverage { get; set; }
    /// <summary>SWR levels for MOX: an amber warning, and where the app stops
    /// transmit. The engine still stops MOX at 2.5:1 by itself.</summary>
    public double SwrWarnAt { get; set; } = 2.0;
    public double SwrStopAt { get; set; } = SwrWatch.EngineLimit;
    /// <summary>Outside the band plan: block transmit, or only warn.</summary>
    public bool BandPlanBlocks { get; set; }
    /// <summary>The parametric TX EQ; the engine holds only its ten-point result.</summary>
    public ParametricEqSettings ParametricEq { get; set; } = ParametricEqSettings.Default;
    /// <summary>Receive audio goes to the radio's speaker jack, not the computer.</summary>
    public bool AudioToRadio { get; set; }
    /// <summary>The real mic gain while a CW keyboard hold has it muted; set back on
    /// the next connect if the app stops mid-hold.</summary>
    public int? CwHeldMicDb { get; set; }
    /// <summary>Mini window size, kept between uses.</summary>
    public double MiniWidth { get; set; } = 440;
    public double MiniHeight { get; set; } = 250;
    /// <summary>Mini window position; null until it has been used.</summary>
    public int? MiniX { get; set; }
    public int? MiniY { get; set; }
    /// <summary>VOX: mic level that keys, and how long it holds after you stop.</summary>
    public double VoxThresholdDb { get; set; } = -30;
    public double VoxDelayMs { get; set; } = 500;
    /// <summary>Correction to the PWR display from an external meter, in dB.</summary>
    public double PowerMeterTrimDb { get; set; }
    /// <summary>ID timer: a reminder on the even ten minutes while connected.</summary>
    public bool IdTimerOn { get; set; }
    /// <summary>A .wav to play for it, or null for the built-in beeps.</summary>
    public string? IdTimerWav { get; set; }
    /// <summary>ID timer sound level, 0 to 100.</summary>
    public int IdTimerVolume { get; set; } = 30;
    /// <summary>While transmitting, the waterfall's colours span this many dB
    /// below the signal's own peak, so the shape of the transmit audio shows
    /// instead of one saturated block. 0 keeps the receive range.</summary>
    public int TxWaterfallRangeDb { get; set; } = 70;
    /// <summary>System, Dark or Light.</summary>
    public string Theme { get; set; } = "System";
    /// <summary>The default SSB filter buttons have been written to the engine.</summary>
    public bool FilterDefaultsApplied { get; set; }
    /// <summary>The same for the AM and SAM buttons.</summary>
    public int AmFilterDefaultsVersion { get; set; }
    /// <summary>Zoom level the Zoom button goes to (2-32).</summary>
    public int ZoomOnLevel { get; set; } = 4;
    /// <summary>Waterfall POP contrast.</summary>
    public bool WaterfallPop { get; set; }
    /// <summary>The TX filter's cuts follow the transmitting slice's RX filter.</summary>
    public bool TxFilterFollowsRx { get; set; } = true;
    /// <summary>The TX cuts of the loaded profile or last set in Config. The engine keeps
    /// separate cuts for FM and DSB, so the app carries these into every mode.</summary>
    public int? TxLowCutHz { get; set; }
    public int? TxHighCutHz { get; set; }
    /// <summary>PureSignal's feedback source, External when true. It belongs to the
    /// station, not to a band, mode or TX profile; null until the first connect.</summary>
    public bool? PsExternalFeedback { get; set; }
    /// <summary>SWL guide: column to sort by, direction, and language shown ("" for all).</summary>
    public string GuideSort { get; set; } = "Status";
    public bool GuideSortAscending { get; set; } = true;
    public string GuideLanguage { get; set; } = "";
    /// <summary>Last frequency used on each band, keyed by band name.</summary>
    public Dictionary<string, long> BandMemory { get; set; } = [];
    /// <summary>Settings per band and mode, keyed "40m|DIGU".</summary>
    public Dictionary<string, ModeMemory> ModeMemories { get; set; } = [];
    /// <summary>The mode last used on each band.</summary>
    public Dictionary<string, int> BandLastMode { get; set; } = [];

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
        // Written beside the real file and moved over it, so a crash can't
        // leave half a settings file. Saves can come from more than one thread.
        lock (SaveGate)
        {
            try
            {
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static readonly object SaveGate = new();
}

/// <summary>What comes back on returning to a band and mode. Enums as numbers.</summary>
public sealed record ModeMemory(
    long Hz, int FilterLowHz, int FilterHighHz,
    bool AgcAuto, int AgcMode, double AgcTopDb, double AgcFixedDb,
    double DrivePct, double TunePct, double MicGainDb, int NrMode, int NbMode);
