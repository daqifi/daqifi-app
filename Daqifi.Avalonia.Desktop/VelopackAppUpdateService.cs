using System.Reflection;
using Daqifi.Desktop.UpdateVersion;
using Velopack;
using Velopack.Sources;

namespace Daqifi.Avalonia;

/// <summary>
/// <see cref="IAppUpdateService"/> over Velopack, reading releases from this repository's GitHub
/// Releases (#343).
/// </summary>
internal sealed class VelopackAppUpdateService : IAppUpdateService
{
    private const string REPOSITORY = "https://github.com/daqifi/daqifi-app";

    /// <summary>
    /// Points the updater at another feed: a folder or URL holding <c>vpk pack</c> output. For
    /// testing an install against a local release, the way <c>DAQIFI_DATA_DIR</c> isolates data.
    /// </summary>
    private const string FEED_OVERRIDE_VARIABLE = "DAQIFI_UPDATE_FEED";

    private readonly UpdateManager _manager;
    private UpdateInfo? _pending;

    public VelopackAppUpdateService()
    {
        var feed = Environment.GetEnvironmentVariable(FEED_OVERRIDE_VARIABLE);
        _manager = string.IsNullOrWhiteSpace(feed)
            ? new UpdateManager(new GithubSource(REPOSITORY, accessToken: null, prerelease: IsPrerelease()))
            : new UpdateManager(feed.Trim());

        Support = AppUpdates.ResolveSupport(_manager.IsInstalled, IsOwnedExternally(), MachinePolicy.DisableUpdates);
        ApplyNeedsElevation = Support == AppUpdateSupport.Supported
                              && OperatingSystem.IsWindows()
                              && !IsInstallFolderWritable();
    }

    public AppUpdateSupport Support { get; }

    public bool ApplyNeedsElevation { get; }

    public async Task<AppUpdateOffer?> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = await _manager.CheckForUpdatesAsync();
        _pending = info;
        return info is null
            ? null
            : new AppUpdateOffer(info.TargetFullRelease.Version.ToString(), info.TargetFullRelease.NotesMarkdown);
    }

    public Task DownloadAsync(AppUpdateOffer offer, IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
        _manager.DownloadUpdatesAsync(PendingFor(offer), p => progress?.Report(p), cancellationToken);

    public void ApplyAndRestart(AppUpdateOffer offer) =>
        _manager.ApplyUpdatesAndRestart(PendingFor(offer).TargetFullRelease);

    /// <summary>
    /// Velopack's apply step ends every process whose image is under the install folder, in every
    /// session it can open, and for an install for all users it runs as administrator. This looks
    /// at the same processes from this one, which usually cannot read another user's paths, so for
    /// that install <see cref="InstallUsage"/> counts an unreadable process with DAQiFi's name as in use.
    /// </summary>
    public bool IsInstallInUseElsewhere()
    {
        if (!OperatingSystem.IsWindows()) { return false; }

        var root = InstallRoot() ?? throw new InvalidOperationException("The install folder could not be found.");
        return InstallUsage.IsInUseElsewhere(
            WindowsProcesses.Snapshot(), Environment.ProcessId, root, AppProcessNames(), ApplyNeedsElevation);
    }

    // This exe, and the stub in the install root that the Start Menu shortcut starts. The stub
    // exits once it has started the app, but it is also under the folder while it runs.
    private static string[] AppProcessNames()
    {
        var self = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
        return string.IsNullOrEmpty(self) ? ["DAQiFi"] : [self, "DAQiFi"];
    }

    private UpdateInfo PendingFor(AppUpdateOffer offer)
    {
        var pending = _pending;
        if (pending is null || pending.TargetFullRelease.Version.ToString() != offer.Version)
        {
            throw new InvalidOperationException($"No checked update matches version {offer.Version}.");
        }

        return pending;
    }

    /// <summary>
    /// The <c>DaqifiUpdatesOwnedExternally</c> MSBuild property, for builds whose channel owns
    /// updates (#480). A build-time switch because a store build and a direct download are the
    /// same OS and RID, so nothing at runtime can tell them apart.
    /// </summary>
    private static bool IsOwnedExternally() =>
        typeof(VelopackAppUpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(a => a.Key == "DaqifiUpdatesOwnedExternally"
                      && string.Equals(a.Value, "true", StringComparison.OrdinalIgnoreCase));

    private static bool IsPrerelease() =>
        typeof(VelopackAppUpdateService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Contains('-') == true;

    /// <summary>
    /// Velopack installs to <c>{root}\current\</c>; this exe runs from <c>current</c>, and updates
    /// replace it from <c>{root}</c>. An install for all users puts <c>{root}</c> in Program Files.
    /// </summary>
    private static string? InstallRoot() =>
        Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName;

    private static bool IsInstallFolderWritable()
    {
        try
        {
            var root = InstallRoot();
            if (root is null) { return false; }

            var probe = Path.Combine(root, $".daqifi-write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
