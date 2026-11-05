using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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
    private TimeSpan? _shownOffset;

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
    }
}
