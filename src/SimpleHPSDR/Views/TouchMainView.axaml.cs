using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using SimpleHPSDR.Controls;
using SimpleHPSDR.ViewModels;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Views;

/// <summary>
/// The large-target layout for small screens. Mouse first: every action is a
/// click, a drag or the wheel. The frequency tunes by dragging sideways (one
/// step per <see cref="DragPixelsPerStep"/> px) or the wheel, and a click opens
/// a keypad. Slice B gets its own panadapter below while it has one.
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
            Vm?.TuneBySteps(SliderWheel.Clicks(VfoBox, e));
            e.Handled = true;
        };

        BandButton.ContextRequested += async (_, e) => { e.Handled = true; await MainMenus.ShowBandStackAsync(Vm, BandButton); };

        // With two panadapters the top one is slice A's; with one, a click
        // tunes whichever slice is active (click a tag to switch).
        Spectrum.TuneRequested += hz =>
        {
            if (Vm is not { } vm || vm.TryPlaceNotch(hz)) return;
            hz = vm.ClickFrequency(hz);
            if (vm.ShowPanadapterB) _ = vm.TuneSliceAsync(0, hz);
            else vm.TuneTo(hz);
        };
        SpectrumB.TuneRequested += hz =>
        {
            if (Vm is { } vm && !vm.TryPlaceNotch(hz)) _ = vm.TuneSliceAsync(1, vm.ClickFrequency(hz));
        };
        SpectrumB.PanRequested += hz => Vm?.PanTo(1, hz);
        SpectrumB.PanEnded += () => Vm?.PanEnded();
        SpectrumB.SliceClicked += rx => _ = Vm?.MakeActiveAsync(rx);
        SpectrumB.MenuRequested += hz => MainMenus.ShowSliceMenu(Vm, hz, SpectrumB);
        SpectrumB.WheelTune += steps => _ = Vm?.TuneSliceByStepsAsync(1, steps);
        SpectrumB.ZoomRequested += Zoom;
        FilterScope.FilterDragged += (low, high) => Vm?.DragFilter(low, high);
        Spectrum.PanRequested += hz => Vm?.PanTo(0, hz);
        Spectrum.PanEnded += () => Vm?.PanEnded();
        Spectrum.SliceClicked += rx => _ = Vm?.MakeActiveAsync(rx);
        Spectrum.MenuRequested += hz => MainMenus.ShowSliceMenu(Vm, hz, Spectrum);
        Spectrum.WheelTune += steps =>
        {
            if (Vm is not { } vm) return;
            if (vm.ShowPanadapterB) _ = vm.TuneSliceByStepsAsync(0, steps);
            else vm.TuneBySteps(steps);
        };
        Spectrum.ZoomRequested += Zoom;
        // Ctrl+wheel (⌘+wheel on a Mac) zooms: SpectrumView raises ZoomRequested.
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // Mem's popup closed: put the memories in frequency order.
    private void OnMemoriesClosed(object? sender, EventArgs e) => Vm?.SortMemories();

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

    // The Help tab opens the Help window and goes back to the tab before it.
    private int _lastDrawerTab;

    private void OnDrawerTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DrawerTabs is null) return;
        if (!ReferenceEquals(DrawerTabs.SelectedItem, HelpTab))
        {
            _lastDrawerTab = DrawerTabs.SelectedIndex;
            return;
        }
        DrawerTabs.SelectedIndex = _lastDrawerTab;
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

    private void OnModeOptionsOpening(object? sender, EventArgs e) => _ = Vm?.LoadModeOptionsAsync();

    private void OnRxLevelerOpening(object? sender, EventArgs e) => _ = Vm?.LoadRxLevelerAsync();

    private void OnSMeterTrimOpening(object? sender, EventArgs e) => _ = Vm?.LoadSMeterTrimAsync();

    private void OnMeterClicked(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control c && e.InitialPressMouseButton == MouseButton.Left) FlyoutBase.ShowAttachedFlyout(c);
    }

    // The chosen filter's label and edges, in their own popup.
    private void EditFilter(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Filters.FirstOrDefault(f => f.IsSelected) is not { } slot)
        {
            if (Vm is { } vm) vm.Status = "Choose a filter first: a dragged filter has no slot to edit.";
            return;
        }
        MainMenus.CloseFlyout(sender);
        slot.BeginEdit();
        var editor = new Flyout
        {
            Placement = PlacementMode.TopEdgeAlignedLeft,
            Content = new ContentControl
            {
                Content = slot,
                ContentTemplate = this.FindResource("FilterEditPanel") as Avalonia.Controls.Templates.IDataTemplate,
            },
        };
        Avalonia.Threading.Dispatcher.UIThread.Post(() => editor.ShowAt(FilterButton));
    }

    // Quit takes a second tap, so a stray touch can't close the radio.
    private DateTime _quitArmed;

    private void OnQuit(object? sender, RoutedEventArgs e)
    {
        if (DateTime.UtcNow - _quitArmed < TimeSpan.FromSeconds(4))
        {
            (TopLevel.GetTopLevel(this) as Window)?.Close();
            return;
        }
        _quitArmed = DateTime.UtcNow;
        QuitButton.Content = "Tap again to quit";
        QuitButton.Classes.Add("rec");
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            QuitButton.Content = "Quit";
            QuitButton.Classes.Remove("rec");
        }, TimeSpan.FromSeconds(4));
    }

    private void OnOpenAnalyzer(object? sender, RoutedEventArgs e)
    {
        Drawer.IsPaneOpen = false;
        (TopLevel.GetTopLevel(this) as MainWindow)?.OpenAnalyzer();
    }

    private void OnOpenWideband(object? sender, RoutedEventArgs e)
    {
        Drawer.IsPaneOpen = false;
        (TopLevel.GetTopLevel(this) as MainWindow)?.OpenWideband();
    }

    private void OnFullScreen(object? sender, RoutedEventArgs e)
    {
        Drawer.IsPaneOpen = false;
        (TopLevel.GetTopLevel(this) as MainWindow)?.ToggleFullScreen();
    }

    private void ToggleSnap(object? sender, RoutedEventArgs e) => Vm?.ToggleSnapClickToStep();

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

    private void Zoom(int dir) => Vm?.ZoomStep(dir);

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
            if (Band.Tunable(hz)) vm.TuneTo(hz);
            else vm.Status = Band.RangeHint;
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
        vm.PropertyChanged += OnVmPropertyChanged;
        LayoutPanadapters();
    }

    private void Unwire()
    {
        if (_wired is not { } vm) return;
        _wired = null;
        vm.Display -= OnDisplay;
        vm.DisplayReset -= OnDisplayReset;
        vm.PropertyChanged -= OnVmPropertyChanged;
        if (vm.FindSignalHz == FindSignalHz) vm.FindSignalHz = null;
    }

    private double? FindSignalHz(int rx, long lo, long hi, double db) =>
        (rx == 1 && _wired is { ShowPanadapterB: true } ? SpectrumB : Spectrum).PeakHz(lo, hi, db);

    private void OnDisplay(DisplayFrame f)
    {
        if (_wired is not { } vm) return;
        Spectrum.AgcLineDb = SpectrumB.AgcLineDb = vm.AgcLineDb();
        Spectrum.Notches = SpectrumB.Notches = vm.NotchMarks();
        Spectrum.Strip = SpectrumB.Strip = vm.BandStrip;
        Spectrum.StripOpacity = SpectrumB.StripOpacity = vm.BandStripOpacity;
        Spectrum.TargetSpanHz = SpectrumB.TargetSpanHz = vm.ZoomTargetSpanHz;
        Spectrum.TxRangeDb = SpectrumB.TxRangeDb = vm.TxWaterfallRangeDb;
        Spectrum.Transmitting = SpectrumB.Transmitting = vm.IsTransmitting;
        if (f.RxId == vm.ActiveRx && Drawer.IsPaneOpen)
            FilterScope.AverageSeconds = vm.CloseUpAverageSec;
            FilterScope.ColouredFill = vm.CloseUpColoured;
            FilterScope.PushFrame(f, vm.VfoHz, vm.FilterLowHz, vm.FilterHighHz, vm.FilterIsSymmetric, vm.CwPitchHz);
        if (f.RxId == 1)
        {
            if (!vm.ShowPanadapterB) return;
            SpectrumB.Slices = vm.SliceMarks();
            SpectrumB.PushFrame(f);
            (SpectrumB.SpecMinDb, SpectrumB.SpecMaxDb) = vm.SliceBRange(SpectrumB.NoiseFloorDb);
            return;
        }
        Spectrum.Slices = vm.SliceMarks();
        Spectrum.PushFrame(f);
        vm.FollowNoiseFloor(Spectrum.NoiseFloorDb);
    }

    private void OnDisplayReset() { Spectrum.Clear(); SpectrumB.Clear(); FilterScope.Clear(); }

    private static readonly IBrush[] ActiveOutline =
        [new SolidColorBrush(Color.Parse("#80F25F5C")), new SolidColorBrush(Color.Parse("#80F5A524"))];

    // Two panadapters while slice B has its own, the active one outlined in
    // its slice colour; one, unoutlined, otherwise.
    private void LayoutPanadapters()
    {
        if (Vm is not { } vm) return;
        bool two = vm.ShowPanadapterB;
        PanGrid.RowDefinitions[1].Height = two ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PanGrid.RowSpacing = two ? 6 : 0;
        PanBorderA.BorderBrush = two && vm.ActiveRx == 0 ? ActiveOutline[0] : Brushes.Transparent;
        PanBorderB.BorderBrush = two && vm.ActiveRx == 1 ? ActiveOutline[1] : Brushes.Transparent;
        if (!two) SpectrumB.Clear();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ShowPanadapterB) or nameof(MainViewModel.ActiveRx))
            LayoutPanadapters();
        // Zooming: redraw now with the new span instead of waiting for the next
        // frame. At each whole zoom level the engine sends no frames for about
        // 130 ms while it rebuilds its analyser, and the picture froze then leapt.
        else if (e.PropertyName == nameof(MainViewModel.ZoomSlider) && _wired is { } vm)
        {
            Spectrum.TargetSpanHz = SpectrumB.TargetSpanHz = vm.ZoomTargetSpanHz;
            Spectrum.InvalidateVisual();
            SpectrumB.InvalidateVisual();
        }
    }
}
