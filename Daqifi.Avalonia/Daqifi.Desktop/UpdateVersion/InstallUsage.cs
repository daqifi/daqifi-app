namespace Daqifi.Desktop.UpdateVersion;

/// <summary>A running process, as the updater's pre-flight check sees it.</summary>
/// <param name="Id">The process ID.</param>
/// <param name="Name">The process name without extension, which Windows shows for every session.</param>
/// <param name="ImagePath">
/// The full path of its executable, or <c>null</c> when this process may not read it, as for
/// another user's processes when DAQiFi runs without administrator rights.
/// </param>
public sealed record RunningProcess(int Id, string Name, string? ImagePath);

/// <summary>
/// Whether another process uses this installation. Installing an update closes every process
/// that runs from the install folder, in every user session. For an install for all users the
/// updater runs as administrator, so on a shared PC it can end another person's recording or
/// firmware update.
/// </summary>
public static class InstallUsage
{
    /// <summary>
    /// True when any process other than <paramref name="selfId"/> runs from
    /// <paramref name="installRoot"/>, or may do so.
    /// </summary>
    /// <param name="processes">Every process on the PC, from all sessions.</param>
    /// <param name="selfId">This process, which the updater restarts on purpose.</param>
    /// <param name="installRoot">The install folder, the one the updater clears of processes.</param>
    /// <param name="appProcessNames">
    /// The names a DAQiFi process from this install can have. When a process's path cannot be read,
    /// a matching name counts as in use. Most often it is another user's copy, which is the case
    /// this check exists for.
    /// </param>
    public static bool IsInUseElsewhere(
        IEnumerable<RunningProcess> processes,
        int selfId,
        string installRoot,
        IReadOnlyCollection<string> appProcessNames)
    {
        var root = WithTrailingSeparator(Path.GetFullPath(installRoot));

        foreach (var process in processes)
        {
            if (process.Id == selfId) { continue; }

            if (process.ImagePath is null)
            {
                if (appProcessNames.Contains(process.Name, StringComparer.OrdinalIgnoreCase)) { return true; }
                continue;
            }

            if (IsUnder(process.ImagePath, root)) { return true; }
        }

        return false;
    }

    // Windows paths are case-insensitive. A path that cannot be normalised is not one the updater
    // could match either.
    private static bool IsUnder(string path, string rootWithSeparator)
    {
        try
        {
            return Path.GetFullPath(path).StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string WithTrailingSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
}
