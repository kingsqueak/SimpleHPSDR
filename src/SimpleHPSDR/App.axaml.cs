using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using SimpleHPSDR.Controls;
using SimpleHPSDR.Platform;
using SimpleHPSDR.ViewModels;
using SimpleHPSDR.Views;

namespace SimpleHPSDR;

public partial class App : Application
{
    private PosixSignalRegistration[] _signals = [];

    /// <summary>The program's version, without the build hash.</summary>
    public static string Version { get; } =
        (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")
        .Split('+')[0];

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // The name in the macOS menu bar.
        Name = $"SimpleHPSDR {Version}";
    }

    private void OnAbout(object? sender, EventArgs e) =>
        ((ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow)?.ShowAbout();

    /// <summary>System, Dark or Light. System follows the OS as it changes.</summary>
    public static void ApplyTheme(string theme)
    {
        if (Current is { } app)
            app.RequestedThemeVariant = theme switch
            {
                "Dark" => ThemeVariant.Dark,
                "Light" => ThemeVariant.Light,
                _ => ThemeVariant.Default,
            };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Development aid: the guide alone, with no engine or radio.
            if (Environment.GetEnvironmentVariable("SIMPLEHPSDR_GUIDE_ONLY") != null)
            {
                var guide = new StationGuideViewModel(Settings.Load(), _ => { });
                guide.Start();
                desktop.MainWindow = new StationGuideWindow { DataContext = guide };
                base.OnFrameworkInitializationCompleted();
                return;
            }
            MacActivity.Begin("Live radio audio and display");
            SliderWheel.Enable();
            PopupTimeout.Enable();
            ScreenFit.Enable();
            LongPress.Enable();
            TouchWindows.Enable();
            var vm = new MainViewModel();
            ApplyTheme(vm.Theme);
            var main = new MainWindow { DataContext = vm };
            LayoutMode.Watch(main, vm.AppSettings);
            desktop.MainWindow = main;
            SingleInstance.Listen(main);
            desktop.Exit += (_, _) => vm.Dispose();
            // Release the radio and stop the engine on kill/Ctrl-C too. The normal
            // shutdown runs on the UI thread: disposing from the signal thread
            // pulled the engine client out from under the still-running UI, which
            // threw and aborted the app before the engine was stopped.
            _signals =
            [
                PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => ShutdownOnSignal(ctx, desktop, vm)),
                PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => ShutdownOnSignal(ctx, desktop, vm)),
            ];
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static int _signalled;

    /// <summary>Close the app the normal way (its Exit runs vm.Dispose on the UI
    /// thread). If the UI doesn't get there in time, release the radio from here.</summary>
    private static void ShutdownOnSignal(PosixSignalContext ctx, IClassicDesktopStyleApplicationLifetime desktop,
        MainViewModel vm)
    {
        ctx.Cancel = true;   // we exit ourselves, once the radio is released
        if (Interlocked.Exchange(ref _signalled, 1) == 1) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => desktop.Shutdown());
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            try { vm.Dispose(); } catch (Exception) { }
            Environment.Exit(0);
        });
    }
}
