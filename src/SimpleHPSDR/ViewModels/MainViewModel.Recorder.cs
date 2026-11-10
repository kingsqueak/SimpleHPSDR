using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>One file in the recordings folder. Names start "RX " (received
/// audio) or "MIC " (a voice keyer message from the microphone); the rest is
/// the label you see and can rename.</summary>
public sealed partial class RecordingRow : ObservableObject
{
    private readonly MainViewModel _owner;

    public RecordingRow(MainViewModel owner, string path)
    {
        _owner = owner;
        FileName = Path.GetFileName(path);
        IsMic = FileName.StartsWith(MainViewModel.MicPrefix, StringComparison.OrdinalIgnoreCase);
        _label = Path.GetFileNameWithoutExtension(FileName)[Prefix.Length..];
        Seconds = MainViewModel.WavSeconds(path);
    }

    public string FileName { get; private set; }
    public bool IsMic { get; }
    public string Prefix => IsMic ? MainViewModel.MicPrefix : MainViewModel.RxPrefix;
    public string Kind => IsMic ? "MIC" : "RX";
    public double Seconds { get; }
    public string Length => MainViewModel.Clock(Seconds);

    [ObservableProperty] private string _label;
    [ObservableProperty] private bool _confirmDelete;

    public string KindTip => IsMic
        ? "From the microphone: goes out through your mic gain and TX processing, like live speech"
        : "From the receiver: goes out as heard, through the TX filter only";

    /// <summary>Rename when the label box loses focus or Enter is pressed.</summary>
    public void CommitLabel() => _owner.Rename(this);

    internal void Renamed(string fileName) => FileName = fileName;

    [RelayCommand] private void Transmit() => _owner.PlayRecording(this, local: false);
    [RelayCommand] private void Listen() => _owner.PlayRecording(this, local: true);

    [RelayCommand]
    private void Delete()
    {
        if (!ConfirmDelete) { ConfirmDelete = true; return; }
        _owner.DeleteRecording(this);
    }
}

/// <summary>
/// The recorder: REC records the receiver (what you hear, after the RX
/// filter) or the microphone into Music/SimpleHPSDR; a recording can be
/// played to the speakers or transmitted, where it goes through the TX filter
/// with the TX level set here. The engine does the audio; the app lists,
/// renames and deletes the files.
/// </summary>
public sealed partial class MainViewModel
{
    internal const string RxPrefix = "RX ", MicPrefix = "MIC ";

    public ObservableCollection<RecordingRow> Recordings { get; } = [];

    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private bool _isPlayingRecording;
    [ObservableProperty] private string _recordText = "REC";
    [ObservableProperty] private string _recorderStatus = "";
    private string? _recorderFolder;
    private DispatcherTimer? _recorderTimer;

    public bool RecorderFromMic
    {
        get => _settings.RecorderFromMic;
        set
        {
            if (_settings.RecorderFromMic == value) return;
            _settings.RecorderFromMic = value;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(RecorderFromReceiver));
            OnPropertyChanged(nameof(RecordSub));
            OnPropertyChanged(nameof(RecordSourceLabel));
        }
    }

    // A real property for the other radio button (a negated binding only works one way).
    public bool RecorderFromReceiver
    {
        get => !RecorderFromMic;
        set { if (value) RecorderFromMic = false; }
    }

    /// <summary>TX level for transmitted recordings, dB (the spinner).</summary>
    public decimal RecorderTxGainDb
    {
        get => (decimal)_settings.RecorderTxGainDb;
        set
        {
            double db = Math.Clamp((double)value, -20, 20);
            if (_settings.RecorderTxGainDb == db) return;
            _settings.RecorderTxGainDb = db;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public string RecordSourceLabel => RecorderFromMic ? "MIC" : "RX";
    public string RecordSub => IsRecording ? RecordText : RecorderFromMic ? "Mic" : "RX";
    partial void OnRecordTextChanged(string value) => OnPropertyChanged(nameof(RecordSub));
    partial void OnIsRecordingChanged(bool value) => OnPropertyChanged(nameof(RecordSub));

    public string RecordTip =>
        "REC: click to start or stop recording (red = recording). Right-click for your recordings: listen, transmit, rename, delete.";

    private string RecorderFolder => _recorderFolder ??= DefaultRecorderFolder();

    private static string DefaultRecorderFolder()
    {
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (string.IsNullOrEmpty(music))
            music = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");
        return Path.Combine(music, "SimpleHPSDR");
    }

    internal static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    /// <summary>Length from the WAV header, without reading the samples.</summary>
    internal static double WavSeconds(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var h = new byte[44];
            if (f.Read(h, 0, 44) < 44) return 0;
            int byteRate = BitConverter.ToInt32(h, 28);
            return byteRate > 0 ? (f.Length - 44) / (double)byteRate : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Reload the list (the flyout opening, a recording finishing).</summary>
    [RelayCommand]
    public async Task RefreshRecordingsAsync()
    {
        if (IsConnected && _client is not null)
        {
            try { ApplyRecorder(await _client.GetRecorderAsync()); } catch { }
        }
        ReloadRecordingList();
    }

    private void ReloadRecordingList(bool selectNewest = false)
    {
        string? keep = selectNewest ? null : DeckSelected?.FileName;
        Recordings.Clear();
        if (Directory.Exists(RecorderFolder))
        {
            var files = new DirectoryInfo(RecorderFolder).GetFiles("*.wav")
                .Where(f => f.Name.StartsWith(RxPrefix, StringComparison.OrdinalIgnoreCase)
                         || f.Name.StartsWith(MicPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc);
            foreach (var f in files) Recordings.Add(new RecordingRow(this, f.FullName));
        }
        DeckSelected = Recordings.FirstOrDefault(r => r.FileName == keep) ?? Recordings.FirstOrDefault();
    }

    // ---- the tape deck that rolls out beside TX ----

    /// <summary>The deck beside TX is rolled out.</summary>
    [ObservableProperty] private bool _deckOpen;
    /// <summary>The recording Play and TX use.</summary>
    [ObservableProperty] private RecordingRow? _deckSelected;
    [ObservableProperty] private string _deckCounter = "0:00";

    public string DeckTip =>
        "REC: click to roll the recorder out or away. Right-click for the list of recordings (rename, delete, open folder).";

    [RelayCommand]
    private async Task ToggleDeckAsync()
    {
        DeckOpen = !DeckOpen;
        if (DeckOpen) await RefreshRecordingsAsync();
    }

    [RelayCommand]
    private void ToggleRecordSource() => RecorderFromMic = !RecorderFromMic;

    /// <summary>Stop: the recording, or the playback.</summary>
    [RelayCommand]
    private Task DeckStopAsync() => Try(async () =>
    {
        if (IsRecording)
        {
            ApplyRecorder(await _client!.StopRecordingAsync());
            ReloadRecordingList(selectNewest: true);
        }
        if (IsPlayingRecording) ApplyRecorder(await _client!.StopPlaybackAsync());
    });

    /// <summary>▶ transmits instead of playing to the speakers. Off at every start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeckPlayTip))]
    private bool _deckTxArmed;

    public string DeckPlayTip => DeckTxArmed
        ? "Transmit the chosen recording at the TX level. MOX or ■ ends it."
        : "Play the chosen recording through your speakers. Turn TX on to transmit it.";

    [RelayCommand]
    private void ToggleDeckTx() => DeckTxArmed = !DeckTxArmed;

    [RelayCommand]
    private void DeckPlay() { if (DeckSelected is { } r) PlayRecording(r, local: !DeckTxArmed); }

    [RelayCommand]
    private Task ToggleRecordAsync() => Try(async () =>
    {
        if (IsRecording)
        {
            ApplyRecorder(await _client!.StopRecordingAsync());
            ReloadRecordingList(selectNewest: true);
            return;
        }
        var now = DateTime.Now;
        string name = RecorderFromMic
            ? $"{MicPrefix}{now:yyyy-MM-dd HHmmss}.wav"
            : $"{RxPrefix}{now:yyyy-MM-dd HHmmss} {VfoHz / 1e6:0.000} {ModeLabel}.wav";
        ApplyRecorder(await _client!.StartRecordingAsync(name, RecorderFromMic, ActiveRx));
        Status = RecorderFromMic ? "Recording the microphone." : "Recording what you hear.";
        StartRecorderTimer();
    });

    internal void PlayRecording(RecordingRow row, bool local) => PlayFile(row.FileName, row.IsMic, local);

    internal void PlayFile(string fileName, bool isMic, bool local) => _ = Try(async () =>
    {
        if (IsPlayingRecording) await _client!.StopPlaybackAsync();
        ApplyRecorder(await _client!.PlayRecordingAsync(fileName, (double)RecorderTxGainDb, isMic, local));
        StartRecorderTimer();
    });

    [RelayCommand]
    private Task StopRecordingPlaybackAsync() => Try(async () => ApplyRecorder(await _client!.StopPlaybackAsync()));

    internal void Rename(RecordingRow row)
    {
        string label = string.Concat(row.Label.Trim().Split(Path.GetInvalidFileNameChars()));
        string current = Path.GetFileNameWithoutExtension(row.FileName)[row.Prefix.Length..];
        if (label.Length == 0) { row.Label = current; return; }
        if (label == current) return;
        string target = row.Prefix + label + ".wav";
        var from = Path.Combine(RecorderFolder, row.FileName);
        var to = Path.Combine(RecorderFolder, target);
        try
        {
            if (File.Exists(to)) { Status = $"There's already a recording called {label}."; row.Label = current; return; }
            File.Move(from, to);
            VoiceKeyerFileRenamed(row.FileName, target);
            row.Renamed(target);
            row.Label = label;
            OnPropertyChanged(nameof(DeckSelected));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = $"Couldn't rename it: {e.Message}";
            row.Label = current;
        }
    }

    internal void DeleteRecording(RecordingRow row)
    {
        try
        {
            File.Delete(Path.Combine(RecorderFolder, row.FileName));
            bool wasSelected = ReferenceEquals(DeckSelected, row);
            Recordings.Remove(row);
            if (wasSelected) DeckSelected = Recordings.FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = $"Couldn't delete it: {e.Message}";
        }
    }

    [RelayCommand]
    private void OpenRecordingsFolder()
    {
        try
        {
            Directory.CreateDirectory(RecorderFolder);
            string tool = OperatingSystem.IsMacOS() ? "open" : OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open";
            var start = new ProcessStartInfo(tool) { UseShellExecute = false };
            start.ArgumentList.Add(RecorderFolder);
            Process.Start(start)?.Dispose();
        }
        catch (Exception e)
        {
            Status = $"Couldn't open the folder: {e.Message}";
        }
    }

    private void StartRecorderTimer()
    {
        if (_recorderTimer is null)
        {
            _recorderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _recorderTimer.Tick += async (_, _) =>
            {
                if (!IsConnected || _client is null) { _recorderTimer.Stop(); return; }
                try { ApplyRecorder(await _client.GetRecorderAsync()); } catch { }
            };
        }
        _recorderTimer.Start();
    }

    private void ApplyRecorder(RecorderStatus s)
    {
        _recorderFolder = s.Folder;
        bool wasRecording = IsRecording;
        IsRecording = s.Recording;
        IsPlayingRecording = s.Playing;
        RecordText = s.Recording ? Clock(s.RecordingSeconds) : "REC";
        DeckCounter = Clock(s.Recording ? s.RecordingSeconds : s.Playing ? s.PlayPositionSeconds : 0);
        if (s.Playing && s.PlayingFile is { } file)
        {
            string label = Path.GetFileNameWithoutExtension(file);
            RecorderStatus = $"{(s.PlayingLocal ? "Playing" : "Transmitting")} {label}  {Clock(s.PlayPositionSeconds)} / {Clock(s.PlayLengthSeconds)}";
        }
        else if (s.Recording)
            RecorderStatus = $"Recording {(s.RecordingSource == "mic" ? "the microphone" : "the receiver")}  {Clock(s.RecordingSeconds)}";
        else
            RecorderStatus = s.LastError ?? "";
        if (wasRecording && !s.Recording) ReloadRecordingList(selectNewest: true);
        foreach (var slot in VoiceKeyerSlots)
            slot.IsPlaying = s.Playing && !s.PlayingLocal && s.PlayingFile == slot.FileName;
        if (!s.Recording && !s.Playing) _recorderTimer?.Stop();
    }
}

public static class RecorderPanelText
{
    public static readonly Avalonia.Data.Converters.IValueConverter DeleteLabel =
        new Avalonia.Data.Converters.FuncValueConverter<bool, string>(sure => sure ? "Sure?" : "Delete");
    public static readonly Avalonia.Data.Converters.IValueConverter DeleteGlyph =
        new Avalonia.Data.Converters.FuncValueConverter<bool, string>(sure => sure ? "Sure?" : "✕");
}
