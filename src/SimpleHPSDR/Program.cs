using Avalonia;

namespace SimpleHPSDR;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Platform.LayoutMode.ReadCommandLine(args);
        if (!Platform.SingleInstance.Claim()) return;   // the running copy came to the front
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
