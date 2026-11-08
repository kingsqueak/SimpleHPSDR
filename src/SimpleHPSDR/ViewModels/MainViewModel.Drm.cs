using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>DRM, set up like SWL: chosen from the Mode menu, with its button beside Mode,
/// the broadcast list, and the box on the waterfall. Raw IQ comes from the engine's TCI
/// server, before WDSP, and the decoder plays its own audio, so the engine's receive
/// audio stays muted while DRM is on. Mute and AF act on the DRM audio then. Receive only.
/// </summary>
public sealed partial class MainViewModel
{
    public const string DrmChoice = "DRM";
    private const int DrmFilterHz = 5000;
    private static readonly TimeSpan DrmPoll = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan NoAudioAfter = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MirrorHunt = TimeSpan.FromSeconds(10);

    private nint _drm;
    private TciIq? _drmIq;
    private DispatcherTimer? _drmTimer;
    private double _drmShiftHz;
    private bool _drmMirror = true;   // the engine's IQ is mirrored
    private bool _ctunBeforeDrm;
    private long _drmVfo, _drmLastAudioOk = -1;
    private DateTime _drmAudioAt, _drmHuntFrom;

    [ObservableProperty] private bool _drmActive;
    public ObservableCollection<Choice> DrmServices { get; } = [];
    public bool DrmHasServices => DrmServices.Count > 1;

    private StationGuideViewModel? _drmGuide;
    private StationGuideViewModel DrmGuide
    {
        get
        {
            if (_drmGuide is null)
            {
                _drmGuide = new StationGuideViewModel(_settings, TuneToStation, drm: true);
                _drmGuide.Changed += () => { if (DrmActive) ShowDrmStation(null); };
            }
            return _drmGuide;
        }
    }

    /// <summary>The list for whichever of SWL and DRM is on.</summary>
    public StationGuideViewModel ActiveGuide => DrmActive ? DrmGuide : Guide;

    private void Drm(object o)
    {
        lock (_drmLock)
            if (_drm != 0) DrmNative.Command(_drm, JsonSerializer.Serialize(o));
    }

    private double DrmGain => Muted || IsTransmitting ? 0 : Math.Min(Math.Pow(10, AfGainDb / 20), 4);
    private void UpdateDrmVolume() { if (_drm != 0) Drm(new { cmd = "volume", gain = DrmGain }); }

    private bool _drmStarting;
    private readonly object _drmLock = new();   // the native decoder isn't freed while IQ or a poll is in it

    /// <summary>Keyed since DRM start began: say so and stop before touching the radio.</summary>
    private bool DrmStartKeyed()
    {
        if (!IsTransmitting) return false;
        Status = "Unkey before changing mode.";
        return true;
    }

    private async Task SelectDrmAsync()
    {
        if (!IsConnected || DrmActive || _drmStarting || DrmStartKeyed()) return;
        _drmStarting = true;
        try { await StartDrmAsync(); }
        finally { _drmStarting = false; }
    }

    private async Task StartDrmAsync()
    {
        int port;
        string host;
        try
        {
            var tci = await _client!.GetTciStatusAsync();
            if (!tci.CurrentlyEnabled)
            {
                Status = "DRM needs the TCI server: turn it on in Config > CAT & TCI.";
                return;
            }
            port = tci.CurrentPort;
            host = tci.CurrentBindAddress is null or "" or "0.0.0.0" or "::" or "*" ? "127.0.0.1" : tci.CurrentBindAddress;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException)
        {
            Status = $"DRM not started: {ex.Message}";
            return;
        }
        // Every await is a chance for PTT or VOX to key: check again before each radio change.
        if (!IsConnected || DrmStartKeyed()) return;

        if (SwlActive) TurnSwlOff();
        var hadMemory = _settings.ModeMemories.ContainsKey(MemoryKey(BandOf(VfoHz), RxMode.SAM));
        await SelectModeAsync(RxMode.SAM);
        if (!IsConnected || DrmStartKeyed()) return;
        if (!hadMemory) await ApplyFilterAsync(-DrmFilterHz, DrmFilterHz);
        if (!IsConnected || DrmStartKeyed()) return;

        // A list click has to move the radio and the display, so CTUN goes off
        // for as long as DRM is on and comes back afterwards.
        _ctunBeforeDrm = CtunOn;
        CtunOn = false;

        _drm = DrmNative.Create(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["play"] = true,
            ["volume"] = DrmGain,
            ["data_dir"] = Directory.CreateDirectory(Path.Combine(EngineHost.DataDir, "drm")).FullName,
        }));
        (_drmLastAudioOk, _drmVfo, _drmMirror) = (-1, 0, true);
        _drmHuntFrom = DateTime.UtcNow;
        DrmActive = true;
        OnPropertyChanged(nameof(ActiveGuide));
        await Try(() => _client!.SetMuteAsync(true));   // the decoder's audio replaces the engine's

        var iq = new TciIq(host, port, ActiveRx);
        var handle = _drm;
        iq.IqReceived += (samples, rate) =>
        {
            lock (_drmLock)
                if (_drm == handle) DrmNative.PushIq(handle, samples, rate, _drmShiftHz, _drmMirror);
        };
        iq.Changed += () =>
        {
            _drmShiftHz = iq.VfoHz - iq.DdsHz;
            Dispatcher.UIThread.Post(OnDrmTuned);
        };
        _drmIq = iq;
        _drmTimer = new DispatcherTimer(DrmPoll, DispatcherPriority.Normal, (_, _) => PollDrm());
        _drmTimer.Start();

        SwlActive = false;
        SwlStationShown = true;
        ShowDrmStation(null);
        DrmGuide.Start();
        ShowGuide?.Invoke();
    }

    [RelayCommand]
    private void TurnDrmOff()
    {
        if (!DrmActive) return;
        StopDrmDecoder();
        DrmGuide.Stop();
        CloseGuide?.Invoke();
        SwlStationShown = false;
        OnPropertyChanged(nameof(ActiveGuide));
        if (IsConnected) _ = Try(() => _client!.SetMuteAsync(Muted));
        CtunOn = _ctunBeforeDrm;
    }

    private void StopDrmDecoder()
    {
        DrmActive = false;
        _drmTimer?.Stop();
        _drmTimer = null;
        _drmIq?.Dispose();
        _drmIq = null;
        lock (_drmLock)
        {
            if (_drm != 0) DrmNative.Destroy(_drm);
            _drm = 0;
        }
        DrmServices.Clear();
        OnPropertyChanged(nameof(DrmHasServices));
    }

    private void OnDrmTuned()
    {
        if (_drmIq is not { VfoHz: not 0 } iq || iq.VfoHz == _drmVfo) return;
        bool moved = Math.Abs(iq.VfoHz - _drmVfo) > 200;
        _drmVfo = iq.VfoHz;
        if (!moved) return;
        Drm(new { cmd = "new_station" });
        _drmHuntFrom = DateTime.UtcNow;
        DrmServices.Clear();
        OnPropertyChanged(nameof(DrmHasServices));
        ShowDrmStation(null);
    }

    private void PollDrm()
    {
        string json;
        lock (_drmLock)
        {
            if (_drm == 0) return;
            DrmNative.Events(_drm);
            json = DrmNative.Status(_drm);
        }
        if (json.Length == 0) return;
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (DrmStr(r, "state") is not "Acquisition") _drmHuntFrom = DateTime.UtcNow;
        else if (Math.Abs(_drmShiftHz) > 100 && DateTime.UtcNow - _drmHuntFrom > MirrorHunt)
        {
            _drmMirror = !_drmMirror;   // no lock off-centre: try it the other way up
            _drmHuntFrom = DateTime.UtcNow;
        }
        UpdateDrmServices(r);
        ShowDrmStation(r);
    }

    private void UpdateDrmServices(JsonElement r)
    {
        var audio = r.GetProperty("services").EnumerateArray()
            .Where(s => s.GetProperty("audio").GetBoolean()).ToList();
        int selected = r.TryGetProperty("selected", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;
        var labels = audio.Select(s => DrmStr(s, "label") ?? "").ToList();
        if (!labels.SequenceEqual(DrmServices.Select(c => c.Label)))
        {
            DrmServices.Clear();
            foreach (var s in audio)
            {
                int id = s.GetProperty("id").GetInt32();
                DrmServices.Add(new Choice(DrmStr(s, "label") ?? "", id, _ => Drm(new { cmd = "service", id })));
            }
            OnPropertyChanged(nameof(DrmHasServices));
        }
        foreach (var c in DrmServices) c.IsSelected = Equals(c.Value, selected);
    }

    private static string? DrmStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? DrmNum(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>The waterfall box: what the signal says, or what's scheduled there until it does.</summary>
    private void ShowDrmStation(JsonElement? status)
    {
        var scheduled = _drmGuide?.OnAirAt(VfoHz / 1000.0) ?? [];
        var listed = scheduled.Count > 0 ? scheduled[0].Station : "";
        if (status is not { } r)
        {
            SwlStationName = listed != "" ? listed : "DRM";
            SwlStationDetail = "Searching for a DRM signal";
            SwlStationMore = "";
            SwlHasMore = false;
            return;
        }
        var state = DrmStr(r, "state") ?? "Acquisition";
        var snr = DrmNum(r, "snr_db");
        var audioOk = (long)(DrmNum(r, "audio_ok") ?? 0);
        if (audioOk != _drmLastAudioOk)
        {
            if (_drmLastAudioOk >= 0) _drmAudioAt = DateTime.UtcNow;
            _drmLastAudioOk = audioOk;
        }
        bool playing = DateTime.UtcNow - _drmAudioAt < NoAudioAfter;
        var service = DrmServices.FirstOrDefault(c => c.IsSelected)?.Label ?? "";
        var needs = DrmNum(r, "needs_snr_db");
        var condition = true switch
        {
            _ when _drmIq is not { Connected: true } => "Waiting for TCI",
            _ when playing => "Playing",
            _ when state == "Locked" && needs is { } n && (snr ?? 0) < n => $"Too weak: needs about {n:0} dB",
            _ when state == "Locked" => "Locked, waiting for audio",
            _ when state == "Tracking" => "Syncing",
            _ => "Searching for a DRM signal",
        };
        var signal = state == "Acquisition" ? [] : new[]
        {
            DrmStr(r, "mode") is { } m ? $"Mode {m}" : "",
            DrmNum(r, "occupancy_khz") is { } k ? $"{k:0.#} kHz" : "",
            DrmStr(r, "msc_mode") ?? "",
            DrmStr(r, "codec") ?? "",
            snr is { } s ? $"SNR {s:0.0} dB" : "",
        };
        SwlStationName = service != "" ? service : listed != "" ? listed : "DRM";
        SwlStationDetail = string.Join(" · ", new[] { condition }.Concat(signal).Where(t => t != ""));
        SwlStationMore = DrmStr(r, "text")?.Trim() ?? "";
        SwlHasMore = SwlStationMore != "";
    }
}
