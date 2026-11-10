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
using Zeus.Contracts;

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
        MemButton.SizeChanged += (_, _) => FitVfo();
        SwapButtons.SizeChanged += (_, _) => FitVfo();
        SwapButtons.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) FitVfo(); };
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
        // margins, so A⇄B takes no room while hidden); the frequency gets the rest.
        double others = MemButton.DesiredSize.Width + StepLockButtons.DesiredSize.Width + OffsetButtons.DesiredSize.Width
                        + (SwapButtons.IsVisible ? SwapButtons.DesiredSize.Width : 0);
        VfoScale.MaxWidth = Math.Max(0, VfoArea.Bounds.Width - others);
    }

    private void FitTopBar(double width)
    {
        bool narrow = width < FullTopBarWidth;
        RadioText.MaxWidth = narrow ? 96 : double.PositiveInfinity;
        StatusText.MaxWidth = narrow ? 230 : 300;
        SignalPanel.Width = Math.Clamp(width - 800, 236, 300);
        SignalLabel.IsVisible = SignalPanel.Width >= 290;
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
        PanGrid.RowDefinitions[1].Height = two ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PanGrid.RowSpacing = two ? 6 : 0;
        PanBorderA.BorderBrush = two && vm.ActiveRx == 0 ? ActiveOutline[0] : Brushes.Transparent;
        PanBorderB.BorderBrush = two && vm.ActiveRx == 1 ? ActiveOutline[1] : Brushes.Transparent;
        if (!two) SpectrumB.Clear();
    }

    private void OnOpenMini(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.OpenMini();

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
        if (f.RxId == vm.ActiveRx)
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

    // The recorder deck sits at the bottom of the waterfall, its left edge at
    // REC's (kept inside the window when REC is near the right side).
    private void PlaceDeck()
    {
        if (!DeckHost.IsVisible) return;
        var at = RecButton.TranslatePoint(new Point(0, 0), MainGrid);
        if (at is null) return;
        double x = Math.Max(0, Math.Min(at.Value.X, MainGrid.Bounds.Width - 24 - DeckHost.Bounds.Width));
        var margin = new Thickness(x, 0, 0, 8);
        if (DeckHost.Margin != margin) DeckHost.Margin = margin;
    }

    private void OnDisplayReset() { Spectrum.Clear(); SpectrumB.Clear(); FilterScope.Clear(); }

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
