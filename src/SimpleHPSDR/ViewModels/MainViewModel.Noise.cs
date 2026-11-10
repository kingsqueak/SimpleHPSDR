using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>Right-click settings for the NB and NR buttons. The engine takes
/// the whole NrConfig, so a change reads the current one, applies the fields
/// the operator touched, and sends it back; slice B gets the same. Defaults
/// and ranges are the engine's (Thetis's). NR1 and NR3 have nothing to adjust:
/// NR1's values are fixed in the engine and NR3 only takes a model file.</summary>
public sealed partial class MainViewModel
{
    private Latest<int>? _noiseSender;
    private int _noiseSeq;
    private NoisePanel[] _noisePanels = [];

    /// <summary>NB and NR are one dropdown each: what's on, lit when something is.</summary>
    public string NbLabel => NoiseBlankers.FirstOrDefault(c => c.IsSelected)?.Label ?? "Off";
    public bool NbOn => NbLabel != "Off";
    public string NrLabel => NoiseReductions.FirstOrDefault(c => c.IsSelected)?.Label ?? "Off";
    public bool NrOn => NrLabel != "Off";
    private NoisePanel? _lastNrPanel;

    /// <summary>Right-click on NB: its settings.</summary>
    public NoisePanel NbParams => _nbPanel ??= BuildNbPanel();

    /// <summary>Right-click on NR: the one that's on, or the last one with NR off.</summary>
    public NoisePanel NrParams =>
        NoiseReductions.FirstOrDefault(c => c.IsSelected)?.Params ?? _lastNrPanel
        ?? NoiseReductions.First(c => Equals(c.Value, NrMode.Emnr)).Params!;

    private void WireNoiseButtons()
    {
        foreach (var c in NoiseBlankers)
            c.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(NbLabel));
                OnPropertyChanged(nameof(NbOn));
            };
        foreach (var c in NoiseReductions)
            c.PropertyChanged += (_, _) =>
            {
                if (c is { IsSelected: true, Params: { } panel }) _lastNrPanel = panel;
                OnPropertyChanged(nameof(NrLabel));
                OnPropertyChanged(nameof(NrOn));
                OnPropertyChanged(nameof(NrParams));
            };
    }

    private NoisePanel BuildNbPanel() => Panel(new NoisePanel(
        "Noise blanker", "NB1 and NB2 share the threshold. Lower blanks more.",
        new NoiseParam
        {
            Label = "Threshold", Default = 20, Max = 100,
            Read = n => n.NbThreshold, Write = (n, v) => n with { NbThreshold = v },
        }));
    private NoisePanel? _nbPanel;

    private NoisePanel NoisePanelFor(NrMode mode) => mode switch
    {
        NrMode.Anr => Panel(new NoisePanel("NR1 (LMS)", "NR1 has no adjustable settings in the engine.")),
        NrMode.Rnnr => Panel(new NoisePanel("NR3 (RNNoise)", "NR3 has no adjustable settings; it only needs a model file.")),
        NrMode.Emnr => Panel(new NoisePanel("NR2 (spectral)", "",
            new NoiseParam
            {
                Label = "Gain method", Default = 2, Options = ["Linear", "Log", "Gamma", "Trained"],
                Read = n => n.EmnrGainMethod ?? 2, Write = (n, v) => n with { EmnrGainMethod = (int)v },
            },
            new NoiseParam
            {
                Label = "Noise estimate", Default = 0, Options = ["OSMS", "MMSE", "NSTAT"],
                Read = n => n.EmnrNpeMethod ?? 0, Write = (n, v) => n with { EmnrNpeMethod = (int)v },
            },
            new NoiseParam
            {
                Label = "Artifact filter", Default = 1, IsToggle = true,
                Read = n => n.EmnrAeRun ?? true ? 1 : 0, Write = (n, v) => n with { EmnrAeRun = v != 0 },
            },
            new NoiseParam
            {
                Label = "Trained T1", Default = -0.5, Min = -5, Max = 5, Step = 0.1, Format = "0.0",
                Read = n => n.EmnrTrainT1 ?? -0.5, Write = (n, v) => n with { EmnrTrainT1 = Math.Round(v, 1) },
            },
            new NoiseParam
            {
                Label = "Trained T2", Default = 0.2, Min = 0.02, Max = 0.3, Step = 0.01, Format = "0.00",
                Read = n => n.EmnrTrainT2 ?? 0.2, Write = (n, v) => n with { EmnrTrainT2 = Math.Round(v, 2) },
            },
            new NoiseParam
            {
                Label = "Comfort noise", Default = 1, IsToggle = true,
                Read = n => n.EmnrPost2Run ?? true ? 1 : 0, Write = (n, v) => n with { EmnrPost2Run = v != 0 },
            },
            new NoiseParam
            {
                Label = "Factor", Default = 15,
                Read = n => n.EmnrPost2Factor ?? 15, Write = (n, v) => n with { EmnrPost2Factor = v },
            },
            new NoiseParam
            {
                Label = "Level", Default = 15,
                Read = n => n.EmnrPost2Nlevel ?? 15, Write = (n, v) => n with { EmnrPost2Nlevel = v },
            },
            new NoiseParam
            {
                Label = "Rate", Default = 5, Max = 30, Unit = " s",
                Read = n => n.EmnrPost2Rate ?? 5, Write = (n, v) => n with { EmnrPost2Rate = v },
            },
            new NoiseParam
            {
                Label = "Taper", Default = 12, Unit = " bins",
                Read = n => n.EmnrPost2Taper ?? 12, Write = (n, v) => n with { EmnrPost2Taper = (int)v },
            })),
        NrMode.Sbnr => Panel(new NoisePanel("NR4 (libspecbleach)", "",
            new NoiseParam
            {
                Label = "Reduction", Default = 10, Max = 20, Unit = " dB",
                Read = n => n.Nr4ReductionAmount ?? 10, Write = (n, v) => n with { Nr4ReductionAmount = v },
            },
            new NoiseParam
            {
                Label = "Smoothing", Default = 0, Unit = " %",
                Read = n => n.Nr4SmoothingFactor ?? 0, Write = (n, v) => n with { Nr4SmoothingFactor = v },
            },
            new NoiseParam
            {
                Label = "Whitening", Default = 0, Unit = " %",
                Read = n => n.Nr4WhiteningFactor ?? 0, Write = (n, v) => n with { Nr4WhiteningFactor = v },
            },
            new NoiseParam
            {
                Label = "Noise rescale", Default = 2, Max = 12, Unit = " dB",
                Read = n => n.Nr4NoiseRescale ?? 2, Write = (n, v) => n with { Nr4NoiseRescale = v },
            },
            new NoiseParam
            {
                Label = "Post-filter", Default = -10, Min = -10, Max = 10, Unit = " dB",
                Read = n => n.Nr4PostFilterThreshold ?? -10, Write = (n, v) => n with { Nr4PostFilterThreshold = v },
            },
            new NoiseParam
            {
                Label = "Noise scaling", Default = 0, Options = ["Method 1", "Method 2", "Method 3"],
                Read = n => n.Nr4NoiseScalingType ?? 0, Write = (n, v) => n with { Nr4NoiseScalingType = (int)v },
            })),
        NrMode.Nnr => Panel(new NoisePanel("NNR (neural)", "",
            new NoiseParam
            {
                Label = "Model", Default = 0, Options = ["Standard", "Premium"],
                Read = n => n.NnrModel ?? 0, Write = (n, v) => n with { NnrModel = (int)v },
            },
            new NoiseParam
            {
                Label = "Mask floor", Default = -25, Min = -60, Max = 0, Unit = " dB",
                Read = n => n.NnrMaskFloorDb ?? -25, Write = (n, v) => n with { NnrMaskFloorDb = v },
            },
            new NoiseParam
            {
                Label = "Alpha", Default = 1, Max = 4, Step = 0.1, Format = "0.0",
                Read = n => n.NnrAlpha ?? 1, Write = (n, v) => n with { NnrAlpha = Math.Round(v, 1) },
            },
            new NoiseParam
            {
                Label = "Knee", Default = 10, Max = 40, Unit = " dB",
                Read = n => n.NnrKneeDb ?? 10, Write = (n, v) => n with { NnrKneeDb = v },
            },
            new NoiseParam
            {
                Label = "Tau", Default = 2, Min = 0.05, Max = 30, Step = 0.05, Format = "0.00", Unit = " s",
                Read = n => n.NnrTauSeconds ?? 2, Write = (n, v) => n with { NnrTauSeconds = Math.Round(v, 2) },
            },
            new NoiseParam
            {
                Label = "Max gain", Default = 12, Max = 24, Unit = " dB",
                Read = n => n.NnrMaxGainDb ?? 12, Write = (n, v) => n with { NnrMaxGainDb = v },
            },
            new NoiseParam
            {
                Label = "Attack", Default = 0, Max = 500, Unit = " ms",
                Read = n => n.NnrAttackMs ?? 0, Write = (n, v) => n with { NnrAttackMs = v },
            },
            new NoiseParam
            {
                Label = "Release", Default = 0, Max = 500, Unit = " ms",
                Read = n => n.NnrReleaseMs ?? 0, Write = (n, v) => n with { NnrReleaseMs = v },
            })),
        _ => Panel(new NoisePanel("", "")),
    };

    private NoisePanel Panel(NoisePanel panel)
    {
        _noisePanels = [.. _noisePanels, panel];
        foreach (var row in panel.Rows)
        {
            row.Changed = () =>
            {
                _noiseSender ??= new Latest<int>(_ => SendNoiseParamsAsync());
                _noiseSender.Send(++_noiseSeq);
            };
        }
        return panel;
    }

    /// <summary>A popup is opening: show what the engine has now.</summary>
    public async Task LoadNoisePanelAsync(NoisePanel panel)
    {
        if (!IsConnected) return;
        await Try(async () =>
        {
            var nr = (await _client!.GetStateAsync()).Nr ?? new NrConfig();
            foreach (var row in panel.Rows) row.Load(nr);
        });
    }

    private async Task SendNoiseParamsAsync()
    {
        var nr = (await _client!.GetStateAsync()).Nr ?? new NrConfig();
        foreach (var row in _noisePanels.SelectMany(p => p.Rows).Where(r => r.Touched))
            nr = row.Write(nr, row.Value);
        await SendNrAsync(nr);
    }

    /// <summary>NR1's whole config, and the same for slice B.</summary>
    private async Task SendNrAsync(NrConfig nr)
    {
        await _client!.SetNrAsync(nr);
        if (SliceBEnabled) await _client.SetReceiverAsync(1, new ReceiverSetRequest(NrMode: nr.NrMode, Nr: nr));
    }
}
