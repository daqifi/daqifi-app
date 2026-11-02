using Avalonia;
using Optris.Icons.Avalonia;
using Optris.Icons.Avalonia.MaterialDesign;
using Daqifi.Desktop.UpdateVersion;
using Velopack;

namespace Daqifi.Avalonia;

internal static class Program
{
    // Avalonia configuration, don't remove; also used by the visual designer.
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before anything else: Velopack re-invokes the exe with hook arguments during
        // install, update and uninstall, and handles and exits those itself (#343).
        //
        // No auto-apply: by default a downloaded update that was not installed (say, its UAC prompt
        // was declined) is applied at the next launch. For an install for all users that is an
        // unexplained administrator prompt before the window opens, on every launch, for a user
        // who may not be able to approve it. Updates install only when the user clicks Install.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        AppUpdates.Current = new VelopackAppUpdateService();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        IconProvider.Current.Register<MaterialDesignIconProvider>();
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
