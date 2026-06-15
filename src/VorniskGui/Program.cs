using Avalonia;

namespace VorniskGui;

internal static class Program
{
    // Avalonia entry point. STAThread is required on Windows; harmless on Linux.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()   // bundle Inter so text metrics are identical across distros (no clipping from missing fonts)
            .LogToTrace();
}
