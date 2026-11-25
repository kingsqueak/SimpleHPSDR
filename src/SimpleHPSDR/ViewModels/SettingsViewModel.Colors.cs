using Avalonia.Media;

namespace SimpleHPSDR.ViewModels;

/// <summary>Config > Colors: spectrum and waterfall colours, an app setting,
/// shown at once. Each colour is a named one from the list or a custom one from
/// the picker beside it, saved as #RRGGBB.</summary>
public sealed partial class SettingsViewModel
{
    public WaterfallPalette[] WaterfallPalettes => SpectrumLook.Palettes;

    // One colour row. Its list never changes (a drop-down whose list is swapped
    // loses its binding): the named colours, then "Custom", whose swatch shows
    // the picked colour.
    private sealed class ColorRow
    {
        private readonly NamedColor[] _named;
        private readonly string _fallback;
        public NamedColor Custom { get; } = new("Custom", Colors.Gray);
        public NamedColor[] List { get; }
        public Func<Settings, string> Get { get; }
        public Action<Settings, string> Set { get; }

        public ColorRow(NamedColor[] named, Func<Settings, string> get, Action<Settings, string> set, string fallback)
        {
            _named = named;
            _fallback = fallback;
            List = [.. named, Custom];
            Get = get;
            Set = set;
        }

        /// <summary>The colour in use: a named one or the picked one.</summary>
        public Color Current(Settings? s) =>
            SpectrumLook.Custom(s is null ? null : Get(s))?.Color ?? Named(s).Color;

        private NamedColor Named(Settings? s)
        {
            string? value = s is null ? null : Get(s);
            return Array.Find(_named, c => c.Name == value) ?? Array.Find(_named, c => c.Name == _fallback)!;
        }

        public NamedColor Selected(Settings? s)
        {
            if (s is not null && SpectrumLook.Custom(Get(s)) is { } picked)
            {
                ((SolidColorBrush)Custom.Swatch).Color = picked.Color;
                return Custom;
            }
            return Named(s);
        }
    }

    private ColorRow? _lineRow, _fillRow, _backRow, _fadeRow, _gridRow, _passRow;
    private ColorRow LineRow => _lineRow ??= new(SpectrumLook.LineColors, s => s.SpecLineColor, (s, v) => s.SpecLineColor = v, "Blue");
    private ColorRow FillRow => _fillRow ??= new(SpectrumLook.FillColors, s => s.SpecFillColor, (s, v) => s.SpecFillColor = v, "Blue");
    private ColorRow BackRow => _backRow ??= new(SpectrumLook.BackgroundColors, s => s.SpecBackground, (s, v) => s.SpecBackground = v, "Black");
    private ColorRow FadeRow => _fadeRow ??= new(SpectrumLook.FadeColors, s => s.SpecBackgroundFade, (s, v) => s.SpecBackgroundFade = v, "None");
    private ColorRow GridRow => _gridRow ??= new(SpectrumLook.GridColors, s => s.SpecGridColor, (s, v) => s.SpecGridColor = v, "Grey");
    private ColorRow PassRow => _passRow ??= new(SpectrumLook.PassbandColors, s => s.SpecPassbandColor, (s, v) => s.SpecPassbandColor = v, "White");

    public NamedColor[] GridColors => GridRow.List;
    public NamedColor? SpecGrid { get => GridRow.Selected(AppSettings); set => Pick(GridRow, value); }
    public Color SpecGridCustom { get => GridRow.Current(AppSettings); set => SetCustom(GridRow, value); }
    /// <summary>How solid the grid lines are, 0 to 100 %.</summary>
    public double SpecGridOpacity
    {
        get => AppSettings?.SpecGridOpacity ?? SpectrumLook.DefaultGridOpacity;
        set
        {
            int v = (int)Math.Round(Math.Clamp(value, 0, 100));
            if (AppSettings is not { } s || s.SpecGridOpacity == v) return;
            s.SpecGridOpacity = v;
            s.Save();
            SpectrumLook.Apply(s);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpecGridOpacityText));
        }
    }
    public string SpecGridOpacityText => $"{SpecGridOpacity:0} %";

    public NamedColor[] PassbandColors => PassRow.List;
    public NamedColor? SpecPassband { get => PassRow.Selected(AppSettings); set => Pick(PassRow, value); }
    public Color SpecPassbandCustom { get => PassRow.Current(AppSettings); set => SetCustom(PassRow, value); }
    /// <summary>How solid the receive filter's shading is, 0 to 100 %.</summary>
    public double SpecPassbandOpacity
    {
        get => AppSettings?.SpecPassbandOpacity ?? SpectrumLook.DefaultPassbandOpacity;
        set
        {
            int v = (int)Math.Round(Math.Clamp(value, 0, 100));
            if (AppSettings is not { } s || s.SpecPassbandOpacity == v) return;
            s.SpecPassbandOpacity = v;
            s.Save();
            SpectrumLook.Apply(s);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpecPassbandOpacityText));
        }
    }
    public string SpecPassbandOpacityText => $"{SpecPassbandOpacity:0} %";

    public NamedColor[] LineColors => LineRow.List;
    public NamedColor[] FillColors => FillRow.List;
    public NamedColor[] BackgroundColors => BackRow.List;
    public NamedColor[] FadeColors => FadeRow.List;

    public NamedColor? SpecLine { get => LineRow.Selected(AppSettings); set => Pick(LineRow, value); }
    public NamedColor? SpecFill { get => FillRow.Selected(AppSettings); set => Pick(FillRow, value); }
    public NamedColor? SpecBackground { get => BackRow.Selected(AppSettings); set => Pick(BackRow, value); }
    public NamedColor? SpecFade { get => FadeRow.Selected(AppSettings); set => Pick(FadeRow, value); }

    // The pickers start from the colour in use; "None" (shading, fade) starts
    // from the line or background colour instead of transparent.
    public Color SpecLineCustom { get => LineRow.Current(AppSettings); set => SetCustom(LineRow, value); }
    public Color SpecFillCustom
    {
        get => FillRow.Current(AppSettings) is { A: > 0 } c ? c : SpecLineCustom;
        set => SetCustom(FillRow, value);
    }
    public Color SpecBackgroundCustom { get => BackRow.Current(AppSettings); set => SetCustom(BackRow, value); }
    public Color SpecFadeCustom
    {
        get => FadeRow.Current(AppSettings) is { A: > 0 } c ? c : SpecBackgroundCustom;
        set => SetCustom(FadeRow, value);
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

    private void Pick(ColorRow row, NamedColor? value)
    {
        if (AppSettings is not { } s || value is null) return;
        // "Custom" from the list keeps the colour in use, as a picked one.
        string next = ReferenceEquals(value, row.Custom) ? SpectrumLook.Hex(row.Current(s)) : value.Name;
        if (row.Get(s) == next) return;
        row.Set(s, next);
        Changed();
    }

    private void SetCustom(ColorRow row, Color value)
    {
        if (AppSettings is not { } s) return;
        string hex = SpectrumLook.Hex(value);
        if (row.Get(s) == hex) return;
        row.Set(s, hex);
        Changed();
    }

    private void Changed()
    {
        var s = AppSettings!;
        s.Save();
        SpectrumLook.Apply(s);
        RaiseSelections();
    }

    private void RaiseSelections()
    {
        OnPropertyChanged(nameof(SpecLine));
        OnPropertyChanged(nameof(SpecFill));
        OnPropertyChanged(nameof(SpecBackground));
        OnPropertyChanged(nameof(SpecFade));
        OnPropertyChanged(nameof(SpecLineCustom));
        OnPropertyChanged(nameof(SpecFillCustom));
        OnPropertyChanged(nameof(SpecBackgroundCustom));
        OnPropertyChanged(nameof(SpecFadeCustom));
        OnPropertyChanged(nameof(SpecGrid));
        OnPropertyChanged(nameof(SpecGridCustom));
        OnPropertyChanged(nameof(SpecGridOpacity));
        OnPropertyChanged(nameof(SpecGridOpacityText));
        OnPropertyChanged(nameof(SpecPassband));
        OnPropertyChanged(nameof(SpecPassbandCustom));
        OnPropertyChanged(nameof(SpecPassbandOpacity));
        OnPropertyChanged(nameof(SpecPassbandOpacityText));
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ResetColors()
    {
        if (AppSettings is not { } s) return;
        (s.SpecLineColor, s.SpecFillColor, s.SpecBackground, s.SpecBackgroundFade, s.WaterfallPalette) =
            ("Blue", "Blue", "Black", "None", "Classic");
        (s.SpecGridColor, s.SpecGridOpacity) = ("Grey", SpectrumLook.DefaultGridOpacity);
        (s.SpecPassbandColor, s.SpecPassbandOpacity) = ("White", SpectrumLook.DefaultPassbandOpacity);
        s.Save();
        SpectrumLook.Apply(s);
        RaiseSelections();
        OnPropertyChanged(nameof(SelectedPalette));
    }
}
