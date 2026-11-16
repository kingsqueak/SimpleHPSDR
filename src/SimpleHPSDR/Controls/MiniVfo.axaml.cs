using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Controls;

/// <summary>
/// A receiver's small VFO on its panadapter, shown while RX2 is on: the
/// receiver's name, a TX mark (click to transmit there), its frequency (wheel
/// to tune, click to type one) and mode. RX2's also has the antenna switch on
/// its left and a ✕ that turns RX2 off. Tuning here gives that receiver the
/// controls first, as a click on its panadapter does.
/// </summary>
public partial class MiniVfo : UserControl
{
    public static readonly StyledProperty<int> RxProperty = AvaloniaProperty.Register<MiniVfo, int>(nameof(Rx));
    /// <summary>0 = RX1, 1 = RX2.</summary>
    public int Rx { get => GetValue(RxProperty); set => SetValue(RxProperty, value); }

    public static readonly StyledProperty<bool> TouchProperty = AvaloniaProperty.Register<MiniVfo, bool>(nameof(Touch));
    /// <summary>Touch layout: larger, and a tap on the frequency gives the
    /// receiver the controls (the main frequency's keypad types one).</summary>
    public bool Touch { get => GetValue(TouchProperty); set => SetValue(TouchProperty, value); }

    private static readonly IBrush[] SliceBrush =
        [new SolidColorBrush(Color.Parse("#F25F5C")), new SolidColorBrush(Color.Parse("#F5A524"))];
    private static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#40FFFFFF"));
    private static readonly IBrush TxRed = new SolidColorBrush(Color.Parse("#A8232A"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#8A93A3"));

    private MainViewModel? _wired;

    public MiniVfo()
    {
        InitializeComponent();
        Box.PointerWheelChanged += (_, e) =>
        {
            e.Handled = true;
            int clicks = SliderWheel.Clicks(Box, e);
            if (clicks != 0) _ = Vm?.TuneSliceByStepsAsync(Rx, clicks);
        };
        // Presses stop here, so they don't tune the panadapter underneath.
        Box.PointerPressed += (_, e) => e.Handled = true;
        FreqHost.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (Vm is not { IsConnected: true } vm || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (Touch) { _ = vm.MakeActiveAsync(Rx); return; }
            if (Entry.IsVisible) return;
            Entry.Text = "";
            Entry.IsVisible = true;
            Freq.IsVisible = false;
            Dispatcher.UIThread.Post(() => Entry.Focus(), DispatcherPriority.Input);
        };
        Entry.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                string text = Entry.Text ?? "";
                EndEntry();
                _ = Vm?.EnterRxFrequencyAsync(Rx, text);
            }
            else if (e.Key == Key.Escape) { e.Handled = true; EndEntry(); }
        };
        Entry.LostFocus += (_, _) => EndEntry();
        TxChip.PointerPressed += (_, e) => { e.Handled = true; _ = Vm?.TransmitOnAsync(Rx); };
        CloseChip.PointerPressed += (_, e) => { e.Handled = true; _ = Vm?.RemoveSliceBAsync(); };
        AntChip.PointerPressed += (_, e) => { e.Handled = true; Vm?.ToggleSliceBInput(); };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void EndEntry()
    {
        Entry.IsVisible = false;
        Freq.IsVisible = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RxProperty || change.Property == TouchProperty) Refresh();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Wire();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Wire();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unwire();
    }

    private void Wire()
    {
        var vm = VisualRoot is null ? null : Vm;
        if (ReferenceEquals(vm, _wired)) return;
        Unwire();
        if (vm is null) return;
        _wired = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
        Refresh();
    }

    private void Unwire()
    {
        if (_wired is not { } vm) return;
        _wired = null;
        vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is MainViewModel.RxReadouts or nameof(MainViewModel.ActiveRx) or nameof(MainViewModel.TxRx)
            or nameof(MainViewModel.Mode) or nameof(MainViewModel.ShowPanadapterB)
            or nameof(MainViewModel.ShowSliceBInput) or nameof(MainViewModel.SliceBInputLabel))
            Refresh();
    }

    private void Refresh()
    {
        if (_wired is not { } vm) return;
        int rx = Math.Clamp(Rx, 0, 1);
        bool active = vm.ActiveRx == rx, tx = vm.TxRx == rx;
        NameText.Text = rx == 0 ? "RX1" : "RX2";
        NameText.Foreground = SliceBrush[rx];
        Box.BorderBrush = active ? SliceBrush[rx] : Idle;
        long hz = vm.RxHz(rx);
        Freq.Text = hz > 0 ? hz.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', '.') : "";
        ModeText.Text = vm.RxModeLabel(rx);
        // On the Rx2 jack RX2 only receives, so it has no TX mark.
        TxChip.IsVisible = !(rx == 1 && vm.Rx2ReceiveOnly);
        TxChip.Background = tx ? TxRed : Brushes.Transparent;
        TxChip.BorderBrush = tx ? TxRed : Dim;
        TxText.Foreground = tx ? Brushes.White : Dim;
        CloseChip.IsVisible = rx == 1;
        AntChip.IsVisible = rx == 1 && vm.ShowSliceBInput;
        AntText.Text = vm.SliceBInputLabel;

        Freq.FontSize = Touch ? 24 : 18;
        NameText.FontSize = ModeText.FontSize = Touch ? 14 : 11;
        TxText.FontSize = Touch ? 14 : 10;
        TxChip.Padding = Touch ? new Thickness(8, 4) : new Thickness(4, 0);
        CloseChip.Padding = Touch ? new Thickness(10, 4) : new Thickness(3, 0);
        Box.Padding = AntChip.Padding = Touch ? new Thickness(10, 6) : new Thickness(8, 3);
        AntText.FontSize = Touch ? 15 : 12;
    }
}
