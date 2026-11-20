using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Controls;

/// <summary>
/// A slice's small VFO on its panadapter: the slice's name, a TX mark (click
/// to transmit there), its frequency (wheel to tune, click to type one), its
/// mode, and a readout of its filter, noise blanker/reduction and AGC ("out
/// of span" first while a shared-stream slice is outside its stream). A
/// slice with an input of its own also has the antenna switch on its left,
/// and a slice that may come off now has a ✕ that turns it off. A click anywhere
/// else on the chip gives that slice the controls; tuning the frequency
/// does too.
/// </summary>
public partial class MiniVfo : UserControl
{
    public static readonly StyledProperty<int> SliceProperty = AvaloniaProperty.Register<MiniVfo, int>(nameof(Slice));
    /// <summary>0..3: A1 and A2 on RX1's panadapter, B1 and B2 on RX2's.</summary>
    public int Slice { get => GetValue(SliceProperty); set => SetValue(SliceProperty, value); }

    public static readonly StyledProperty<bool> TouchProperty = AvaloniaProperty.Register<MiniVfo, bool>(nameof(Touch));
    /// <summary>Touch layout: larger, and a tap on the frequency asks the
    /// layout for its keypad (EntryRequested) instead of a text box.</summary>
    public bool Touch { get => GetValue(TouchProperty); set => SetValue(TouchProperty, value); }

    // By colour index (SliceColorIndex), whatever the slice layout is.
    private static readonly IBrush[] SliceBrush =
        [.. Enumerable.Range(0, MainViewModel.MaxSlices).Select(c => new SolidColorBrush(Color.Parse(MainViewModel.SliceColorAt(c))))];
    private static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#40FFFFFF"));
    private static readonly IBrush TxRed = new SolidColorBrush(Color.Parse("#A8232A"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#8A93A3"));

    /// <summary>Touch: the frequency was tapped; the layout shows its keypad here.</summary>
    public event Action<MiniVfo>? EntryRequested;

    private MainViewModel? _wired;

    public MiniVfo()
    {
        InitializeComponent();
        Box.PointerWheelChanged += (_, e) =>
        {
            e.Handled = true;
            int clicks = SliderWheel.Clicks(Box, e);
            if (clicks != 0) _ = Vm?.TuneSliceByStepsAsync(Slice, clicks);
        };
        // Presses stop here, so they don't tune the panadapter underneath.
        // A press on the chip itself (not one of its own controls) gives
        // this slice the controls, like a click on its panadapter.
        Box.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (Vm is { IsConnected: true } vm && vm.ActiveRx != Slice) _ = vm.MakeActiveAsync(Slice);
        };
        FreqHost.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (Vm is not { IsConnected: true } vm || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (Touch) { EntryRequested?.Invoke(this); return; }
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
                _ = Vm?.EnterRxFrequencyAsync(Slice, text);
            }
            else if (e.Key == Key.Escape) { e.Handled = true; EndEntry(); }
        };
        Entry.LostFocus += (_, _) => EndEntry();
        TxChip.PointerPressed += (_, e) => { e.Handled = true; _ = Vm?.TransmitOnAsync(Slice); };
        CloseChip.PointerPressed += (_, e) => { e.Handled = true; _ = Vm?.RemoveSliceAsync(Slice); };
        AntChip.PointerPressed += (_, e) => { e.Handled = true; Vm?.ToggleSliceInput(Slice); };
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
        if (change.Property == SliceProperty || change.Property == TouchProperty) Refresh();
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
            or nameof(MainViewModel.Mode) or nameof(MainViewModel.SliceCount))
            Refresh();
    }

    private void Refresh()
    {
        if (_wired is not { } vm) return;
        int rx = Math.Clamp(Slice, 0, MainViewModel.MaxSlices - 1);
        bool active = vm.ActiveRx == rx, tx = vm.TxRx == rx;
        NameText.Text = MainViewModel.SliceLabel(rx);
        NameText.Foreground = SliceBrush[MainViewModel.SliceColorIndex(rx)];
        Box.BorderBrush = active ? SliceBrush[MainViewModel.SliceColorIndex(rx)] : Idle;
        long hz = vm.RxHz(rx);
        Freq.Text = hz > 0 ? hz.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', '.') : "";
        ModeText.Text = vm.RxModeLabel(rx);
        SettingsText.Text = vm.RxSettingsLabel(rx);
        SettingsText.IsVisible = SettingsText.Text.Length > 0;
        TxChip.Background = tx ? TxRed : Brushes.Transparent;
        TxChip.BorderBrush = tx ? TxRed : Dim;
        TxText.Foreground = tx ? Brushes.White : Dim;
        // Only a slice that may come off now offers a ✕ (in the four-slice
        // layout slices go off in reverse order, so the top one).
        CloseChip.IsVisible = vm.MayRemoveSlice(rx);
        AntChip.IsVisible = rx > 0 && vm.ShowSliceInput(rx);
        AntText.Text = vm.SliceInputLabel(rx);

        Freq.FontSize = Touch ? 24 : 18;
        NameText.FontSize = ModeText.FontSize = SettingsText.FontSize = Touch ? 14 : 11;
        TxText.FontSize = Touch ? 14 : 10;
        TxChip.Padding = Touch ? new Thickness(8, 4) : new Thickness(4, 0);
        CloseChip.Padding = Touch ? new Thickness(10, 4) : new Thickness(3, 0);
        Box.Padding = AntChip.Padding = Touch ? new Thickness(10, 6) : new Thickness(8, 3);
        AntText.FontSize = Touch ? 15 : 12;
    }
}
