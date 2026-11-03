using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        FrequencyPanel.PointerWheelChanged += (_, e) =>
        {
            Vm?.TuneBySteps(Math.Sign(e.Delta.Y));
            e.Handled = true;
        };
        // Click the frequency to type one; Enter tunes, Esc or clicking away cancels.
        FrequencyPanel.PointerPressed += (_, e) =>
        {
            if (Vm is not { EditingFrequency: false } vm || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            vm.BeginFrequencyEntry();
            Dispatcher.UIThread.Post(() => FrequencyEntryBox.Focus(), DispatcherPriority.Input);
            e.Handled = true;
        };
        FrequencyEntryBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Vm?.EnterFrequencyCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Escape) { Vm?.CancelFrequencyEntry(); e.Handled = true; }
        };
        FrequencyEntryBox.LostFocus += (_, _) => Vm?.CancelFrequencyEntry();
        Spectrum.TuneRequested += hz => Vm?.TuneTo(hz);
        Spectrum.WheelTune += steps => Vm?.TuneBySteps(steps);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // The filter editor loads the slot's stored values each time it opens.
    private void OnFilterEditOpening(object? sender, EventArgs e)
    {
        if (sender is Flyout { Content: Control { DataContext: FilterSlot slot } }) slot.BeginEdit();
    }

    // A button's Click runs before its Command; closing the popup right away
    // would detach the button and the command would never run, so close
    // once the click has finished.
    private void CloseFlyout(object? sender, RoutedEventArgs e)
    {
        var popup = (sender as Control)?.FindLogicalAncestorOfType<Popup>();
        Dispatcher.UIThread.Post(() => popup?.Close());
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is not { } vm) return;
        vm.ShowRadioPicker = picker => new RadioPickerWindow { DataContext = picker }.PickAsync(this);
        vm.ShowSettings = settings => new SettingsWindow { DataContext = settings }.ShowDialog(this);
        vm.Display += f =>
        {
            Spectrum.VfoHz = vm.VfoHz;
            Spectrum.FilterLowHz = vm.FilterLowHz;
            Spectrum.FilterHighHz = vm.FilterHighHz;
            Spectrum.PushFrame(f);
            vm.FollowNoiseFloor(Spectrum.NoiseFloorDb);
        };
        vm.DisplayReset += Spectrum.Clear;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Vm is { } vm) await vm.StartAsync();
    }
}
