using Avalonia;
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
        //
        // Guarded like the service below. Run() builds Velopack's locator, which creates its
        // package folder (%LocalAppData%\<packId> for an install for all users, /var/tmp/velopack
        // on Linux) with no catch of its own, so a folder this user cannot write would otherwise
        // stop the app before its window opens.
        if (!TryRunVelopack(args))
        {
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // True to carry on starting the app. Updates are never worth failing startup over: on any
    // failure the app runs on with the default unsupported update service, and offers none.
    // Too early for AppLogger, hence Trace.
    private static bool TryRunVelopack(string[] args)
    {
        try
        {
            VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Velopack startup failed: {ex}");
            // A hook invocation (install, update, uninstall) that failed must still exit rather
            // than open a window in the middle of the installer.
            return !args.Any(a => a.StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase));
        }

        try
        {
            AppUpdates.Current = new VelopackAppUpdateService();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Update service unavailable: {ex}");
        }

        return true;
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        Daqifi.Avalonia.Services.AppIcons.EnsureRegistered();
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
