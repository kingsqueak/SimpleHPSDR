using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using SimpleHPSDR.Controls;
using SimpleHPSDR.Engine;
using SimpleHPSDR.ViewModels;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Views;

/// <summary>
/// The main window's desktop layout. The window itself keeps what belongs to
/// the window (menus, dialogs, the mini and CW windows, starting the engine);
/// everything drawn below the menu bar and its mouse handling lives here.
/// </summary>
public partial class DesktopMainView : UserControl
{
    public DesktopMainView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => FitTopBar(e.NewSize.Width);
        // RIT and XIT stay full size beside the VFO, which takes what's left.
        VfoArea.SizeChanged += (_, _) => FitVfo();
        OffsetButtons.SizeChanged += (_, _) => FitVfo();
        StepLockButtons.SizeChanged += (_, _) => FitVfo();
        SliceBadges.SizeChanged += (_, _) => FitVfo();
        SliceBadges.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            VfoGrid.InvalidateMeasure();   // the badges' column must make room for them
            FitVfo();
        };
        MainGrid.LayoutUpdated += (_, _) => PlaceDeck();
        VfoDigitWheel.Attach(FrequencyReadout, () => Vm);
        if (StepButton.Flyout is MenuFlyout stepMenu) stepMenu.Opening += (_, _) => FillStepMenu(stepMenu);
        FrequencyPanel.PointerWheelChanged += (_, e) =>
        {
            Vm?.TuneBySteps(SliderWheel.Clicks(FrequencyPanel, e));
            e.Handled = true;
        };
        // The wheel over Step and lock tunes too, as when they sat in the frequency box.
        StepLockButtons.PointerWheelChanged += (_, e) =>
        {
            Vm?.TuneBySteps(SliderWheel.Clicks(FrequencyPanel, e));
            e.Handled = true;
        };
        // Click the frequency to type one; Enter tunes, Esc or clicking away cancels.
        // A press on a slice badge moves transmit there and stops before it
        // reaches the frequency box (which would open typing).
        SliceBadgeA.PointerPressed += (_, e) => { e.Handled = true; _ = Vm?.TransmitOnAsync(0); };
        SliceBadgeB.PointerPressed += (_, e) => { e.Handled = true; _ = Vm?.TransmitOnAsync(1); };
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
        RitButton.PointerWheelChanged += (_, e) => { Vm?.NudgeRit(SliderWheel.Clicks(RitButton, e)); e.Handled = true; };
        XitButton.PointerWheelChanged += (_, e) => { Vm?.NudgeXit(SliderWheel.Clicks(XitButton, e)); e.Handled = true; };
        RitButton.ContextRequested += (_, e) => { Vm?.ClearRit(); e.Handled = true; };
        XitButton.ContextRequested += (_, e) => { Vm?.ClearXit(); e.Handled = true; };
        WpmButton.PointerWheelChanged += (_, e) => { Vm?.NudgeCwWpm(SliderWheel.Clicks(WpmButton, e)); e.Handled = true; };
        BandButton.ContextRequested += async (_, e) => { e.Handled = true; await MainMenus.ShowBandStackAsync(Vm, BandButton); };
        Spectrum.TuneRequested += TuneA;
        Spectrum.PanRequested += hz => Vm?.PanTo(0, hz);
        SpectrumB.PanRequested += hz => Vm?.PanTo(1, hz);
        Spectrum.PanEnded += () => Vm?.PanEnded();
        SpectrumB.PanEnded += () => Vm?.PanEnded();
        FilterScope.FilterDragged += (low, high) => Vm?.DragFilter(low, high);
        _ = new CloseUpFeed(FilterScope, () => Vm, () => FilterScope.IsEffectivelyVisible
            && TopLevel.GetTopLevel(FilterScope) is not Window { WindowState: WindowState.Minimized });
        Spectrum.SliceClicked += rx => _ = Vm?.MakeActiveAsync(rx);
        Spectrum.MenuRequested += hz => MainMenus.ShowSliceMenu(Vm, hz, Spectrum);
        Spectrum.WheelTune += steps =>
        {
            if (Vm is not { } vm) return;
            if (vm.ShowPanadapterB) _ = vm.TuneSliceByStepsAsync(0, steps);
            else vm.TuneBySteps(steps);   // one panadapter: tune the active slice
        };
        // Slice B's own panadapter tunes slice B, making it active first.
        SpectrumB.TuneRequested += hz =>
        {
            if (Vm is { } vm && !vm.TryPlaceNotch(hz)) _ = vm.TuneSliceAsync(1, vm.ClickFrequency(hz));
        };
        SpectrumB.SliceClicked += rx => _ = Vm?.MakeActiveAsync(rx);
        SpectrumB.MenuRequested += hz => MainMenus.ShowSliceMenu(Vm, hz, SpectrumB);
        SpectrumB.WheelTune += steps => _ = Vm?.TuneSliceByStepsAsync(1, steps);
        // Pinch (trackpad or fingers) zooms both panadapters, as the Zoom button does.
        Spectrum.ZoomRequested += dir => Vm?.ZoomStep(dir);
        SpectrumB.ZoomRequested += dir => Vm?.ZoomStep(dir);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // Mem's popup closed: put the memories in frequency order.
    private void OnMemoriesClosed(object? sender, EventArgs e) => Vm?.SortMemories();

    // A narrow window gives its width to the frequency: the radio name and
    // status shorten and the signal meter narrows, and the buttons stay as they are.
    private const double FullTopBarWidth = 1180;

    private void FitVfo()
    {
        // The buttons beside the frequency keep their size (their gaps are
        // margins, so the A/B badges take no room while hidden); the frequency gets the rest.
        // In the side panel Step, lock, RIT and XIT sit under the frequency, not beside it.
        double others = (_side ? 0 : StepLockButtons.DesiredSize.Width + OffsetButtons.DesiredSize.Width)
                        + (SliceBadges.IsVisible ? SliceBadges.DesiredSize.Width : 0);
        VfoScale.MaxWidth = Math.Max(0, VfoArea.Bounds.Width - others);
    }

    private void FitTopBar(double width)
    {
        bool narrow = width < FullTopBarWidth;
        RadioText.MaxWidth = narrow ? 96 : double.PositiveInfinity;
        StatusText.MaxWidth = narrow ? 230 : 300;
        // The side panel sets the meter's width; below, a narrow window narrows it.
        SignalPanel.Width = _side ? double.NaN : Math.Clamp(width - 800, 236, 300);
        SignalLabel.IsVisible = _side || SignalPanel.Width >= 290;
    }

    // The tuning steps, and whether a panadapter click snaps to the step.
    private void FillStepMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        if (Vm is not { } vm) return;
        foreach (var c in vm.TuneSteps)
            menu.Items.Add(new MenuItem
            {
                Header = c.Label, Command = c.Select,
                ToggleType = MenuItemToggleType.Radio, IsChecked = c.IsSelected,
            });
        menu.Items.Add(new Separator());
        var snap = new MenuItem
        {
            Header = "Snap clicks to step",
            ToggleType = MenuItemToggleType.CheckBox, IsChecked = vm.SnapClickToStep,
        };
        ToolTip.SetTip(snap, "A click on the panadapter tunes to the nearest whole step");
        snap.Click += (_, _) => vm.ToggleSnapClickToStep();
        menu.Items.Add(snap);
    }

    // With two panadapters the top one is slice A's; with one, a click tunes
    // whichever slice is active (click a tag to switch).
    private void TuneA(long hz)
    {
        if (Vm is not { } vm || vm.TryPlaceNotch(hz)) return;
        hz = vm.ClickFrequency(hz);
        if (vm.ShowPanadapterB) _ = vm.TuneSliceAsync(0, hz);
        else vm.TuneTo(hz);
    }

    private static readonly IBrush[] ActiveOutline =
        [new SolidColorBrush(Color.Parse("#80F25F5C")), new SolidColorBrush(Color.Parse("#80F5A524"))];

    // Two panadapters while slice B is on, the active one outlined in its
    // slice colour; one, unoutlined, otherwise.
    private void LayoutPanadapters()
    {
        if (Vm is not { } vm) return;
        bool two = vm.ShowPanadapterB;
        bool beside = two && vm.PanadaptersSideBySide;
        var star = new GridLength(1, GridUnitType.Star);
        PanGrid.RowDefinitions[1].Height = two && !beside ? star : new GridLength(0);
        PanGrid.ColumnDefinitions[1].Width = beside ? star : new GridLength(0);
        PanGrid.RowSpacing = two && !beside ? 6 : 0;
        PanGrid.ColumnSpacing = beside ? 6 : 0;
        Grid.SetRow(PanBorderB, beside ? 0 : 1);
        Grid.SetColumn(PanBorderB, beside ? 1 : 0);
        PanBorderA.BorderBrush = two && vm.ActiveRx == 0 ? ActiveOutline[0] : Brushes.Transparent;
        PanBorderB.BorderBrush = two && vm.ActiveRx == 1 ? ActiveOutline[1] : Brushes.Transparent;
        if (!two) SpectrumB.Clear();
    }

    private void OnOpenMini(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.OpenMini();

    // ---- the two arrangements ------------------------------------------

    private bool _side;
    private bool _arranged;

    // The view button: controls below, controls at the side, the small
    // window, and round again (closing the small window goes on to "below").
    private void OnNextView(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (!vm.SidePanel) vm.SidePanel = true;
        else (TopLevel.GetTopLevel(this) as MainWindow)?.OpenMini(thenControlsBelow: true);
    }

    private void OnViewBelow(object? sender, RoutedEventArgs e) { if (Vm is { } vm) vm.SidePanel = false; }

    private void OnViewSide(object? sender, RoutedEventArgs e) { if (Vm is { } vm) vm.SidePanel = true; }

    // The view button's right-click menu: a dot on the arrangement in use.
    private void OnViewMenuOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout { Items: [MenuItem below, MenuItem atSide, ..] }) return;
        below.IsChecked = !_side;
        atSide.IsChecked = _side;
    }

    private static void Move(Control piece, Panel to, int index = -1)
    {
        if (ReferenceEquals(piece.Parent, to)) return;
        (piece.Parent as Panel)?.Children.Remove(piece);
        if (index < 0 || index > to.Children.Count) to.Children.Add(piece);
        else to.Children.Insert(index, piece);
    }

    /// <summary>Puts the pieces where the chosen arrangement wants them. The
    /// controls are the same objects either way, so nothing they show or hold
    /// is lost by switching.</summary>
    private void Arrange(bool side)
    {
        if (_arranged && _side == side) return;
        _arranged = true;
        _side = side;
        MainGrid.Classes.Set("side", side);
        if (side)
        {
            Move(VfoArea, SideTuneHost, 0);
            Move(TuneButtons, SideTuneRow);
            Move(StepLockButtons, SideTuneRow);
            Move(OffsetButtons, SideTuneRow);
            Move(SignalPanel, SideMeterHost);
            Move(ReceivePanel, SideReceiveHost);
            Move(AudioBlock, SideAudioHost);
            Move(TxBlock, SideTxHost);
            Move(VkBlock, SideTxHost);
            Move(ClockBlock, SideClockHost);
        }
        else
        {
            Move(TuneButtons, TopHost);
            Move(VfoArea, TopHost);
            Move(SignalPanel, TopHost);
            Move(StepLockButtons, VfoGrid);
            Move(OffsetButtons, VfoGrid);
            Move(ReceivePanel, MainGrid);
            Move(ClockBlock, BottomHost, 0);     // docked right: before the wrap panel that fills
            Move(AudioBlock, BottomWrap, 0);
            Move(TxBlock, BottomWrap, 1);
            Move(VkBlock, BottomWrap, 2);
        }
        TopHost.IsVisible = BottomPanel.IsVisible = !side;
        SideRail.IsVisible = side;

        // Step, lock, RIT and XIT: stacked beside the frequency, or in a row under it.
        var flow = side ? Avalonia.Layout.Orientation.Horizontal : Avalonia.Layout.Orientation.Vertical;
        StepLockButtons.Orientation = OffsetButtons.Orientation = flow;
        StepLockButtons.Margin = OffsetButtons.Margin = side ? default : new Thickness(10, 0, 0, 0);
        VfoArea.Margin = side ? default : new Thickness(8, 0);

        // Receive panel: buttons beside the close-up, or the close-up on top
        // with the buttons and the volume under it.
        var star = new GridLength(1, GridUnitType.Star);
        ReceiveGrid.ColumnDefinitions[0].Width = side ? star : GridLength.Auto;
        ReceiveGrid.ColumnDefinitions[1].Width = side ? new GridLength(0) : star;
        Grid.SetColumn(FilterScope, side ? 0 : 1);
        Grid.SetRow(ReceiveButtons, side ? 1 : 0);
        FilterScope.Height = side ? 64 : double.NaN;
        FilterScope.Margin = side ? new Thickness(-10, -8, -10, 8) : new Thickness(12, -8, -10, -8);
        FilterLabel.IsVisible = ReceiveLabel.IsVisible = !side;
        ReceiveButtons.ColumnSpacing = side ? 0 : 12;
        SideAudioHost.Margin = side ? new Thickness(0, 8, 0, 0) : default;
        ReceivePanel.Margin = side ? default : new Thickness(0, 0, 0, 10);
        ClockBlock.Margin = side ? new Thickness(0, 0, 8, 0) : new Thickness(12, 0, 4, 0);

        FitTopBar(Bounds.Width);
        FitVfo();
    }

    private void OnOpenCw(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.OpenCw();

    // The filter editor loads the slot's stored values each time it opens.
    private void OnFilterEditOpening(object? sender, EventArgs e)
    {
        if (sender is Flyout { Content: Control { DataContext: FilterSlot slot } }) slot.BeginEdit();
    }

    private void OnModeOptionsOpening(object? sender, EventArgs e) => _ = Vm?.LoadModeOptionsAsync();

    private void OnSMeterTrimOpening(object? sender, EventArgs e) => _ = Vm?.LoadSMeterTrimAsync();

    private void OnRxLevelerOpening(object? sender, EventArgs e) => _ = Vm?.LoadRxLevelerAsync();

    // The NB and NR popups show what the engine has when they open.
    private void OnNbParamsOpening(object? sender, EventArgs e)
    {
        if (Vm is { } vm) _ = vm.LoadNoisePanelAsync(vm.NbParams);
    }

    private void OnNrParamsOpening(object? sender, EventArgs e)
    {
        if (Vm is { } vm) _ = vm.LoadNoisePanelAsync(vm.NrParams);
    }

    // ---- display frames: only while this layout is on screen ----------

    private MainViewModel? _wired;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // An empty MenuFlyout never opens, so fill it now; Opening refreshes the ticks.
        if (StepButton.Flyout is MenuFlyout stepMenu) FillStepMenu(stepMenu);
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
        Arrange(vm.SidePanel);
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
        Spectrum.HoldView = SpectrumB.HoldView = vm.CtunOn;
        Spectrum.TxRangeDb = SpectrumB.TxRangeDb = vm.TxWaterfallRangeDb;
        Spectrum.Transmitting = SpectrumB.Transmitting = vm.IsTransmitting;
        if (f.RxId == vm.ActiveRx)
        {
            FilterScope.AverageSeconds = vm.CloseUpAverageSec;
            FilterScope.ColouredFill = vm.CloseUpColoured;
            FilterScope.PushFrame(f, vm.VfoHz, vm.FilterLowHz, vm.FilterHighHz, vm.FilterIsSymmetric, vm.CwPitchHz);
        }
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

    // The recorder deck sits at the bottom of the waterfall, its left edge at
    // REC's (kept inside the window when REC is near the right side).
    private void PlaceDeck()
    {
        if (!DeckHost.IsVisible) return;
        var at = RecButton.TranslatePoint(new Point(0, 0), MainGrid);
        if (at is null) return;
        // Inside the panadapter's width: in the side panel REC is to the right of it.
        double x = Math.Max(0, Math.Min(at.Value.X, PanGrid.Bounds.Width - 12 - DeckHost.Bounds.Width));
        var margin = new Thickness(x, 0, 0, 8);
        if (DeckHost.Margin != margin) DeckHost.Margin = margin;
    }

    private void OnDisplayReset() { Spectrum.Clear(); SpectrumB.Clear(); FilterScope.Clear(); }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ShowPanadapterB) or nameof(MainViewModel.ActiveRx)
            or nameof(MainViewModel.PanadaptersSideBySide))
            LayoutPanadapters();
        else if (e.PropertyName == nameof(MainViewModel.SidePanel) && _wired is { } v)
            Arrange(v.SidePanel);
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
