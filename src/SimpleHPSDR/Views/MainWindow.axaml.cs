using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using SimpleHPSDR.Controls;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        BuildMenu();
        SizeChanged += (_, e) => FitTopBar(e.NewSize.Width);
        // RIT and XIT stay full size beside the VFO, which takes what's left.
        VfoArea.SizeChanged += (_, _) => FitVfo();
        OffsetButtons.SizeChanged += (_, _) => FitVfo();
        FrequencyPanel.PointerWheelChanged += (_, e) =>
        {
            Vm?.TuneBySteps(WheelClicks(e));
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
        RitButton.PointerWheelChanged += (_, e) => { Vm?.NudgeRit(WheelClicks(e)); e.Handled = true; };
        XitButton.PointerWheelChanged += (_, e) => { Vm?.NudgeXit(WheelClicks(e)); e.Handled = true; };
        RitButton.ContextRequested += (_, e) => { Vm?.ClearRit(); e.Handled = true; };
        XitButton.ContextRequested += (_, e) => { Vm?.ClearXit(); e.Handled = true; };
        BandButton.ContextRequested += async (_, e) => { e.Handled = true; await ShowBandStackAsync(); };
        Spectrum.TuneRequested += TuneA;
        Spectrum.PanRequested += hz => Vm?.PanTo(0, hz);
        SpectrumB.PanRequested += hz => Vm?.PanTo(1, hz);
        FilterScope.FilterDragged += (low, high) => Vm?.DragFilter(low, high);
        Spectrum.SliceClicked += rx => _ = Vm?.MakeActiveAsync(rx);
        Spectrum.MenuRequested += ShowSliceMenu;
        Spectrum.WheelTune += steps =>
        {
            if (Vm is not { } vm) return;
            if (vm.ShowPanadapterB) _ = vm.TuneSliceByStepsAsync(0, steps);
            else vm.TuneBySteps(steps);   // one panadapter: tune the active slice
        };
        // Slice B's own panadapter tunes slice B, making it active first.
        SpectrumB.TuneRequested += hz =>
        {
            if (Vm is { } vm && !vm.TryPlaceNotch(hz)) _ = vm.TuneSliceAsync(1, hz);
        };
        SpectrumB.SliceClicked += rx => _ = Vm?.MakeActiveAsync(rx);
        SpectrumB.MenuRequested += ShowSliceMenu;
        SpectrumB.WheelTune += steps => _ = Vm?.TuneSliceByStepsAsync(1, steps);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // Help everywhere; About is in the application menu on macOS.
    private void BuildMenu()
    {
        var help = new NativeMenuItem("SimpleHPSDR Help");
        help.Click += (_, _) => InfoWindow.Help().ShowDialog(this);
        var items = new NativeMenu { help };
        if (!OperatingSystem.IsMacOS())
        {
            var about = new NativeMenuItem("About SimpleHPSDR");
            about.Click += (_, _) => ShowAbout();
            items.Add(about);
        }
        NativeMenu.SetMenu(this, new NativeMenu { new NativeMenuItem("Help") { Menu = items } });
    }

    public async void ShowAbout()
    {
        string engine = Vm is { } vm ? await vm.EngineVersionAsync() : "";
        await InfoWindow.About(App.Version, engine).ShowDialog(this);
    }

    // A narrow window gives its width to the frequency: the radio name and
    // status shorten and the signal meter narrows, and the buttons stay as they are.
    private const double FullTopBarWidth = 1180;

    private void FitVfo()
    {
        const double Gap = 10, SwapWidth = 44;
        VfoScale.MaxWidth = Math.Max(0, VfoArea.Bounds.Width - OffsetButtons.Bounds.Width - 2 * Gap - SwapWidth);
    }

    private void FitTopBar(double width)
    {
        bool narrow = width < FullTopBarWidth;
        RadioText.MaxWidth = narrow ? 96 : double.PositiveInfinity;
        StatusText.MaxWidth = narrow ? 230 : 300;
        SignalPanel.Width = Math.Clamp(width - 760, 236, 300);
        SignalLabel.IsVisible = SignalPanel.Width >= 290;
    }

    private static int WheelClicks(PointerWheelEventArgs e) => Math.Sign(SliderWheel.Amount(e));

    // Right-click on the band: frequencies saved on this band.
    private async Task ShowBandStackAsync()
    {
        if (Vm is not { IsConnected: true } vm) return;
        var items = new List<Control>
        {
            new MenuItem
            {
                Header = $"Save {vm.FrequencyText} {vm.ModeLabel}",
                Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(vm.SaveToBandStackAsync),
            },
        };
        var saved = await vm.BandStackAsync();
        foreach (var e in saved)
            items.Add(new MenuItem
            {
                Header = e.Label,
                Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => vm.RecallBandStackAsync(e)),
            });
        if (saved.Count > 0)
        {
            items.Add(new Separator());
            items.Add(new MenuItem
            {
                Header = "Remove",
                ItemsSource = saved.Select(e => new MenuItem
                {
                    Header = e.Label,
                    Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => vm.RemoveBandStackAsync(e)),
                }).ToList(),
            });
        }
        new ContextMenu { ItemsSource = items }.Open(BandButton);
    }

    // With two panadapters the top one is slice A's; with one, a click tunes
    // whichever slice is active (click a tag to switch).
    private void TuneA(long hz)
    {
        if (Vm is not { } vm || vm.TryPlaceNotch(hz)) return;
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
            items.Add(Item($"Add slice B at {at}", () => vm.AddSliceBAsync(hz, ownPanadapter: false)));
            items.Add(Item($"Add slice B at {at} in a new panadapter", () => vm.AddSliceBAsync(hz, ownPanadapter: true)));
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
            items.Add(vm.SliceBOwnPanadapter
                ? Item("Show slice B on this panadapter", () => { vm.SliceBOwnPanadapter = false; return Task.CompletedTask; })
                : Item("Show slice B in its own panadapter", () => { vm.SliceBOwnPanadapter = true; return Task.CompletedTask; }));
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

    private void OnModeOptionsOpening(object? sender, EventArgs e) => _ = Vm?.LoadModeOptionsAsync();

    private void OnSMeterTrimOpening(object? sender, EventArgs e) => _ = Vm?.LoadSMeterTrimAsync();

    // The NB and NR popups show what the engine has when they open.
    private void OnNbParamsOpening(object? sender, EventArgs e)
    {
        if (Vm is { } vm) _ = vm.LoadNoisePanelAsync(vm.NbParams);
    }

    private void OnNrParamsOpening(object? sender, EventArgs e)
    {
        if (Vm is { } vm) _ = vm.LoadNoisePanelAsync(vm.NrParams);
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
        vm.ShowDspPrep = prep => new DspPrepWindow { DataContext = prep }.ShowDialog(this);
        vm.ShowSettings = settings => new SettingsWindow { DataContext = settings }.ShowDialog(this);
        vm.Display += f =>
        {
            Spectrum.AgcLineDb = SpectrumB.AgcLineDb = vm.AgcLineDb();
            Spectrum.Notches = SpectrumB.Notches = vm.NotchMarks();
            if (f.RxId == vm.ActiveRx)
                FilterScope.PushFrame(f, vm.VfoHz, vm.FilterLowHz, vm.FilterHighHz);
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
        };
        vm.DisplayReset += () => { Spectrum.Clear(); SpectrumB.Clear(); FilterScope.Clear(); };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.ShowPanadapterB) or nameof(MainViewModel.ActiveRx))
                LayoutPanadapters();
        };
        LayoutPanadapters();
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Vm is { } vm) await vm.StartAsync();
    }
}
