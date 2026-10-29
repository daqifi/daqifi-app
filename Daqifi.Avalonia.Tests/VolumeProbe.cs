namespace Daqifi.Avalonia.Tests;

/// <summary>
/// Finds a second mounted volume to measure against the root filesystem.
///
/// <para>
/// It lives out here, rather than as a private helper on the one suite that uses it, because
/// <see cref="SecondVolumeFactAttribute"/> has to ask the <i>same</i> question at discovery time
/// that the row asks when it runs. Two copies of that question would be two chances for the skip
/// and the row to disagree — the attribute reporting "this host has a second volume" over a row
/// that then finds none, which is the silent-skip defect of issue #421 one step along.
/// </para>
/// </summary>
internal static class VolumeProbe
{
    private const long HalfAGigabyte = 512L * 1024 * 1024;

    /// <summary>The volume the temp root lives on.</summary>
    public static DriveInfo RootFilesystem() =>
        new(Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!);

    /// <summary>
    /// A mounted volume that is not <paramref name="other"/> and whose free space differs from it
    /// by more than half a gigabyte, so the two readings cannot be confused. <c>null</c> on a
    /// single-volume host — which is what CI is, and what a developer box usually is.
    /// </summary>
    /// <remarks>
    /// Candidates whose mount point is a symlink are rejected: the resolution under test follows
    /// links, so such a mount would legitimately resolve somewhere else and the test would be
    /// asserting against the wrong volume for a reason that has nothing to do with the fix.
    /// </remarks>
    public static DriveInfo? FindAVolumeDistinctFrom(DriveInfo other)
    {
        var otherFreeSpace = ReadableFreeSpace(other);
        if (otherFreeSpace is null)
        {
            return null;
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            var root = drive.RootDirectory.FullName;
            if (string.Equals(root, other.RootDirectory.FullName, StringComparison.Ordinal))
            {
                continue;
            }

            var freeSpace = ReadableFreeSpace(drive);
            if (freeSpace is null or 0 || Math.Abs(freeSpace.Value - otherFreeSpace.Value) <= HalfAGigabyte)
            {
                continue;
            }

            if (!Directory.Exists(root) || new DirectoryInfo(root).LinkTarget is not null)
            {
                continue;
            }

            return drive;
        }

        return null;
    }

    /// <summary>
    /// A drive's free space, or <c>null</c> for the pseudo-filesystems and unreadable mounts that
    /// <see cref="DriveInfo.GetDrives"/> lists alongside the real ones.
    /// </summary>
    private static long? ReadableFreeSpace(DriveInfo drive)
    {
        try
        {
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // DriveNotFoundException is an IOException, so an unmounted candidate lands here too.
            return null;
        }
    }
}
