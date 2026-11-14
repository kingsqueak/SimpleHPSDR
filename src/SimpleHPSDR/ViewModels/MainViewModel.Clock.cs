using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Contracts;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>The clock in the bottom corner: local and Zulu time, whether or
/// not a radio is connected.</summary>
public sealed partial class MainViewModel
{
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    [ObservableProperty] private string _localTimeText = "";
    [ObservableProperty] private string _zuluTimeText = "";
    [ObservableProperty] private string _localZoneText = "";
    [ObservableProperty] private string _idCountdownText = "00:00";
    [ObservableProperty] private bool _idTimerRunning;
    [ObservableProperty] private bool _idTimerAtMark;
    [ObservableProperty] private string _idLabel = "ID";
    private TimeSpan? _shownOffset;
    private int _idMinute = -1;
    private bool _idDue;

    private void StartClock()
    {
        ShowTime();
        _clockTimer.Tick += (_, _) => ShowTime();
        _clockTimer.Start();
    }

    // Ticks faster than the display changes, so seconds never look skipped.
    private void ShowTime()
    {
        var now = DateTimeOffset.Now;
        // The zone's name changes with daylight saving, and only then.
        if (now.Offset != _shownOffset)
        {
            _shownOffset = now.Offset;
            LocalZoneText = TimeZoneLabel.Local();
        }
        LocalTimeText = now.ToString("HH:mm:ss");
        ZuluTimeText = now.UtcDateTime.ToString("HH:mm:ss");
        CheckIdTimer(now);
    }

    // Above the clock: ID 00:00 while off, the time to the next mark while on.
    // With CW ID on, the engine's timer instead: time to the next ID, "next
    // over" when one is owed, "sending" while it's on the air.
    private void ShowIdCountdown(DateTimeOffset now)
    {
        IdLabel = _settings.CwIdAuto ? "CW ID" : "ID";
        if (_settings.CwIdAuto && IsConnected && _cwId is { } cw && (cw.Running || cw.FinalIdArmed))
        {
            IdTimerRunning = true;
            IdTimerAtMark = cw.Sending || cw.Due || cw.FinalIdArmed;
            IdCountdownText = cw.Sending ? "sending" : cw.Due || cw.FinalIdArmed ? "next over"
                : cw.SecondsUntilDue is int s ? $"{s / 60:00}:{s % 60:00}" : "ready";
            return;
        }
        IdTimerRunning = _settings.IdTimerOn;
        int left = 600 - (now.Minute % 10 * 60 + now.Second);
        // Green from 5 s before the mark to 5 s after, and while one is waiting.
        IdTimerAtMark = IdTimerRunning && (_idDue || left <= 5 || left > 595);
        IdCountdownText = !IdTimerRunning ? "00:00" : _idDue ? "now" : $"{left / 60:00}:{left % 60:00}";
    }

    [RelayCommand]
    private void ToggleIdTimer()
    {
        _settings.IdTimerOn = !_settings.IdTimerOn;
        _settings.Save();
        ShowTime();
    }

    /// <summary>CW ID: the engine sends the callsign in CW, mixed into your
    /// voice transmissions, at least every interval. It never keys the radio
    /// for it. Turning it off sends one last ID on your next over.</summary>
    public bool CwIdAuto
    {
        get => _settings.CwIdAuto;
        set
        {
            if (_settings.CwIdAuto == value) return;
            _settings.CwIdAuto = value;
            _settings.Save();
            OnPropertyChanged();
            if (value) Status = "CW ID on: your callsign goes out in CW during your next voice over, then at least every interval.";
            _ = SyncCwIdAsync();
            ShowTime();
        }
    }

    [RelayCommand]
    private void ToggleCwId() => CwIdAuto = !CwIdAuto;

    // Config can turn CW ID on or off too; the label changes with it.
    partial void OnIdLabelChanged(string value) => OnPropertyChanged(nameof(CwIdAuto));

    private CwIdStatusDto? _cwId;
    private bool _cwIdBusy;
    private DateTime _cwIdCallCheckedAt;

    /// <summary>Make the engine's CW ID match the setting (connect, engine
    /// restart, Config) and read its state for the ID box. From the 1 s poll.</summary>
    private async Task SyncCwIdAsync()
    {
        if (!IsConnected || _client is not { } client || _cwIdBusy) return;
        _cwIdBusy = true;
        try
        {
            var st = await client.GetCwIdAsync();
            bool want = _settings.CwIdAuto;
            // The callsign can change in Config: check it now and then.
            bool recheck = want && st.Running && DateTime.UtcNow - _cwIdCallCheckedAt > TimeSpan.FromSeconds(10);
            if (want && (!st.Running || recheck))
            {
                _cwIdCallCheckedAt = DateTime.UtcNow;
                string call = ((await client.GetOperatorAsync()).Callsign ?? "").Trim().ToUpperInvariant();
                if (call.Length == 0)
                {
                    if (st.Running) await client.StopCwIdAsync();
                    _settings.CwIdAuto = false;
                    _settings.Save();
                    OnPropertyChanged(nameof(CwIdAuto));
                    Status = "CW ID needs your callsign: set it in Config > Station.";
                }
                else if (!st.Running || !string.Equals(st.Callsign, call, StringComparison.OrdinalIgnoreCase))
                {
                    await client.StartCwIdAsync(call);
                }
                st = await client.GetCwIdAsync();
            }
            else if (!want && st.Running)
            {
                await client.StopCwIdAsync();
                st = await client.GetCwIdAsync();
            }
            _cwId = st;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException)
        {
            Status = $"CW ID: {ex.Message}";
        }
        finally { _cwIdBusy = false; }
    }

    // ID timer: at each even ten minutes, once, while connected. A mark that
    // falls in a transmission waits for the unkey.
    private void CheckIdTimer(DateTimeOffset now)
    {
        int minute = now.Hour * 60 + now.Minute;
        if (now.Minute % 10 == 0 && minute != _idMinute)
        {
            _idMinute = minute;
            _idDue = _settings.IdTimerOn && IsConnected;
        }
        ShowIdCountdown(now);
        if (!_idDue || IsTransmitting) return;
        _idDue = false;
        if (_settings.IdTimerOn && IsConnected) _ = IdReminder.PlayAsync(_settings.IdTimerWav, _settings.IdTimerVolume);
    }
}
