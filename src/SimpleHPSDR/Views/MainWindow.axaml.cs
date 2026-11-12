using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SimpleHPSDR.Engine;
using SimpleHPSDR.Platform;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Views;

/// <summary>
/// The main window: menus, dialogs, the mini and CW windows, and starting
/// the engine. What's drawn below the menu bar is a layout view: the desktop
/// one, or the large-target one for small screens (Config > Radio > Layout).
/// </summary>
public partial class MainWindow : Window
{
    private StationGuideWindow? _guide;
    private SettingsWindow? _settings;

    public MainWindow()
    {
        InitializeComponent();
        BuildMenu();
        ShowLayout(LayoutMode.Current);
        LayoutMode.Changed += ShowLayout;
        Closed += (_, _) => LayoutMode.Changed -= ShowLayout;
        // F5 to F8: the voice keyer buttons, wherever the focus is in the window.
        AddHandler(KeyDownEvent, OnVoiceKeyerKey, RoutingStrategies.Tunnel);
    }

    // macOS takes the bare top-row keys for itself (F5 is dictation) unless fn
    // is held, so ⌘1 to ⌘4 there, Ctrl+1 to 4 elsewhere, fire them as well.
    private static readonly KeyModifiers VoiceKeyerModifier =
        OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private void OnVoiceKeyerKey(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        int slot = e.KeyModifiers == KeyModifiers.None
            ? e.Key switch { Key.F5 => 1, Key.F6 => 2, Key.F7 => 3, Key.F8 => 4, _ => 0 }
            : e.KeyModifiers == VoiceKeyerModifier
                ? e.Key switch { Key.D1 or Key.NumPad1 => 1, Key.D2 or Key.NumPad2 => 2, Key.D3 or Key.NumPad3 => 3, Key.D4 or Key.NumPad4 => 4, _ => 0 }
                : 0;
        if (slot == 0) return;
        vm.FireVoiceKeyer(slot);
        e.Handled = true;
    }

    /// <summary>The desktop layout, or the large-target one; both use the same view model.</summary>
    private void ShowLayout(UiLayout layout)
    {
        bool touch = layout == UiLayout.Touch;
        if (ViewHost.Content is TouchMainView == touch && ViewHost.Content is not null) return;
        ViewHost.Content = touch ? new TouchMainView() : new DesktopMainView();
        // A small screen chosen automatically gets the whole screen; the
        // desktop layout always gets a normal window back.
        if (touch && Vm is { } vm && LayoutMode.Choice(vm.AppSettings) == LayoutMode.Automatic)
            WindowState = WindowState.FullScreen;
        else if (!touch && WindowState == WindowState.FullScreen)
            WindowState = WindowState.Normal;
    }

    public void ToggleFullScreen() =>
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

    private MainViewModel? Vm => DataContext as MainViewModel;

    // Help everywhere; About is in the application menu on macOS.
    private void BuildMenu()
    {
        var help = new NativeMenuItem("SimpleHPSDR Help");
        help.Click += (_, _) => new HelpWindow().ShowDialog(this);
        var check = new NativeMenuItem("Check for Updates…");
        check.Click += async (_, _) =>
        {
            var latest = await UpdateChecker.FetchLatestAsync();
            if (latest is not null) Vm?.ShowUpdate(latest);
            await InfoWindow.Update(App.Version, latest).ShowDialog(this);
        };
        var items = new NativeMenu { help, check };
        if (!OperatingSystem.IsMacOS())
        {
            var about = new NativeMenuItem("About SimpleHPSDR");
            about.Click += (_, _) => ShowAbout();
            items.Add(about);
        }
        var analyzer = new NativeMenuItem("Antenna Analyser…");
        analyzer.Click += (_, _) => OpenAnalyzer();
        var wideband = new NativeMenuItem("Wideband View…");
        wideband.Click += (_, _) => OpenWideband();
        NativeMenu.SetMenu(this, new NativeMenu
        {
            new NativeMenuItem("Tools") { Menu = new NativeMenu { analyzer, wideband } },
            new NativeMenuItem("Help") { Menu = items },
        });
    }

    public async void ShowAbout()
    {
        string engine = Vm is { } vm ? await vm.EngineVersionAsync() : "";
        await InfoWindow.About(App.Version, engine).ShowDialog(this);
    }

    // The mini window replaces this one until it's closed.
    public void OpenMini()
    {
        if (Vm is not { } vm) return;
        var (width, height) = vm.MiniSize;
        var mini = new MiniWindow { DataContext = vm, Width = width, Height = height };
        // Back where it was, unless that screen has gone.
        if (vm.MiniPosition is { } at && Screens.ScreenFromPoint(new PixelPoint(at.X, at.Y)) is not null)
        {
            mini.WindowStartupLocation = WindowStartupLocation.Manual;
            mini.Position = new PixelPoint(at.X, at.Y);
        }
        mini.Closing += (_, _) => vm.RememberMiniWindow(mini.Width, mini.Height, mini.Position.X, mini.Position.Y);
        mini.Closed += (_, _) => Dispatcher.UIThread.Post(Show);
        mini.Show();
        Hide();
    }

    private CwWindow? _cw;

    // One antenna analyser window, beside this one.
    private AnalyzerWindow? _analyzer;

    public void OpenAnalyzer()
    {
        if (_analyzer is not null) { _analyzer.Activate(); return; }
        if (Vm is not { } vm) return;
        _analyzer = new AnalyzerWindow { DataContext = vm.NewAnalyzer() };
        _analyzer.Closed += (_, _) => _analyzer = null;
        _analyzer.Show(this);
    }

    // One wideband view, in its own window; it never changes the radio.
    private WidebandWindow? _wideband;

    public void OpenWideband()
    {
        if (_wideband is not null) { _wideband.Activate(); return; }
        if (Vm is not { } vm) return;
        _wideband = new WidebandWindow { DataContext = vm.NewWideband() };
        _wideband.Closed += (_, _) => _wideband = null;
        _wideband.Show(this);
    }

    // One CW window, which stays open beside this one.
    public void OpenCw()
    {
        if (_cw is not null)
        {
            _cw.Activate();
            return;
        }
        _cw = new CwWindow { DataContext = Vm };
        _cw.Closed += (_, _) => _cw = null;
        _cw.Show(this);
    }

    // ---- size and place, kept between runs --------------------------------

    // The last normal (not maximised or full-screen) bounds: a maximised
    // window reports the screen's size, which mustn't become its normal size.
    private (double W, double H, PixelPoint At)? _normalBounds;
    private bool _placementRestored;

    private void RestorePlacement(Settings s)
    {
        if (_placementRestored) return;
        _placementRestored = true;
        PositionChanged += (_, _) => NoteNormalBounds();
        SizeChanged += (_, _) => NoteNormalBounds();
        Closing += (_, _) => SavePlacement(s);
        if (s.MainWidth is not double w || s.MainHeight is not double h) return;
        // Only back where it was if that spot is still on a screen (a monitor
        // may have gone), and never bigger than that screen.
        if (s.MainX is int x && s.MainY is int y
            && Screens.ScreenFromPoint(new PixelPoint(x + 60, y + 20)) is { } screen)
        {
            var area = screen.WorkingArea;
            double scale = screen.Scaling;
            Width = Math.Clamp(w, MinWidth, Math.Max(MinWidth, area.Width / scale));
            Height = Math.Clamp(h, MinHeight, Math.Max(MinHeight, area.Height / scale));
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }
        else
        {
            (Width, Height) = (Math.Max(w, MinWidth), Math.Max(h, MinHeight));
        }
        if (s.MainMaximized && WindowState == WindowState.Normal) WindowState = WindowState.Maximized;
    }

    private void NoteNormalBounds()
    {
        if (WindowState == WindowState.Normal && IsVisible)
            _normalBounds = (ClientSize.Width, ClientSize.Height, Position);
    }

    private void SavePlacement(Settings s)
    {
        NoteNormalBounds();
        if (WindowState == WindowState.FullScreen) return;   // the touch layout's choice, not a size
        if (_normalBounds is { } b)
            (s.MainWidth, s.MainHeight, s.MainX, s.MainY) = (b.W, b.H, b.At.X, b.At.Y);
        s.MainMaximized = WindowState == WindowState.Maximized;
        s.Save();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is not { } vm) return;
        RestorePlacement(vm.AppSettings);
        _ = vm.CheckForUpdatesAtStartAsync();
        vm.ShowRadioPicker = picker => new RadioPickerWindow { DataContext = picker }.PickAsync(this);
        vm.ShowEngineStopped = (radio, reason, log) => new EngineStoppedWindow().AskAsync(this, radio, reason, log);
        vm.ShowFirstStart = (plans, preselected) => new FirstStartWindow().ChooseAsync(this, plans, preselected);
        vm.ShowGuide = () =>
        {
            if (_guide is { IsVisible: true })
            {
                _guide.Activate();
                return;
            }
            _guide = new StationGuideWindow { DataContext = vm.ActiveGuide };
            _guide.Show(this);
        };
        vm.CloseGuide = () => { if (_guide is { IsVisible: true }) _guide.Close(); };
        vm.QuitRequested += () => Dispatcher.UIThread.Post(Close);   // a G2 panel button held
        vm.ShowDspPrep = prep => new DspPrepWindow { DataContext = prep }.ShowDialog(this);
        vm.ShowPsSetup = setup => new PsSetupWindow { DataContext = setup }.ShowDialog(this);
        vm.ShowSettings = settings =>
        {
            var closed = new TaskCompletionSource();
            var window = new SettingsWindow { DataContext = settings };
            window.Closed += (_, _) =>
            {
                _settings = null;
                closed.TrySetResult();
            };
            _settings = window;
            window.Show(this);
            return closed.Task;
        };
        vm.FocusSettings = () => _settings?.Activate();
        vm.CloseSettings = () => _settings?.Close();
    }

    private bool _started;

    // This also fires when the window comes back after being hidden for the
    // mini window, and starting twice would restart the engine.
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_started) return;
        _started = true;
        if (Vm is { } vm) await vm.StartAsync();
    }
}
