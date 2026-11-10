using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SimpleHPSDR.ViewModels;

/// <summary>One panel control on the mapping page: what it does, as a choice.</summary>
public sealed partial class PanelMapRow : ObservableObject
{
    private readonly Action<PanelMapRow> _changed;

    public PanelMapRow(string key, string label, IReadOnlyList<string> names, IReadOnlyList<string> words,
        string current, string fallback, Action<PanelMapRow> changed)
    {
        Key = key;
        Label = label;
        Names = names;
        Words = words;
        Default = fallback;
        _changed = changed;
        _selectedIndex = Math.Max(0, IndexOf(current));
    }

    public string Key { get; }
    public string Label { get; }
    public IReadOnlyList<string> Names { get; }
    public IReadOnlyList<string> Words { get; }
    public string Default { get; }
    public string Selected => Names[Math.Clamp(SelectedIndex, 0, Names.Count - 1)];

    [ObservableProperty] private int _selectedIndex;
    partial void OnSelectedIndexChanged(int value) => _changed(this);

    private int IndexOf(string name)
    {
        for (int i = 0; i < Names.Count; i++) if (Names[i] == name) return i;
        return 0;
    }
}

/// <summary>Configuration > Hardware: the ANAN G2 front panel (through p2app).</summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty] private bool _hasG2Panel;
    [ObservableProperty] private int _panelTypeIndex;   // 0 original G2 panel, 1 G2V2 / Ultra
    public string[] PanelTypes { get; } = ["Original G2 panel (GPIO v1 or v2)", "G2V2 / G2 Ultra panel"];
    public ObservableCollection<PanelMapRow> PanelButtons { get; } = [];
    public ObservableCollection<PanelMapRow> PanelKnobs { get; } = [];

    /// <summary>From the main window: restart the panel link after a change.</summary>
    public Func<Task>? RestartG2Panel { get; init; }
    /// <summary>From the main window: what the panel link is doing, and which panel it found.</summary>
    public string G2PanelStatusText { get; init; } = "";
    public int G2PanelFound { get; init; }

    private static readonly string[] ButtonNames = Enum.GetNames<PanelButtonAction>();
    private static readonly string[] ButtonWords = [.. Enum.GetValues<PanelButtonAction>().Select(G2Panels.Describe)];
    private static readonly string[] KnobNames = Enum.GetNames<PanelKnobAction>();
    private static readonly string[] KnobWords = [.. Enum.GetValues<PanelKnobAction>().Select(G2Panels.Describe)];

    public bool G2PanelOn
    {
        get => AppSettings?.G2PanelOn ?? true;
        set
        {
            if (AppSettings is null || AppSettings.G2PanelOn == value) return;
            AppSettings.G2PanelOn = value;
            AppSettings.Save();
            OnPropertyChanged();
            _ = RestartG2Panel?.Invoke();
        }
    }

    public decimal G2PanelPort
    {
        get => AppSettings?.G2PanelPort ?? 19090;
        set
        {
            int port = Math.Clamp((int)value, 1024, 65535);
            if (AppSettings is null || AppSettings.G2PanelPort == port) return;
            AppSettings.G2PanelPort = port;
            AppSettings.Save();
            OnPropertyChanged();
            _ = RestartG2Panel?.Invoke();
        }
    }

    private void LoadG2Panel(bool supported)
    {
        HasG2Panel = supported;
        if (!supported) return;
        PanelTypeIndex = G2PanelFound == G2Panels.Ultra ? 1 : 0;
        FillPanelMap();
    }

    partial void OnPanelTypeIndexChanged(int value) => FillPanelMap();

    private void FillPanelMap()
    {
        int product = PanelTypeIndex == 1 ? G2Panels.Ultra : G2Panels.Original;
        var map = AppSettings?.G2PanelMap ?? [];
        PanelButtons.Clear();
        foreach (var c in G2Panels.Buttons(product))
        {
            string key = $"B{product}:{c.Code}";
            PanelButtons.Add(new PanelMapRow(key, c.Label, ButtonNames, ButtonWords,
                map.GetValueOrDefault(key, c.DefaultAction), c.DefaultAction, SavePanelRow));
        }
        PanelKnobs.Clear();
        foreach (var c in G2Panels.Knobs(product))
        {
            string key = $"E{product}:{c.Code}";
            PanelKnobs.Add(new PanelMapRow(key, c.Label, KnobNames, KnobWords,
                map.GetValueOrDefault(key, c.DefaultAction), c.DefaultAction, SavePanelRow));
        }
    }

    // Only changes from the default are kept, so a later default change still reaches you.
    private void SavePanelRow(PanelMapRow row)
    {
        if (AppSettings is null) return;
        if (row.Selected == row.Default) AppSettings.G2PanelMap.Remove(row.Key);
        else AppSettings.G2PanelMap[row.Key] = row.Selected;
        AppSettings.Save();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ResetPanelMap()
    {
        if (AppSettings is null) return;
        string prefix = PanelTypeIndex == 1 ? "5:" : "4:";
        foreach (var key in AppSettings.G2PanelMap.Keys.Where(k => k[1..].StartsWith(prefix)).ToList())
            AppSettings.G2PanelMap.Remove(key);
        AppSettings.Save();
        FillPanelMap();
    }
}
