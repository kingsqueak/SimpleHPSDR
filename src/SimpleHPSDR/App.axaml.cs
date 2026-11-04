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

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

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
            MacActivity.Begin("Live radio audio and display");
            SliderWheel.Enable();
            PopupTimeout.Enable();
            var vm = new MainViewModel();
            ApplyTheme(vm.Theme);
            desktop.MainWindow = new MainWindow { DataContext = vm };
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
