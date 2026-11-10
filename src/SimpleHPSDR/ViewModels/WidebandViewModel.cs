using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// The wideband view: 0-60 MHz straight from the radio's ADC, in its own
/// window. It only reads: the radio's frequency, mode and display are left
/// as they are. Polling keeps the engine's wideband feed running; when the
/// window closes the polls stop and the engine turns the feed off by itself.
/// </summary>
public sealed partial class WidebandViewModel : ObservableObject
{
    private readonly Func<StationClient?> _client;
    private readonly Func<long> _vfo;
    private DispatcherTimer? _timer;
    private bool _polling;
    private long _lastSeq;

    [ObservableProperty] private StationClient.WidebandRow? _row;
    [ObservableProperty] private long _vfoHz;
    [ObservableProperty] private string _status = "Starting…";

    public WidebandViewModel(Func<StationClient?> client, Func<long> vfo)
    {
        _client = client;
        _vfo = vfo;
    }

    public void Start()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    private async Task PollAsync()
    {
        if (_polling) return;   // one poll in flight at a time
        _polling = true;
        try
        {
            VfoHz = _vfo();
            if (_client() is not { } c) { Status = "Connect to a radio to see the wideband view."; return; }
            var row = await c.GetWidebandAsync();
            if (row is null) { Status = "Waiting for the radio…"; return; }
            if (row.Seq == _lastSeq) return;
            _lastSeq = row.Seq;
            Status = "";
            Row = row;
        }
        catch (StationException e) { Status = e.Message; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { Status = "The engine isn't answering."; }
        finally { _polling = false; }
    }
}
