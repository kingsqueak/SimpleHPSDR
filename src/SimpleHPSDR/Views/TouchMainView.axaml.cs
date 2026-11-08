using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using SimpleHPSDR.Controls;
using SimpleHPSDR.ViewModels;
using Zeus.Contracts;

namespace SimpleHPSDR.Views;

/// <summary>
/// The large-target layout for small screens. Mouse first: every action is a
/// click, a drag or the wheel. The frequency tunes by dragging sideways (one
/// step per <see cref="DragPixelsPerStep"/> px) or the wheel, and a click opens
/// a keypad. One panadapter shows the receiver of slice A with both slices'
/// markers.
/// </summary>
public partial class TouchMainView : UserControl
{
    private const double DragPixelsPerStep = 20;
    private const double ClickSlop = 6;

    public TouchMainView()
    {
        InitializeComponent();

        VfoBox.PointerPressed += OnVfoPressed;
        VfoBox.PointerMoved += OnVfoMoved;
        VfoBox.PointerReleased += OnVfoReleased;
        VfoBox.PointerCaptureLost += (_, _) => _vfoPress = null;
        VfoBox.PointerWheelChanged += (_, e) =>
        {
            Vm?.TuneBySteps(Math.Sign(SliderWheel.Amount(e)));
            e.Handled = true;
        };

        Spectrum.TuneRequested += hz =>
        {
            if (Vm is not { } vm || vm.TryPlaceNotch(hz)) return;
            vm.TuneTo(vm.ClickFrequency(hz));
        };
        Spectrum.PanRequested += hz => Vm?.PanTo(0, hz);
        Spectrum.PanEnded += () => Vm?.PanEnded();
        Spectrum.SliceClicked += rx => _ = Vm?.MakeActiveAsync(rx);
        Spectrum.MenuRequested += hz => MainMenus.ShowSliceMenu(Vm, hz, Spectrum);
        Spectrum.WheelTune += steps => Vm?.TuneBySteps(steps);
        // Ctrl+wheel zooms; the spectrum itself only tunes with the wheel.
        Spectrum.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
            Zoom(Math.Sign(SliderWheel.Amount(e)));
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // ---- drawer --------------------------------------------------------

    private void OpenDrawer(object? sender, RoutedEventArgs e) => Drawer.IsPaneOpen = true;

    private void CloseDrawer(object? sender, RoutedEventArgs e) => Drawer.IsPaneOpen = false;

    private void OnOpenMini(object? sender, RoutedEventArgs e)
    {
        Drawer.IsPaneOpen = false;
        (TopLevel.GetTopLevel(this) as MainWindow)?.OpenMini();
    }

    private void OnOpenCw(object? sender, RoutedEventArgs e)
    {
        Drawer.IsPaneOpen = false;
        (TopLevel.GetTopLevel(this) as MainWindow)?.OpenCw();
    }

    private void OnOpenHelp(object? sender, RoutedEventArgs e)
    {
        Drawer.IsPaneOpen = false;
        if (TopLevel.GetTopLevel(this) is Window w) _ = new HelpWindow().ShowDialog(w);
    }

    // A choice in a picker grid closes the picker (after its command runs).
    private void ClosePicker(object? sender, RoutedEventArgs e) => MainMenus.CloseFlyout(sender);

    private void OnNbParamsOpening(object? sender, EventArgs e)
    {
        if (Vm is { } vm) _ = vm.LoadNoisePanelAsync(vm.NbParams);
    }

    private void OnNrParamsOpening(object? sender, EventArgs e)
    {
        if (Vm is { } vm) _ = vm.LoadNoisePanelAsync(vm.NrParams);
    }

    // ---- RIT / XIT -----------------------------------------------------

    private void RitDown(object? sender, RoutedEventArgs e) => Vm?.NudgeRit(-1);
    private void RitUp(object? sender, RoutedEventArgs e) => Vm?.NudgeRit(1);
    private void RitClear(object? sender, RoutedEventArgs e) => Vm?.ClearRit();
    private void XitDown(object? sender, RoutedEventArgs e) => Vm?.NudgeXit(-1);
    private void XitUp(object? sender, RoutedEventArgs e) => Vm?.NudgeXit(1);
    private void XitClear(object? sender, RoutedEventArgs e) => Vm?.ClearXit();

    // ---- zoom ----------------------------------------------------------

    private void ZoomIn(object? sender, RoutedEventArgs e) => Zoom(1);
    private void ZoomOut(object? sender, RoutedEventArgs e) => Zoom(-1);

    // One step doubles or halves the zoom; out from 2x goes to full span.
    private void Zoom(int dir)
    {
        if (Vm is not { } vm || dir == 0) return;
        if (dir > 0 && !vm.ZoomOn) { vm.ToggleZoomCommand.Execute(null); return; }
        if (dir < 0 && vm.ZoomOn && vm.ZoomSlider <= 1.01) { vm.ToggleZoomCommand.Execute(null); return; }
        if (vm.ZoomOn) vm.ZoomSlider += dir;
    }

    // ---- frequency: drag to tune, click for the keypad -----------------

    private Point? _vfoPress;
    private double _vfoDragged;   // pixels already turned into steps
    private bool _vfoMoved;

    private void OnVfoPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(VfoBox).Properties.IsLeftButtonPressed) return;
        _vfoPress = e.GetPosition(VfoBox);
        _vfoDragged = 0;
        _vfoMoved = false;
        e.Pointer.Capture(VfoBox);
        e.Handled = true;
    }

    private void OnVfoMoved(object? sender, PointerEventArgs e)
    {
        if (_vfoPress is not { } at) return;
        double dx = e.GetPosition(VfoBox).X - at.X;
        if (Math.Abs(dx) >= ClickSlop) _vfoMoved = true;
        if (!_vfoMoved) return;
        // Dragging right tunes up.
        int steps = (int)((dx - _vfoDragged) / DragPixelsPerStep);
        if (steps == 0) return;
        _vfoDragged += steps * DragPixelsPerStep;
        Vm?.TuneBySteps(steps);
        e.Handled = true;
    }

    private void OnVfoReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_vfoPress is null) return;
        _vfoPress = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (!_vfoMoved) OpenKeypad();
    }

    private string _keys = "";

    private void OpenKeypad()
    {
        if (Vm is not { IsConnected: true }) return;
        _keys = "";
        KeypadText.Text = "";
        FlyoutBase.ShowAttachedFlyout(VfoBox);
    }

    private void KeyDigit(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string k } || _keys.Length >= 12) return;
        if (k == "." && _keys.Contains('.')) return;
        _keys += k;
        KeypadText.Text = _keys;
    }

    private void KeyBack(object? sender, RoutedEventArgs e)
    {
        if (_keys.Length > 0) _keys = _keys[..^1];
        KeypadText.Text = _keys;
    }

    private void KeyCancel(object? sender, RoutedEventArgs e) => FlyoutBase.GetAttachedFlyout(VfoBox)?.Hide();

    private void KeyMhz(object? sender, RoutedEventArgs e) => EnterKeys(1e6);

    private void KeyKhz(object? sender, RoutedEventArgs e) => EnterKeys(1e3);

    private void EnterKeys(double unitHz)
    {
        if (Vm is not { } vm) return;
        if (double.TryParse(_keys, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
        {
            long hz = (long)Math.Round(v * unitHz);
            if (hz is >= 10_000 and <= 60_000_000) vm.TuneTo(hz);
            else vm.Status = "Enter a frequency from 0.01 to 60 MHz.";
        }
        FlyoutBase.GetAttachedFlyout(VfoBox)?.Hide();
    }

    // ---- display frames: only while this layout is on screen ----------

    private MainViewModel? _wired;

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
        vm.FindSignalHz = FindSignalHz;
        vm.Display += OnDisplay;
        vm.DisplayReset += OnDisplayReset;
    }

    private void Unwire()
    {
        if (_wired is not { } vm) return;
        _wired = null;
        vm.Display -= OnDisplay;
        vm.DisplayReset -= OnDisplayReset;
        if (vm.FindSignalHz == FindSignalHz) vm.FindSignalHz = null;
    }

    private double? FindSignalHz(int rx, long lo, long hi, double db) => Spectrum.PeakHz(lo, hi, db);

    // Slice A's receiver only; slice B shows as a marker on it.
    private void OnDisplay(DisplayFrame f)
    {
        if (_wired is not { } vm || f.RxId != 0) return;
        Spectrum.AgcLineDb = vm.AgcLineDb();
        Spectrum.Notches = vm.NotchMarks();
        Spectrum.TxRangeDb = vm.TxWaterfallRangeDb;
        Spectrum.Transmitting = vm.IsTransmitting;
        Spectrum.Slices = vm.SliceMarks();
        Spectrum.PushFrame(f);
        vm.FollowNoiseFloor(Spectrum.NoiseFloorDb);
    }

    private void OnDisplayReset() => Spectrum.Clear();
}
