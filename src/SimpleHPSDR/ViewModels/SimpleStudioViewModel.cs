using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Contracts;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>The fixed ends of the chain strip and its "+ Add effect" button.</summary>
public sealed class StudioStripMic;
public sealed class StudioStripTx;
public sealed class StudioStripAdd(SimpleStudioViewModel owner)
{
    public SimpleStudioViewModel Owner { get; } = owner;
}

/// <summary>
/// The SimpleStudio window: the chain of mic effects in signal order, the
/// selected effect's controls and its meters. It knows nothing of the engine:
/// every edit raises <see cref="Changed"/> and the owner sends
/// <see cref="ToConfig"/>; meters come through <see cref="FetchMeters"/>.
/// </summary>
public sealed partial class SimpleStudioViewModel : ObservableObject
{
    private bool _loading;
    private bool _polling;

    public SimpleStudioViewModel()
    {
        AddChoices = StudioCatalogue.Effects
            .Select(e => new Choice(e.Label, e, c => Add((StudioEffectInfo)c.Value))).ToList();
        PresetChoices = StudioPresets.All
            .Select(p => new Choice(p.Name, p, c => ApplyPreset((StudioPresets.Preset)c.Value))).ToList();
        _addItem = new StudioStripAdd(this);
        Effects.CollectionChanged += (_, _) => RebuildStrip();
        RebuildStrip();
    }

    public ObservableCollection<StudioEffectViewModel> Effects { get; } = [];

    /// <summary>The strip in order: MIC, the tiles, "+ Add effect", TX AUDIO.</summary>
    public ObservableCollection<object> Strip { get; } = [];
    private readonly StudioStripAdd _addItem;
    private static readonly StudioStripMic MicItem = new();
    private static readonly StudioStripTx TxItem = new();

    public IReadOnlyList<Choice> AddChoices { get; }
    public IReadOnlyList<Choice> PresetChoices { get; }

    [ObservableProperty] private StudioEffectViewModel? _selected;
    /// <summary>The whole chain on or off (the tick box in Config > TX Audio).</summary>
    [ObservableProperty] private bool _enabled;
    /// <summary>The last meter reading had transmit audio in it.</summary>
    [ObservableProperty] private bool _metersActive;

    public bool IsEmpty => Effects.Count == 0;
    public bool HasSelection => Selected is not null;
    public bool CanAdd => Effects.Count < StudioCatalogue.MaxEffects;
    public string AddTip => CanAdd ? "Add an effect at the end of the chain"
        : $"The chain is full ({StudioCatalogue.MaxEffects} effects). Remove one to add another.";

    /// <summary>Any edit to the chain (not a <see cref="Load"/>).</summary>
    public event Action? Changed;

    /// <summary>Reads the engine's meters; null where there is no engine.</summary>
    public Func<Task<SimpleStudioMetersDto>>? FetchMeters { get; set; }

    partial void OnEnabledChanged(bool value) => RaiseChanged();

    partial void OnSelectedChanged(StudioEffectViewModel? value)
    {
        foreach (var e in Effects) e.IsSelected = e == value;
        OnPropertyChanged(nameof(HasSelection));
    }

    private void RaiseChanged()
    {
        if (!_loading) Changed?.Invoke();
    }

    private void RebuildStrip()
    {
        Strip.Clear();
        Strip.Add(MicItem);
        foreach (var e in Effects) Strip.Add(e);
        Strip.Add(_addItem);
        Strip.Add(TxItem);
        for (int i = 0; i < Effects.Count; i++)
        {
            Effects[i].CanMoveLeft = i > 0;
            Effects[i].CanMoveRight = i < Effects.Count - 1;
        }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(AddTip));
    }

    // ---- building the chain ----

    private StudioEffectViewModel Make(StudioEffectInfo info, string id, bool enabled, IReadOnlyDictionary<string, double>? values)
    {
        var e = new StudioEffectViewModel(info, id, enabled, values)
        {
            MoveBy = Move,
            RemoveMe = Remove,
            SelectMe = Select,
        };
        e.Changed += RaiseChanged;
        return e;
    }

    /// <summary>An id for a new effect: its type and a short random tail.</summary>
    private string NewId(string type)
    {
        string id;
        do id = $"{type}-{Random.Shared.Next(0x10000):x4}";
        while (Effects.Any(e => e.Id == id));
        return id;
    }

    /// <summary>Add an effect at the end, with the catalogue's defaults, and select it.</summary>
    public StudioEffectViewModel? Add(StudioEffectInfo info, IReadOnlyDictionary<string, double>? values = null)
    {
        if (!CanAdd) return null;
        var e = Make(info, NewId(info.Type), true, values);
        Effects.Add(e);
        Selected = e;
        RaiseChanged();
        return e;
    }

    public void Remove(StudioEffectViewModel e)
    {
        int at = Effects.IndexOf(e);
        if (at < 0) return;
        Effects.RemoveAt(at);
        if (Selected == e) Selected = Effects.Count == 0 ? null : Effects[Math.Min(at, Effects.Count - 1)];
        RaiseChanged();
    }

    /// <summary>Move one place left (-1) or right (+1).</summary>
    public void Move(StudioEffectViewModel e, int by)
    {
        int at = Effects.IndexOf(e), to = at + by;
        if (at < 0 || to < 0 || to >= Effects.Count) return;
        Effects.Move(at, to);
        RebuildStrip();
        Selected = e;
        RaiseChanged();
    }

    public void Select(StudioEffectViewModel e)
    {
        if (Effects.Contains(e)) Selected = e;
    }

    /// <summary>Replace the chain with a ready-made one; the chain's on/off stays.</summary>
    public void ApplyPreset(StudioPresets.Preset preset)
    {
        _loading = true;
        try
        {
            Effects.Clear();
            foreach (var (type, values) in preset.Effects.Take(StudioCatalogue.MaxEffects))
                if (StudioCatalogue.Find(type) is { } info)
                    Effects.Add(Make(info, NewId(type), true, values));
            Selected = Effects.FirstOrDefault();
        }
        finally { _loading = false; }
        RaiseChanged();
    }

    public SimpleStudioConfig ToConfig() =>
        new(Enabled, Effects.Select(e => e.ToConfig()).ToList());

    /// <summary>Show a chain from the engine or a profile without sending it
    /// back. The selection stays on the same effect where it can.</summary>
    public void Load(SimpleStudioConfig? config)
    {
        var cfg = (config ?? SimpleStudioConfig.Default).Normalized();
        _loading = true;
        try
        {
            Enabled = cfg.Enabled;
            var now = ToConfig();
            if (SameEffects(cfg, now)) return;
            string? keep = Selected?.Id;
            Effects.Clear();
            foreach (var e in cfg.Effects ?? [])
                if (StudioCatalogue.Find(e.Type) is { } info)
                    Effects.Add(Make(info, e.Id, e.Enabled, e.Params));
            Selected = Effects.FirstOrDefault(e => e.Id == keep) ?? Effects.FirstOrDefault();
        }
        finally { _loading = false; }
    }

    /// <summary>The same chain, on/off included.</summary>
    public static bool Same(SimpleStudioConfig? a, SimpleStudioConfig? b)
    {
        a ??= SimpleStudioConfig.Default;
        b ??= SimpleStudioConfig.Default;
        return a.Enabled == b.Enabled && SameEffects(a, b);
    }

    private static bool SameEffects(SimpleStudioConfig a, SimpleStudioConfig b)
    {
        var ea = a.Effects ?? [];
        var eb = b.Effects ?? [];
        if (ea.Count != eb.Count) return false;
        for (int i = 0; i < ea.Count; i++)
            if (!StudioEffectViewModel.Same(ea[i], eb[i])) return false;
        return true;
    }

    // ---- meters ----

    /// <summary>One meter read (the window calls this about 15 times a second
    /// while it is showing). A read still on its way is not doubled.</summary>
    public async Task RefreshMetersAsync()
    {
        if (_polling || FetchMeters is not { } fetch) return;
        _polling = true;
        try { ApplyMeters(await fetch()); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException
            or System.Text.Json.JsonException)
        {
            ApplyMeters(SimpleStudioMetersDto.Inactive);
        }
        finally { _polling = false; }
    }

    public void ApplyMeters(SimpleStudioMetersDto m)
    {
        MetersActive = m.Active;
        var byId = (m.Effects ?? []).Where(e => e is not null).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var e in Effects) e.ApplyMeter(byId.GetValueOrDefault(e.Id), m.Active);
    }

    public void ClearMeters() => ApplyMeters(SimpleStudioMetersDto.Inactive);
}
