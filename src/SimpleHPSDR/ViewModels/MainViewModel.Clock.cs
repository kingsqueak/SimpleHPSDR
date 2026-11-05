using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private void ShowIdCountdown(DateTimeOffset now)
    {
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
