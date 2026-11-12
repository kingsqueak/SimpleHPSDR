using Avalonia.Threading;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Controls;

/// <summary>Polls the engine for the high-resolution filter close-up about 15
/// times a second while the close-up is on screen and High resolution is on.
/// The engine only runs it while asked, so nothing is spent otherwise.</summary>
public sealed class CloseUpFeed
{
    private readonly FilterScope _scope;
    private readonly Func<MainViewModel?> _vm;
    private readonly Func<bool> _shown;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(66) };
    private bool _busy;

    public CloseUpFeed(FilterScope scope, Func<MainViewModel?> vm, Func<bool> shown)
    {
        (_scope, _vm, _shown) = (scope, vm, shown);
        _timer.Tick += async (_, _) => await TickAsync();
        scope.AttachedToVisualTree += (_, _) => _timer.Start();
        scope.DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    private async Task TickAsync()
    {
        if (_busy || !_shown() || _vm() is not { CloseUpHighRes: true } vm) return;
        _busy = true;
        try
        {
            if (await vm.FetchCloseUpAsync() is { } c)
            {
                _scope.AverageSeconds = vm.CloseUpAverageSec;
                _scope.PushCloseUp(c.Db, c.RateHz);
            }
        }
        finally { _busy = false; }
    }
}
