using Avalonia;
using Avalonia.Controls;
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
        NativeMenu.SetMenu(this, new NativeMenu { new NativeMenuItem("Help") { Menu = items } });
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

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is not { } vm) return;
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
        vm.ShowDspPrep = prep => new DspPrepWindow { DataContext = prep }.ShowDialog(this);
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
