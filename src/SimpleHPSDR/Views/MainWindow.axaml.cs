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
        Spectrum.SliceClicked += rx => _ = Vm?.MakeActiveAsync(rx);
        Spectrum.MenuRequested += ShowSliceMenu;
        Spectrum.WheelTune += steps => Vm?.TuneBySteps(steps);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // Right-click on the panadapter: add, select, move, mute, remove slices and
    // choose which one transmits.
    private void ShowSliceMenu(long hz)
    {
        if (Vm is not { IsConnected: true } vm) return;
        string at = $"{hz / 1e6:0.000000} MHz";
        var items = new List<Control>();
        MenuItem Item(string header, Func<Task> act) =>
            new() { Header = header, Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(act) };

        if (!vm.SliceBEnabled)
        {
            items.Add(Item($"Add slice B at {at}", () => vm.AddSliceBAsync(hz)));
        }
        else
        {
            int other = vm.ActiveRx == 0 ? 1 : 0;
            string o = other == 0 ? "A" : "B";
            items.Add(Item($"Make slice {o} active", () => vm.MakeActiveAsync(other)));
            items.Add(Item($"Move slice B here ({at})", () => vm.MoveSliceBAsync(hz)));
            items.Add(new Separator());
            for (int rx = 0; rx <= 1; rx++)
            {
                int r = rx;
                string n = r == 0 ? "A" : "B";
                if (vm.TxRx != r) items.Add(Item($"Transmit on slice {n}", () => vm.TransmitOnAsync(r)));
            }
            items.Add(new Separator());
            for (int rx = 0; rx <= 1; rx++)
            {
                int r = rx;
                string n = r == 0 ? "A" : "B";
                bool muted = vm.IsSliceMuted(r);
                items.Add(Item(muted ? $"Unmute slice {n}" : $"Mute slice {n}", () => vm.SetSliceMuteAsync(r, !muted)));
            }
            items.Add(new Separator());
            items.Add(Item("Remove slice B", () => vm.RemoveSliceBAsync()));
        }
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(Spectrum);
    }

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
            Spectrum.Slices = vm.SliceMarks();
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
