using Avalonia;

namespace Daqifi.Avalonia;

internal static class Program
{
    // Avalonia configuration, don't remove; also used by the visual designer.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        Daqifi.Avalonia.Services.AppIcons.EnsureRegistered();
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
