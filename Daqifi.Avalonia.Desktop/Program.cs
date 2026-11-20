using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
        // An update of this install is under way, and the updater would close this copy with
        // whatever it does (#509), so it stops here, before any work starts. First, and apart from
        // any updater setup below, which can fail: the note is all it needs. Hook runs pass.
        if (PendingUpdateCheck(args) is { } version)
        {
            SayUpdateInProgress(version);
            return;
        }

        // Before anything else: Velopack re-invokes the exe with hook arguments during install,
        // update and uninstall, and handles and exits those itself (#343).
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

    /// <summary>
    /// The version an update of this install installs now, or <c>null</c> when this copy can start
    /// (#509). Reads the note and the process list only. Velopack sets <c>VELOPACK_RESTART</c> when its
    /// updater starts the app after an install, and its Run() clears it, so it is read here, first.
    /// </summary>
    private static string? PendingUpdateCheck(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || VelopackAppUpdateService.PendingUpdateNote() is not { } note) { return null; }

            var restartedByUpdater = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VELOPACK_RESTART"));
            return note.VersionBeingInstalled(args, restartedByUpdater, WindowsProcesses.Sessions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Pending update check failed: {ex}");
            return null;
        }
    }

    // Before the app's own windows exist, so a plain Windows message box. Only Windows leaves the
    // note that leads here.
    private static void SayUpdateInProgress(string version)
    {
        var message = $"DAQiFi {version} is being installed on this PC. The update closes every copy of DAQiFi, " +
                      "so DAQiFi cannot start now. Try again in a few minutes.";
        if (OperatingSystem.IsWindows())
        {
            _ = MessageBoxW(IntPtr.Zero, message, "DAQiFi", MB_OK | MB_ICONINFORMATION);
        }
        else
        {
            System.Diagnostics.Trace.WriteLine(message);
        }
    }

    private const uint MB_OK = 0x0;
    private const uint MB_ICONINFORMATION = 0x40;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [SupportedOSPlatform("windows")]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    public static AppBuilder BuildAvaloniaApp()
    {
        Daqifi.Avalonia.Services.AppIcons.EnsureRegistered();
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
