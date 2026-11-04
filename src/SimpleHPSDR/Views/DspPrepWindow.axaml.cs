using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Views;

public partial class DspPrepWindow : Window
{
    private readonly CancellationTokenSource _stop = new();

    public DspPrepWindow() => InitializeComponent();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var vm = (DspPrepViewModel)DataContext!;
        vm.Finished += () => Dispatcher.UIThread.Post(Close);
        _ = vm.RunAsync(_stop.Token);
    }

    // Closing it early only hides the window: the engine carries on, and
    // Connect waits for it with a line in the status bar.
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _stop.Cancel();
    }

    private void OnContinue(object? sender, RoutedEventArgs e) => Close();
}
