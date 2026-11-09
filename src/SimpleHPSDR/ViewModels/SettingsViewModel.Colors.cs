namespace SimpleHPSDR.ViewModels;

/// <summary>Config > Colors: spectrum and waterfall colours, an app setting,
/// shown at once.</summary>
public sealed partial class SettingsViewModel
{
    public NamedColor[] LineColors => SpectrumLook.LineColors;
    public NamedColor[] FillColors => SpectrumLook.FillColors;
    public NamedColor[] BackgroundColors => SpectrumLook.BackgroundColors;
    public NamedColor[] FadeColors => SpectrumLook.FadeColors;
    public WaterfallPalette[] WaterfallPalettes => SpectrumLook.Palettes;

    public NamedColor? SpecLine
    {
        get => Array.Find(LineColors, c => c.Name == AppSettings?.SpecLineColor) ?? LineColors[0];
        set => SetLook(value, (s, v) => s.SpecLineColor = v);
    }
    public NamedColor? SpecFill
    {
        get => Array.Find(FillColors, c => c.Name == AppSettings?.SpecFillColor) ?? FillColors[1];
        set => SetLook(value, (s, v) => s.SpecFillColor = v);
    }
    public NamedColor? SpecBackground
    {
        get => Array.Find(BackgroundColors, c => c.Name == AppSettings?.SpecBackground) ?? BackgroundColors[0];
        set => SetLook(value, (s, v) => s.SpecBackground = v);
    }
    public NamedColor? SpecFade
    {
        get => Array.Find(FadeColors, c => c.Name == AppSettings?.SpecBackgroundFade) ?? FadeColors[0];
        set => SetLook(value, (s, v) => s.SpecBackgroundFade = v);
    }
    public WaterfallPalette? SelectedPalette
    {
        get => Array.Find(WaterfallPalettes, p => p.Name == AppSettings?.WaterfallPalette) ?? WaterfallPalettes[0];
        set
        {
            if (AppSettings is not { } s || value is null || value.Name == s.WaterfallPalette) return;
            s.WaterfallPalette = value.Name;
            s.Save();
            SpectrumLook.Apply(s);
            OnPropertyChanged();
        }
    }

    private void SetLook(NamedColor? value, Action<Settings, string> set)
    {
        if (AppSettings is not { } s || value is null) return;
        set(s, value.Name);
        s.Save();
        SpectrumLook.Apply(s);
        OnPropertyChanged(nameof(SpecLine));
        OnPropertyChanged(nameof(SpecFill));
        OnPropertyChanged(nameof(SpecBackground));
        OnPropertyChanged(nameof(SpecFade));
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ResetColors()
    {
        if (AppSettings is not { } s) return;
        (s.SpecLineColor, s.SpecFillColor, s.SpecBackground, s.SpecBackgroundFade, s.WaterfallPalette) =
            ("Blue", "Blue", "Black", "None", "Classic");
        s.Save();
        SpectrumLook.Apply(s);
        OnPropertyChanged(nameof(SpecLine));
        OnPropertyChanged(nameof(SpecFill));
        OnPropertyChanged(nameof(SpecBackground));
        OnPropertyChanged(nameof(SpecFade));
        OnPropertyChanged(nameof(SelectedPalette));
    }
}
