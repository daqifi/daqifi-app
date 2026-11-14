namespace Daqifi.Desktop.UpdateVersion;

/// <summary>Whether this copy of the app can update itself, and if not, why.</summary>
public enum AppUpdateSupport
{
    /// <summary>Installed by our installer; updates can be checked for and applied.</summary>
    Supported,

    /// <summary>The platform does not self-update (Android, iOS); its store owns updates.</summary>
    PlatformUnsupported,

    /// <summary>Not running from an installed copy, for example a dev build or an unzipped publish.</summary>
    NotInstalled,

    /// <summary>A build whose channel owns updates, such as a store build (#480).</summary>
    OwnedExternally,

    /// <summary>An administrator turned updates off with the machine policy.</summary>
    DisabledByPolicy,
}

/// <summary>A newer version the update service found.</summary>
/// <param name="Version">The version as released, for example <c>3.4.0</c> or <c>3.4.0-beta.1</c>.</param>
/// <param name="ReleaseNotes">The release notes in Markdown, when the release carries any.</param>
public sealed record AppUpdateOffer(string Version, string? ReleaseNotes);

/// <summary>
/// The app's self-update service (#343). Lives in the shared library and speaks only in the app's
/// own types: the Velopack implementation is in the desktop head, because a Velopack package
/// reference here would flow into both mobile heads.
/// </summary>
public interface IAppUpdateService
{
    /// <summary>Whether updates are possible at all. Every other member is only meaningful when this is <see cref="AppUpdateSupport.Supported"/>.</summary>
    AppUpdateSupport Support { get; }

    /// <summary>
    /// True when the install folder is not writable by this process, as for an install for all
    /// users in Program Files. Applying then shows a Windows administrator prompt.
    /// </summary>
    bool ApplyNeedsElevation { get; }

    /// <summary>The newest version available, or <c>null</c> when this copy is current.</summary>
    Task<AppUpdateOffer?> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads <paramref name="offer"/> so <see cref="ApplyAndRestart"/> can install it.</summary>
    Task DownloadAsync(AppUpdateOffer offer, IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Exits the app, installs the downloaded <paramref name="offer"/>, and starts the new version.</summary>
    void ApplyAndRestart(AppUpdateOffer offer);

    /// <summary>
    /// True when another process, in this or any other user's session, runs from this installation.
    /// Installing closes all of them, so the install waits. See <see cref="InstallUsage"/>.
    /// </summary>
    bool IsInstallInUseElsewhere();
}

/// <summary>
/// Registration point for <see cref="IAppUpdateService"/>, set by the desktop head before the app
/// starts, the same way <c>NetworkDiscoveryScope.Current</c> is. Heads that set nothing (the mobile
/// heads) get <see cref="UnsupportedAppUpdateService"/>.
/// </summary>
public static class AppUpdates
{
    public static IAppUpdateService Current { get; set; } = UnsupportedAppUpdateService.Instance;

    /// <summary>
    /// The support state of an installable head. Ordered so the reason reported is the most
    /// specific: a store build is never "not installed", and a policy only matters for a copy that
    /// could otherwise update.
    /// </summary>
    public static AppUpdateSupport ResolveSupport(bool isInstalled, bool ownedExternally, bool disabledByPolicy)
    {
        if (ownedExternally) { return AppUpdateSupport.OwnedExternally; }
        if (!isInstalled) { return AppUpdateSupport.NotInstalled; }
        if (disabledByPolicy) { return AppUpdateSupport.DisabledByPolicy; }
        return AppUpdateSupport.Supported;
    }
}

/// <summary>The service for platforms that do not self-update.</summary>
public sealed class UnsupportedAppUpdateService : IAppUpdateService
{
    public static UnsupportedAppUpdateService Instance { get; } = new();

    private UnsupportedAppUpdateService() { }

    public AppUpdateSupport Support => AppUpdateSupport.PlatformUnsupported;

    public bool ApplyNeedsElevation => false;

    public Task<AppUpdateOffer?> CheckAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<AppUpdateOffer?>(null);

    public Task DownloadAsync(AppUpdateOffer offer, IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This platform does not update itself.");

    public void ApplyAndRestart(AppUpdateOffer offer) =>
        throw new NotSupportedException("This platform does not update itself.");

    public bool IsInstallInUseElsewhere() => false;
}
