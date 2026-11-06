using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using SimpleHPSDR.Controls;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Views;

/// <summary>A small scalable view: VFO, meters, Mute and a momentary TX. It
/// stands in for the main window, and closing it brings that back.</summary>
public partial class MiniWindow : Window
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public MiniWindow()
    {
        InitializeComponent();
        FrequencyPanel.PointerWheelChanged += (_, e) =>
        {
            Vm?.TuneBySteps(Math.Sign(SliderWheel.Amount(e)));
            e.Handled = true;
        };
        FrequencyPanel.PointerPressed += (_, e) =>
        {
            if (Vm is not { EditingFrequency: false } vm) return;
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

        FullButton.Click += (_, _) => Close();
        MuteButton.PointerPressed += (_, _) => { if (Vm is { } vm) vm.Muted = !vm.Muted; };

        // Momentary: anything that ends the press ends transmit.
        TxButton.PointerPressed += (_, e) =>
        {
            e.Pointer.Capture(TxButton);
            Vm?.MomentaryTxDown();
            e.Handled = true;
        };
        TxButton.PointerReleased += (_, e) =>
        {
            Vm?.MomentaryTxUp();
            e.Pointer.Capture(null);
        };
        TxButton.PointerCaptureLost += (_, _) => Vm?.MomentaryTxUp();
        Deactivated += (_, _) => Vm?.MomentaryTxUp();
        Closed += (_, _) => Vm?.MomentaryTxUp();
    }
}
