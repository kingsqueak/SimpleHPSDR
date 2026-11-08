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
            desktop.Exit += (_, _) => vm.Dispose();
            // Release the radio and stop the engine on kill/Ctrl-C too.
            _signals =
            [
                PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => vm.Dispose()),
                PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => vm.Dispose()),
            ];
        }
        base.OnFrameworkInitializationCompleted();
    }
}
