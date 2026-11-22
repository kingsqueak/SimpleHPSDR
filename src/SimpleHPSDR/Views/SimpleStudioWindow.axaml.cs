using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Views;

/// <summary>
/// SimpleStudio: the mic effects chain. Non-modal; Config opens it from the
/// tick box on its TX Audio page and closes it with itself. Meters are read
/// about 15 times a second, only while this window is showing.
/// </summary>
public partial class SimpleStudioWindow : Window
{
    private readonly DispatcherTimer _meters = new() { Interval = TimeSpan.FromMilliseconds(66) };

    public SimpleStudioWindow()
    {
        InitializeComponent();
        _meters.Tick += (_, _) =>
        {
            if (IsVisible && WindowState != WindowState.Minimized)
                _ = (DataContext as SimpleStudioViewModel)?.RefreshMetersAsync();
        };
        Opened += (_, _) => _meters.Start();
        Closed += (_, _) =>
        {
            _meters.Stop();
            (DataContext as SimpleStudioViewModel)?.ClearMeters();
        };
        // Double-click a slider: back to the catalogue's default.
        AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source) return;
        var slider = source as Slider ?? source.FindAncestorOfType<Slider>();
        if (slider?.DataContext is StudioParamRow row)
        {
            row.Reset();
            e.Handled = true;
        }
    }

    // A click on a tile's free area selects it; its tick box and buttons
    // handle their own clicks.
    private void OnTilePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: StudioEffectViewModel effect } && DataContext is SimpleStudioViewModel vm)
            vm.Select(effect);
    }
}
