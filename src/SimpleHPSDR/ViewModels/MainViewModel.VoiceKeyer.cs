using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SimpleHPSDR.ViewModels;

/// <summary>One voice keyer button: a label and a recording it transmits.</summary>
public sealed partial class VoiceKeyerSlot : ObservableObject
{
    private readonly MainViewModel _owner;

    public VoiceKeyerSlot(MainViewModel owner, int index, string? label, string? file)
    {
        _owner = owner;
        Index = index;
        _label = string.IsNullOrWhiteSpace(label) ? $"VK{index}" : label;
        _fileName = string.IsNullOrWhiteSpace(file) ? null : file;
    }

    /// <summary>The recordings, for the right-click picker.</summary>
    public ObservableCollection<RecordingRow> Recordings => _owner.Recordings;

    /// <summary>1 to 4.</summary>
    public int Index { get; }
    public string KeyName => $"F{Index + 4}";
    /// <summary>The shortcut that always works: ⌘1-4 on macOS (its F5 is dictation), Ctrl+1-4 elsewhere.</summary>
    public string ShortKey => OperatingSystem.IsMacOS() ? $"⌘{Index}" : $"Ctrl+{Index}";
    public string KeyLabel => OperatingSystem.IsMacOS() ? ShortKey : KeyName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tip))]
    private string _label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClip), nameof(Tip), nameof(Choice))]
    private string? _fileName;

    [ObservableProperty] private bool _isPlaying;

    public bool HasClip => FileName is not null;

    public string Tip => HasClip
        ? $"Click, {ShortKey} or {KeyName}: transmit {Label}. Click again (or MOX) to stop. Right-click to change it."
        : $"{ShortKey} / {KeyName}: right-click to choose a recording for this button.";

    /// <summary>The assigned recording, for the right-click picker.</summary>
    public RecordingRow? Choice
    {
        get => _owner.Recordings.FirstOrDefault(r => r.FileName == FileName);
        set
        {
            if (value is null || value.FileName == FileName) return;
            FileName = value.FileName;
            // A fresh button takes the recording's name until it's given one.
            if (Label == $"VK{Index}") Label = value.Label;
            _owner.SaveVoiceKeyer();
        }
    }

    partial void OnLabelChanged(string value) => _owner.SaveVoiceKeyer();

    internal void RefreshChoice() => OnPropertyChanged(nameof(Choice));

    /// <summary>The settings panel opening.</summary>
    public void Prepare() => _ = _owner.PrepareVoiceKeyerAsync();

    [RelayCommand] private void Fire() => _owner.FireVoiceKeyer(this);

    [RelayCommand]
    private void Clear()
    {
        FileName = null;
        Label = $"VK{Index}";
        _owner.SaveVoiceKeyer();
    }
}

/// <summary>
/// Voice keyer: four buttons right of REC (F5 to F8), each transmitting one
/// recording at the deck's TX level, through the same engine playback as the
/// deck (MOX with every TX check; MOX or a second click stops it).
/// </summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<VoiceKeyerSlot> VoiceKeyerSlots { get; } = [];

    /// <summary>The voice keyer row over the waterfall on the touch layout (REC's settings).</summary>
    public bool VoiceKeyerOnTouch
    {
        get => _settings.VoiceKeyerOnTouch;
        set
        {
            if (_settings.VoiceKeyerOnTouch == value) return;
            _settings.VoiceKeyerOnTouch = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }
    private bool _loadingVoiceKeyer;

    private void LoadVoiceKeyer()
    {
        _loadingVoiceKeyer = true;
        var saved = _settings.VoiceKeyer;
        for (int i = 1; i <= 4; i++)
        {
            var s = i <= saved.Count ? saved[i - 1] : null;
            VoiceKeyerSlots.Add(new VoiceKeyerSlot(this, i, s?.Label, s?.File));
        }
        ReloadRecordingList();
        _loadingVoiceKeyer = false;
    }

    internal void SaveVoiceKeyer()
    {
        if (_loadingVoiceKeyer || VoiceKeyerSlots.Count < 4) return;
        _settings.VoiceKeyer = [.. VoiceKeyerSlots.Select(s => new VoiceKeyerSetting { Label = s.Label, File = s.FileName })];
        _settings.Save();
    }

    /// <summary>F5 to F8 (index 1 to 4), or a click.</summary>
    public void FireVoiceKeyer(int index)
    {
        if (index >= 1 && index <= VoiceKeyerSlots.Count) FireVoiceKeyer(VoiceKeyerSlots[index - 1]);
    }

    internal void FireVoiceKeyer(VoiceKeyerSlot slot)
    {
        if (!IsConnected) return;
        if (slot.IsPlaying)
        {
            _ = Try(async () => ApplyRecorder(await _client!.StopPlaybackAsync()));
            return;
        }
        if (slot.FileName is not { } file)
        {
            Status = $"{slot.Label}: right-click the button to choose a recording.";
            return;
        }
        if (!File.Exists(Path.Combine(RecorderFolder, file)))
        {
            Status = $"{slot.Label}: the recording isn't there any more. Right-click to choose another.";
            return;
        }
        PlayFile(file, file.StartsWith(MicPrefix, StringComparison.OrdinalIgnoreCase), local: false);
    }

    /// <summary>Keep the buttons pointing at a recording that was renamed.</summary>
    private void VoiceKeyerFileRenamed(string from, string to)
    {
        bool changed = false;
        foreach (var slot in VoiceKeyerSlots)
            if (slot.FileName == from) { slot.FileName = to; changed = true; }
        if (changed) SaveVoiceKeyer();
    }

    /// <summary>The right-click panel opening: current list for the picker.</summary>
    public async Task PrepareVoiceKeyerAsync()
    {
        await RefreshRecordingsAsync();
        foreach (var slot in VoiceKeyerSlots) slot.RefreshChoice();
    }
}
