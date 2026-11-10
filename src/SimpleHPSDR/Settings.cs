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
    /// <summary>Transverters on or off, the last time Config applied it. The engine
    /// keeps this per radio model; like Thetis we keep one switch for every radio
    /// and set it on each connect. Null until first set: leave the engine alone.</summary>
    public bool? TransverterOn { get; set; }
    /// <summary>Keep PureSignal's HW peak just under the signal's measured
    /// peak: lower it when calibration stalls, raise it when the signal
    /// clearly exceeds it.</summary>
    public bool AutoHwPeak { get; set; } = true;
    /// <summary>The guided PureSignal setup has been finished (or skipped), so it
    /// no longer opens by itself.</summary>
    public bool PsSetupDone { get; set; }
    /// <summary>Load the saved PureSignal correction each time a radio connects.
    /// Off by default: a loaded correction stays fixed (calcc "stay on") and
    /// PureSignal stops adapting until it's reset; on the air an old one gave
    /// worse IMD than none (2026-11-09). Replaces PsRestoreAtStart, which
    /// defaulted to on.</summary>
    public bool PsLoadCorrectionAtStart { get; set; }
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
    /// <summary>Auto AGC: how far above the noise floor the AGC threshold sits (Thetis default 20).</summary>
    public double AutoAgcOffsetDb { get; set; } = 20;
    /// <summary>Where the dial sits across the panadapter, -90 to +90 (% of half the span).</summary>
    public double DialPosition { get; set; }
    /// <summary>Slice B in its own panadapter (true) or on slice A's.</summary>
    public bool SliceBOwnPanadapter { get; set; } = true;

    /// <summary>Slice B's own panadapter beside A's rather than below it.</summary>
    public bool PanadaptersSideBySide { get; set; }
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
    /// <summary>Extra level for recordings transmitted from the recorder, dB.</summary>
    public double RecorderTxGainDb { get; set; }
    /// <summary>The recorder records the microphone (voice keyer messages) instead of the receiver.</summary>
    public bool RecorderFromMic { get; set; }
    /// <summary>The four voice keyer buttons right of REC: a label and a recording each.</summary>
    public List<VoiceKeyerSetting> VoiceKeyer { get; set; } = [];
    /// <summary>Show the voice keyer buttons over the waterfall on the touch layout.</summary>
    public bool VoiceKeyerOnTouch { get; set; }
    /// <summary>The ANAN G2's front panel through p2app (only on a G2 / G2-1K).</summary>
    public bool G2PanelOn { get; set; } = true;
    /// <summary>FreeDV RADE: shaping of the mic before the encoder (engine defaults).</summary>
    public bool RadeMicAgc { get; set; }
    public double RadeMicAgcTargetDb { get; set; } = -18;
    public bool RadeMicEq { get; set; }
    public double RadeMicBassDb { get; set; }
    public double RadeMicMidDb { get; set; }
    public double RadeMicTrebleDb { get; set; }
    /// <summary>FreeDV RADE squelch, mic gate and TX level (engine defaults).</summary>
    public bool RadeRxSquelch { get; set; } = true;
    public double RadeRxHoldMs { get; set; } = 250;
    public double RadeRxMinSnrDb { get; set; } = -10;
    public bool RadeMicGate { get; set; } = true;
    public double RadeMicGateOpenDb { get; set; } = 14;
    public double RadeMicGateHangMs { get; set; } = 250;
    public double RadeTxLevelDb { get; set; } = -6;
    public int G2PanelPort { get; set; } = 19090;
    /// <summary>Changes from the default panel map: "B4:47" (button 47 on the
    /// original G2 panel) or "E5:9" (encoder 9 on the G2 Ultra) to an action name.</summary>
    public Dictionary<string, string> G2PanelMap { get; set; } = [];
    /// <summary>While transmitting, the waterfall's colours span this many dB
    /// below the signal's own peak, so the shape of the transmit audio shows
    /// instead of one saturated block. 0 keeps the receive range.</summary>
    public int TxWaterfallRangeDb { get; set; } = 70;
    /// <summary>System, Dark or Light.</summary>
    public string Theme { get; set; } = "System";
    /// <summary>Main window layout: Automatic (touch on screens narrower
    /// than 1080), Desktop or Touch.</summary>
    public string Layout { get; set; } = "Automatic";
    /// <summary>SimpleHPSDR runs on an ANAN-G2's own computer (Linux arm64 only).</summary>
    public bool G2Computer { get; set; }
    /// <summary>Config > Colors: names from SpectrumLook's lists.</summary>
    public string SpecLineColor { get; set; } = "Blue";
    public string SpecFillColor { get; set; } = "Blue";
    public string SpecBackground { get; set; } = "Black";
    public string SpecBackgroundFade { get; set; } = "None";
    public string WaterfallPalette { get; set; } = "Classic";
    /// <summary>The band strip along the top of the spectrum.</summary>
    public bool BandStrip { get; set; } = true;
    /// <summary>How solid the band strip's colours are, 0.2 to 1.</summary>
    public double BandStripOpacity { get; set; } = 1.0;
    /// <summary>The default SSB filter buttons have been written to the engine.</summary>
    public bool FilterDefaultsApplied { get; set; }
    /// <summary>The same for the AM and SAM buttons.</summary>
    public int AmFilterDefaultsVersion { get; set; }
    /// <summary>Zoom level the Zoom button goes to (2-32).</summary>
    public int ZoomOnLevel { get; set; } = 4;
    /// <summary>The last zoom used (smooth, 1.25 to 32), for the Zoom button.</summary>
    public double ZoomOnFine { get; set; } = 4;
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
    /// <summary>The 16 Mem buttons, the same on every band; null = open.</summary>
    public MemorySlot?[] Memories { get; set; } = new MemorySlot?[16];

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

    /// <summary>
    /// Saves soon: half a second after the last change, so dragging a slider or
    /// the panadapter split writes the file once, not on every step. On the UI
    /// thread, where the settings are changed, so the write never races an edit.
    /// Quitting (and anything that reads the file) calls SaveNow / Flush.
    /// </summary>
    public void Save()
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            SaveNow();
            return;
        }
        _pending = this;
        _saveTimer ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(500),
            Avalonia.Threading.DispatcherPriority.Background, (_, _) => Flush());
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes any delayed save now.</summary>
    public static void Flush()
    {
        _saveTimer?.Stop();
        var s = _pending;
        _pending = null;
        s?.SaveNow();
    }

    /// <summary>Writes the file now. Beside the real file and moved over it,
    /// so a crash can't leave half a settings file.</summary>
    public void SaveNow()
    {
        if (ReferenceEquals(_pending, this)) _pending = null;
        lock (SaveGate)
        {
            try
            {
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
    }

    private static readonly object SaveGate = new();
    private static Settings? _pending;
    private static Avalonia.Threading.DispatcherTimer? _saveTimer;
}

/// <summary>What comes back on returning to a band and mode. Enums as numbers.</summary>
/// <summary>One Mem button: a frequency and its mode.</summary>
public sealed record MemorySlot(long Hz, int Mode);

public sealed record ModeMemory(
    long Hz, int FilterLowHz, int FilterHighHz,
    bool AgcAuto, int AgcMode, double AgcTopDb, double AgcFixedDb,
    double DrivePct, double TunePct, double MicGainDb, int NrMode, int NbMode);

/// <summary>One voice keyer button: its label and the recording's file name
/// in the recordings folder (null when nothing is assigned).</summary>
public sealed class VoiceKeyerSetting
{
    public string? Label { get; set; }
    public string? File { get; set; }
}
