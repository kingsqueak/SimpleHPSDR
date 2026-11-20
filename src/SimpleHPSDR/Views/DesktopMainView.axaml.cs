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
        SizeChanged += (_, e) => { FitTopBar(e.NewSize.Width); FitSideRail(); };
        // RIT and XIT stay full size beside the VFO, which takes what's left.
        VfoArea.SizeChanged += (_, _) => FitVfo();
        OffsetButtons.SizeChanged += (_, _) => FitVfo();
        StepLockButtons.SizeChanged += (_, _) => FitVfo();
        SliceBadges.SizeChanged += (_, _) => FitVfo();
        AbBadges.PropertyChanged += (_, e) =>
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
        // A press on a slice badge gives that slice the controls and stops before
        // it reaches the frequency box (which would open typing).
        // RX1 and RX2 give that panadapter the controls; RX2's first click turns it on.
        RxBadge1.PointerPressed += (_, e) => PressRxBadge(0, e);
        RxBadge2.PointerPressed += (_, e) => PressRxBadge(1, e);
        RxBadge2.ContextRequested += (_, e) => { e.Handled = true; MainMenus.ShowRx2Menu(Vm, RxBadge2); };
        SliceBadgeA.PointerPressed += (_, e) => PressSliceBadge(0, e);
        SliceBadgeB.PointerPressed += (_, e) => PressSliceBadge(1, e);
        SliceBadgeA.ContextRequested += (_, e) => { e.Handled = true; ShowSliceBadgeMenu(0, SliceBadgeA); };
        SliceBadgeB.ContextRequested += (_, e) => { e.Handled = true; ShowSliceBadgeMenu(1, SliceBadgeB); };
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
        // The bar between the panadapters: keep the share it was dragged to.
        PanSplitter.DragCompleted += (_, _) =>
        {
            if (Vm is not { ShowPanadapterB: true } vm) return;
            bool beside = vm.PanadaptersSideBySide;
            double a = beside ? PanBorderA.Bounds.Width : PanBorderA.Bounds.Height;
            double b = beside ? PanBorderB.Bounds.Width : PanBorderB.Bounds.Height;
            if (a + b > 0) vm.PanSplit = a / (a + b);
        };
        WireSideGrip();
        PanSplitter.DoubleTapped += (_, _) => { if (Vm is { } vm) vm.PanSplit = 0.5; };
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
            if (vm.ShowPanadapterB) _ = vm.TuneSliceByStepsAsync(vm.TuneTargetFor(0), steps);
            else vm.TuneBySteps(steps);   // one panadapter: tune the active slice
        };
        // Slice B1's/B2's own panadapter tunes the slice active on it, making
        // it active first.
        SpectrumB.TuneRequested += hz =>
        {
            if (Vm is { } vm && !vm.TryPlaceNotch(hz)) _ = vm.TuneSliceAsync(vm.TuneTargetFor(1), vm.ClickFrequency(hz));
        };
        SpectrumB.SliceClicked += rx => _ = Vm?.MakeActiveAsync(rx);
        SpectrumB.MenuRequested += hz => MainMenus.ShowSliceMenu(Vm, hz, SpectrumB, pan: 1);
        SpectrumB.WheelTune += steps => _ = Vm?.TuneSliceByStepsAsync(Vm.TuneTargetFor(1), steps);
        // Pinch (trackpad or fingers) zooms both panadapters, as the Zoom button does.
        Spectrum.ZoomRequested += dir => Vm?.ZoomStep(0, dir);
        SpectrumB.ZoomRequested += dir => Vm?.ZoomStep(1, dir);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // A press on RX1 or RX2 stops before it reaches the frequency box.
    private void PressRxBadge(int rx, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) _ = Vm?.SelectRxAsync(rx);
    }

    // The A1/A2 (or B1/B2) badges beside the frequency: the active pan's two
    // slices. A left press gives that slice the controls; the right-click menu
    // transmits on it, mutes it, or removes it when it's the top slice.
    private void PressSliceBadge(int n, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (Vm is { } vm && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            _ = vm.MakeActiveAsync(vm.BadgeSlice(n));
    }

    private void ShowSliceBadgeMenu(int n, Control at)
    {
        if (Vm is not { IsConnected: true } vm) return;
        int slice = vm.BadgeSlice(n);
        string label = MainViewModel.SliceLabel(slice);
        var items = new List<Control>
        {
            MenuItem($"Transmit on {label}", () => vm.TransmitOnAsync(slice)),
            MenuItem(vm.IsSliceMuted(slice) ? $"Unmute {label}" : $"Mute {label}",
                () => vm.SetSliceMuteAsync(slice, !vm.IsSliceMuted(slice))),
            new Separator(),
        };
        // Only a slice that may come off now can be removed (B1 only once B2 is off).
        var remove = MenuItem($"Remove {label}", () => vm.RemoveSliceAsync(slice));
        remove.IsEnabled = vm.MayRemoveSlice(slice);
        items.Add(remove);
        new ContextMenu { ItemsSource = items }.Open(at);
    }

    private static MenuItem MenuItem(string header, Func<Task> act) =>
        new() { Header = header, Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(act) };

    // The B1 antenna button toggles B1 between the main antenna and the Rx2 jack.
    private void ToggleB1Input(object? sender, RoutedEventArgs e) => Vm?.ToggleSliceInput(MainViewModel.PanPrimary(1));

    // Mem's popup closed: put the memories in frequency order.
    private void OnMemoriesClosed(object? sender, EventArgs e) => Vm?.SortMemories();

    // A narrow window gives its width to the frequency: the radio name and
    // status shorten and the signal meter narrows, and the buttons stay as they are.
    private const double FullTopBarWidth = 1180;

    private void FitVfo()
    {
        // The buttons beside the frequency keep their size; the frequency gets the rest.
        // In the side panel Step, lock, RIT and XIT sit under the frequency, not beside it.
        double others = (_side ? 0 : StepLockButtons.DesiredSize.Width + OffsetButtons.DesiredSize.Width)
                        + SliceBadges.DesiredSize.Width + SliceBadges.Margin.Right;
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

    // With two panadapters the top one holds slices A1/A2; with one, a click
    // tunes whichever slice is active (click a tag to switch).
    private void TuneA(long hz)
    {
        if (Vm is not { } vm || vm.TryPlaceNotch(hz)) return;
        hz = vm.ClickFrequency(hz);
        if (vm.ShowPanadapterB) _ = vm.TuneSliceAsync(vm.TuneTargetFor(0), hz);
        else vm.TuneTo(hz);
    }

    private static readonly IBrush[] ActiveOutline =
        [new SolidColorBrush(Color.Parse("#80F25F5C")), new SolidColorBrush(Color.Parse("#80F5A524"))];

    // Two panadapters while B1 is on, the active one outlined in its pan's
    // slice colour; one, unoutlined, otherwise.
    private void LayoutPanadapters()
    {
        if (Vm is not { } vm) return;
        bool two = vm.ShowPanadapterB;
        bool beside = two && vm.PanadaptersSideBySide;
        bool stacked = two && !beside;
        // RX1's panadapter, the bar, RX2's: down the rows or across the columns.
        GridLength none = new(0), bar = new(6), one = new(1, GridUnitType.Star);
        GridLength a = new(vm.PanSplit, GridUnitType.Star), b = new(1 - vm.PanSplit, GridUnitType.Star);
        var rows = PanGrid.RowDefinitions;
        var cols = PanGrid.ColumnDefinitions;
        (rows[0].Height, rows[1].Height, rows[2].Height) = stacked ? (a, bar, b) : (one, none, none);
        (cols[0].Width, cols[1].Width, cols[2].Width) = beside ? (a, bar, b) : (one, none, none);
        rows[0].MinHeight = rows[2].MinHeight = stacked ? 90 : 0;
        cols[0].MinWidth = cols[2].MinWidth = beside ? 220 : 0;
        Grid.SetRow(PanSplitter, beside ? 0 : 1);
        Grid.SetColumn(PanSplitter, beside ? 1 : 0);
        PanSplitter.ResizeDirection = beside ? GridResizeDirection.Columns : GridResizeDirection.Rows;
        Grid.SetRow(PanBorderB, beside ? 0 : 2);
        Grid.SetColumn(PanBorderB, beside ? 2 : 0);
        PanBorderA.BorderBrush = two && vm.ActivePan == 0 ? ActiveOutline[0] : Brushes.Transparent;
        PanBorderB.BorderBrush = two && vm.ActivePan == 1 ? ActiveOutline[1] : Brushes.Transparent;
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

    // The large-button layout; its own view button comes back.
    private void OnViewTouch(object? sender, RoutedEventArgs e) =>
        (TopLevel.GetTopLevel(this) as MainWindow)?.SwitchLayout(Platform.UiLayout.Touch);

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

    // The bar at the side panel's left edge: drag it to widen or narrow the
    // panel (kept in the settings), double-click for the standard width.
    private double? _gripFrom;
    private double _gripWidth;

    private void WireSideGrip()
    {
        SideGrip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(SideGrip).Properties.IsLeftButtonPressed) return;
            _gripFrom = e.GetPosition(this).X;
            _gripWidth = SideRail.Bounds.Width;
            e.Pointer.Capture(SideGrip);
            e.Handled = true;
        };
        SideGrip.PointerMoved += (_, e) =>
        {
            if (_gripFrom is not double from) return;
            SideRail.Width = Math.Clamp(_gripWidth + from - e.GetPosition(this).X, MainViewModel.SideRailMin, SideRailMost());
        };
        SideGrip.PointerReleased += (_, e) => { e.Pointer.Capture(null); EndGrip(); };
        SideGrip.PointerCaptureLost += (_, _) => EndGrip();
        SideGrip.DoubleTapped += (_, e) =>
        {
            if (Vm is not { } vm) return;
            vm.SideRailWidth = MainViewModel.SideRailDefault;
            FitSideRail();
            e.Handled = true;
        };
    }

    // The widest the panel may be while leaving the panadapter 420 px. Before
    // the first layout pass Bounds.Width is 0, so only the fixed limits apply.
    private double SideRailMost() => Bounds.Width <= 0
        ? MainViewModel.SideRailMax
        : Math.Max(MainViewModel.SideRailMin, Math.Min(MainViewModel.SideRailMax, Bounds.Width - 420));

    // Shows the saved width, cut down to fit the window. Never writes it back,
    // so widening the window again restores the saved width.
    private void FitSideRail()
    {
        if (_gripFrom is not null || Vm is not { } vm) return;
        SideRail.Width = Math.Clamp(vm.SideRailWidth, MainViewModel.SideRailMin, SideRailMost());
    }

    private void EndGrip()
    {
        if (_gripFrom is null) return;
        _gripFrom = null;
        if (Vm is { } vm) vm.SideRailWidth = SideRail.Width;
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
            // REC with the voice keyer first, then the transmit row, then MOX and TUNE last.
            Move(TxBlock, SideTxHost, 1);
            Move(RecButton, SideRecRow);
            Move(VkBlock, SideRecRow);
            Move(MoxToggle, SideMoxRow);
            Move(TuneToggle, SideMoxRow);
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
            Move(MoxToggle, TxBlock, 0);
            Move(TuneToggle, TxBlock, 1);
            Move(RecButton, TxBlock);
            Move(AudioBlock, BottomWrap, 0);
            Move(TxBlock, BottomWrap, 1);
            Move(VkBlock, BottomWrap, 2);
        }
        TopHost.IsVisible = BottomPanel.IsVisible = !side;
        SideRail.IsVisible = side;

        Grid.SetColumn(TuneToggle, side ? 1 : 0);
        // The volume sliders: a fixed width below, the panel's width at the side.
        AfMain.Width = RxPanRow.Width = side ? double.NaN : 150;

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
        // The clock: three short rows at the end of the bottom row, or one
        // line in the side panel's bottom corner.
        ClockBlock.Margin = side ? default : new Thickness(12, 0, 4, 0);
        ClockBlock.ColumnDefinitions = new ColumnDefinitions(side ? "Auto,Auto,Auto,Auto,Auto,Auto" : "Auto,Auto");
        for (int i = 0; i < ClockBlock.Children.Count; i++)
        {
            var part = ClockBlock.Children[i];
            Grid.SetRow(part, side ? 0 : i / 2);
            Grid.SetColumn(part, side ? i : i % 2);
            // A wider gap before each label after the first, so the three pairs read apart.
            part.Margin = side && i > 0 && i % 2 == 0 ? new Thickness(10, 0, 0, 0) : default;
        }

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
        FitSideRail();
        LayoutPanadapters();
        RefreshMiniVfos();
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
        (MainViewModel.SlicePan(rx) == 1 && _wired is { ShowPanadapterB: true } ? SpectrumB : Spectrum).PeakHz(lo, hi, db);

    private void OnDisplay(DisplayFrame f)
    {
        if (_wired is not { } vm) return;
        Spectrum.AgcLineDb = SpectrumB.AgcLineDb = vm.AgcLineDb();
        Spectrum.Notches = SpectrumB.Notches = vm.NotchMarks();
        Spectrum.Strip = SpectrumB.Strip = vm.BandStrip;
        Spectrum.StripOpacity = SpectrumB.StripOpacity = vm.BandStripOpacity;
        SetZoomViews(vm);
        Spectrum.HoldView = SpectrumB.HoldView = vm.CtunOn;
        Spectrum.TxRangeDb = SpectrumB.TxRangeDb = vm.TxWaterfallRangeDb;
        Spectrum.Transmitting = SpectrumB.Transmitting = vm.IsTransmitting;
        // A slice on another's stream gets that stream's frames: no use to
        // the close-up while its dial is outside them.
        if (f.RxId == vm.ActiveRx && !vm.SliceOutOfSpan(f.RxId))
        {
            FilterScope.AverageSeconds = vm.CloseUpAverageSec;
            FilterScope.ColouredFill = vm.CloseUpColoured;
            FilterScope.PushFrame(f, vm.VfoHz, vm.FilterLowHz, vm.FilterHighHz, vm.FilterIsSymmetric, vm.CwPitchHz);
        }
        if (f.RxId == MainViewModel.PanPrimary(1))
        {
            if (!vm.ShowPanadapterB) return;
            SpectrumB.Slices = vm.PanMarks(1);
            SpectrumB.PushFrame(f);
            (SpectrumB.SpecMinDb, SpectrumB.SpecMaxDb) = vm.SliceBRange(SpectrumB.NoiseFloorDb);
            return;
        }
        if (f.RxId != MainViewModel.PanPrimary(0)) return;
        Spectrum.Slices = vm.PanMarks(0);
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

    // Each panadapter has its receiver's own span; RX2's also places its dial
    // by its own Centre (RX1's is placed by the engine).
    private void SetZoomViews(MainViewModel vm)
    {
        Spectrum.TargetSpanHz = vm.ZoomTargetSpanFor(0);
        Spectrum.DialRx = vm.PanDialSlice(0);
        SpectrumB.TargetSpanHz = vm.ZoomTargetSpanFor(1);
        SpectrumB.DialRx = vm.PanDialSlice(1);
        SpectrumB.DialAt = vm.Rx2DialAt;
    }

    private void OnDisplayReset() { Spectrum.Clear(); SpectrumB.Clear(); FilterScope.Clear(); }

    // The panadapter chips show by slice state: A1's chip while any other
    // slice is on (alone, the main VFO covers it), A2/B2 when that slice is
    // on, B1 always (its pan only renders when shown). The chips draw their
    // own contents.
    private void RefreshMiniVfos()
    {
        if (Vm is not { } vm) return;
        // Which receiver each chip shows follows the layout; a pan with no
        // second slice keeps its hidden chip as it is.
        MiniA1.Slice = MainViewModel.PanPrimary(0);
        if (MainViewModel.PanSecondary(0) is >= 0 and int a2) MiniA2.Slice = a2;
        MiniB1.Slice = MainViewModel.PanPrimary(1);
        if (MainViewModel.PanSecondary(1) is >= 0 and int b2) MiniB2.Slice = b2;
        MiniA1.IsVisible = vm.SliceCount > 1;
        MiniA2.IsVisible = vm.SliceOn(MainViewModel.PanSecondary(0));
        MiniB1.IsVisible = true;
        MiniB2.IsVisible = vm.SliceOn(MainViewModel.PanSecondary(1));
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ShowPanadapterB) or nameof(MainViewModel.ActiveRx)
            or nameof(MainViewModel.PanadaptersSideBySide) or nameof(MainViewModel.PanSplit))
            LayoutPanadapters();
        else if (e.PropertyName == nameof(MainViewModel.SidePanel) && _wired is { } v)
            Arrange(v.SidePanel);
        // Zooming: redraw now with the new span instead of waiting for the next
        // frame. At each whole zoom level the engine sends no frames for about
        // 130 ms while it rebuilds its analyser, and the picture froze then leapt.
        else if (e.PropertyName is nameof(MainViewModel.ZoomSlider) or nameof(MainViewModel.DialPosition) && _wired is { } vm)
        {
            SetZoomViews(vm);
            Spectrum.InvalidateVisual();
            SpectrumB.InvalidateVisual();
        }
        if (e.PropertyName is nameof(MainViewModel.SliceCount) or nameof(MainViewModel.ShowPanadapterB)
            or nameof(MainViewModel.ActiveRx) or nameof(MainViewModel.TxRx))
            RefreshMiniVfos();
    }
}
