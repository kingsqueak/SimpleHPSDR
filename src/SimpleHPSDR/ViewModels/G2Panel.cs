namespace SimpleHPSDR.ViewModels;

/// <summary>What a G2 front panel button does.</summary>
public enum PanelButtonAction
{
    None, Mox, Tune, TwoTone, Ctun, Lock, Mute, MuteA, MuteB,
    BandUp, BandDown, ModeUp, ModeDown, FilterWider, FilterNarrower, FilterDefault,
    AToB, BToA, SwapAB, Split, Rit, Xit, RitXitClear, Nb, Nr, Anf,
    Band160, Band80, Band60, Band40, Band30, Band20, Band17, Band15, Band12, Band10, Band6,
    Quit,
}

/// <summary>What a G2 front panel knob does.</summary>
public enum PanelKnobAction
{
    None, AfGain, AfA, AfB, AgcTop, Drive, MicGain, Rit, Xit, FilterHigh, FilterLow, Attenuation, TuneB,
}

/// <summary>One control on a panel: its code in p2app's messages, the words
/// printed on the panel, and what it does unless changed.</summary>
public sealed record PanelControl(int Code, string Label, string DefaultAction);

/// <summary>
/// The two G2 panels p2app reports (ZZZS product 4 and 5), with the codes it
/// sends and defaults taken from piHPSDR (dl1ycf/pihpsdr src/g2panel.c), so G2
/// owners find the layout they know. Silk-screen names as there.
/// </summary>
public static class G2Panels
{
    public const int Original = 4, Ultra = 5;

    public static string Name(int product) => product switch
    {
        Original => "original G2 panel",
        Ultra => "G2V2 / G2 Ultra panel",
        _ => $"panel type {product}",
    };

    public static IReadOnlyList<PanelControl> Buttons(int product) => product switch
    {
        Original =>
        [
            new(47, "MOX", nameof(PanelButtonAction.Mox)),
            new(50, "2TONE/TUNE", nameof(PanelButtonAction.Tune)),
            new(45, "CTUNE (below VFO)", nameof(PanelButtonAction.Ctun)),
            new(44, "LOCK (below VFO)", nameof(PanelButtonAction.Lock)),
            new(31, "Mode+", nameof(PanelButtonAction.ModeUp)),
            new(32, "Fil+", nameof(PanelButtonAction.FilterWider)),
            new(30, "Band+", nameof(PanelButtonAction.BandUp)),
            new(34, "Mode-", nameof(PanelButtonAction.ModeDown)),
            new(35, "Fil-", nameof(PanelButtonAction.FilterNarrower)),
            new(33, "Band-", nameof(PanelButtonAction.BandDown)),
            new(36, "A>B", nameof(PanelButtonAction.AToB)),
            new(37, "B>A", nameof(PanelButtonAction.BToA)),
            new(38, "Split", nameof(PanelButtonAction.Split)),
            new(21, "FCN (hold)", nameof(PanelButtonAction.Quit)),
            new(42, "RIT", nameof(PanelButtonAction.Rit)),
            new(43, "XIT", nameof(PanelButtonAction.Xit)),
            new(1, "RX AF/AGC knob push", nameof(PanelButtonAction.Mute)),
            new(11, "MIC/DRIVE knob push", nameof(PanelButtonAction.RitXitClear)),
            new(5, "Multi 1 knob push", nameof(PanelButtonAction.FilterDefault)),
            new(9, "Multi 2 knob push", nameof(PanelButtonAction.RitXitClear)),
        ],
        Ultra =>
        [
            new(7, "MOX", nameof(PanelButtonAction.Mox)),
            new(6, "TUNE", nameof(PanelButtonAction.Tune)),
            new(5, "2TONE", nameof(PanelButtonAction.TwoTone)),
            new(8, "CTUNE", nameof(PanelButtonAction.Ctun)),
            new(9, "LOCK", nameof(PanelButtonAction.Lock)),
            new(10, "A/B", nameof(PanelButtonAction.SwapAB)),
            new(11, "RIT/XIT", nameof(PanelButtonAction.Rit)),
            new(14, "160/MODE+", nameof(PanelButtonAction.ModeUp)),
            new(15, "80/FIL+", nameof(PanelButtonAction.FilterWider)),
            new(16, "60/BAND+", nameof(PanelButtonAction.BandUp)),
            new(17, "40/MODE-", nameof(PanelButtonAction.ModeDown)),
            new(18, "30/FIL-", nameof(PanelButtonAction.FilterNarrower)),
            new(19, "20/BAND-", nameof(PanelButtonAction.BandDown)),
            new(20, "17/A>B", nameof(PanelButtonAction.AToB)),
            new(21, "15/B>A", nameof(PanelButtonAction.BToA)),
            new(22, "12/SPLIT", nameof(PanelButtonAction.Split)),
            new(23, "10/F1", nameof(PanelButtonAction.Anf)),
            new(24, "6/F2", nameof(PanelButtonAction.Nb)),
            new(25, "LF/HF/F3", nameof(PanelButtonAction.Nr)),
            new(27, "160 (Band)", nameof(PanelButtonAction.Band160)),
            new(28, "80 (Band)", nameof(PanelButtonAction.Band80)),
            new(29, "60 (Band)", nameof(PanelButtonAction.Band60)),
            new(30, "40 (Band)", nameof(PanelButtonAction.Band40)),
            new(31, "30 (Band)", nameof(PanelButtonAction.Band30)),
            new(32, "20 (Band)", nameof(PanelButtonAction.Band20)),
            new(33, "17 (Band)", nameof(PanelButtonAction.Band17)),
            new(34, "15 (Band)", nameof(PanelButtonAction.Band15)),
            new(35, "12 (Band)", nameof(PanelButtonAction.Band12)),
            new(36, "10 (Band)", nameof(PanelButtonAction.Band10)),
            new(37, "6 (Band)", nameof(PanelButtonAction.Band6)),
            new(38, "LF/HF (Band)", nameof(PanelButtonAction.None)),
            new(2, "RX1 AF/AGC knob push", nameof(PanelButtonAction.MuteA)),
            new(1, "RX2 AF/AGC knob push", nameof(PanelButtonAction.MuteB)),
            new(3, "DRIVE/MULTI knob push", nameof(PanelButtonAction.None)),
            new(12, "RIT/ATTN knob push", nameof(PanelButtonAction.RitXitClear)),
            new(13, "MULTI 2 knob push", nameof(PanelButtonAction.FilterDefault)),
            new(41, "MULTI 2 knob push (shift)", nameof(PanelButtonAction.None)),
        ],
        _ => [],
    };

    public static IReadOnlyList<PanelControl> Knobs(int product) => product switch
    {
        Original =>
        [
            new(1, "RX AF/AGC inner", nameof(PanelKnobAction.AfGain)),
            new(2, "RX AF/AGC outer", nameof(PanelKnobAction.AgcTop)),
            new(11, "MIC/DRIVE inner", nameof(PanelKnobAction.MicGain)),
            new(12, "MIC/DRIVE outer", nameof(PanelKnobAction.Drive)),
            new(5, "Multi 1 inner", nameof(PanelKnobAction.FilterHigh)),
            new(6, "Multi 1 outer", nameof(PanelKnobAction.FilterLow)),
            new(9, "Multi 2 inner", nameof(PanelKnobAction.Rit)),
            new(10, "Multi 2 outer", nameof(PanelKnobAction.Xit)),
        ],
        Ultra =>
        [
            new(3, "RX1 AF/AGC inner", nameof(PanelKnobAction.AfA)),
            new(4, "RX1 AF/AGC outer", nameof(PanelKnobAction.AgcTop)),
            new(1, "RX2 AF/AGC inner", nameof(PanelKnobAction.AfB)),
            new(2, "RX2 AF/AGC outer", nameof(PanelKnobAction.None)),
            new(5, "DRIVE/MULTI inner", nameof(PanelKnobAction.MicGain)),
            new(6, "DRIVE/MULTI outer", nameof(PanelKnobAction.Drive)),
            new(7, "RIT/ATTN inner", nameof(PanelKnobAction.Rit)),
            new(8, "RIT/ATTN outer", nameof(PanelKnobAction.Attenuation)),
            new(9, "MULTI 2 inner", nameof(PanelKnobAction.FilterHigh)),
            new(10, "MULTI 2 outer", nameof(PanelKnobAction.FilterLow)),
            new(11, "MULTI 2 inner (shift)", nameof(PanelKnobAction.None)),
            new(12, "MULTI 2 outer (shift)", nameof(PanelKnobAction.None)),
        ],
        _ => [],
    };

    /// <summary>Words for the mapping page.</summary>
    public static string Describe(PanelButtonAction a) => a switch
    {
        PanelButtonAction.None => "Nothing",
        PanelButtonAction.Mox => "MOX",
        PanelButtonAction.Tune => "TUNE",
        PanelButtonAction.TwoTone => "Two-tone",
        PanelButtonAction.Ctun => "CTUN",
        PanelButtonAction.Lock => "Lock the frequency",
        PanelButtonAction.Mute => "Mute",
        PanelButtonAction.MuteA => "Mute slice A",
        PanelButtonAction.MuteB => "Mute slice B",
        PanelButtonAction.BandUp => "Band up",
        PanelButtonAction.BandDown => "Band down",
        PanelButtonAction.ModeUp => "Mode up",
        PanelButtonAction.ModeDown => "Mode down",
        PanelButtonAction.FilterWider => "Filter wider",
        PanelButtonAction.FilterNarrower => "Filter narrower",
        PanelButtonAction.FilterDefault => "Filter back to the usual",
        PanelButtonAction.AToB => "A > B",
        PanelButtonAction.BToA => "B > A",
        PanelButtonAction.SwapAB => "Switch to slice A / B",
        PanelButtonAction.Split => "Split (transmit on the other slice)",
        PanelButtonAction.Rit => "RIT on/off",
        PanelButtonAction.Xit => "XIT on/off",
        PanelButtonAction.RitXitClear => "Clear RIT and XIT",
        PanelButtonAction.Nb => "Noise blanker",
        PanelButtonAction.Nr => "Noise reduction",
        PanelButtonAction.Anf => "Auto notch (ANF)",
        PanelButtonAction.Quit => "Quit SimpleHPSDR (hold)",
        _ => a.ToString().Replace("Band", "") + " m",
    };

    public static string Describe(PanelKnobAction a) => a switch
    {
        PanelKnobAction.None => "Nothing",
        PanelKnobAction.AfGain => "Volume (AF)",
        PanelKnobAction.AfA => "Slice A volume",
        PanelKnobAction.AfB => "Slice B volume",
        PanelKnobAction.AgcTop => "AGC threshold",
        PanelKnobAction.Drive => "Drive",
        PanelKnobAction.MicGain => "Mic gain",
        PanelKnobAction.Rit => "RIT offset",
        PanelKnobAction.Xit => "XIT offset",
        PanelKnobAction.FilterHigh => "Filter high edge",
        PanelKnobAction.FilterLow => "Filter low edge",
        PanelKnobAction.Attenuation => "Attenuator",
        PanelKnobAction.TuneB => "Tune slice B",
        _ => a.ToString(),
    };
}
