using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> for a row that needs a second mounted volume to measure against
/// the root filesystem, which the runner reports as <b>Skipped</b>, with the reason given,
/// instead of silently as Passed. <see cref="UnixOnlyFactAttribute"/> explains the mechanism and
/// why xunit 2.9.3 leaves no alternative to a discovery-time skip.
///
/// <para>
/// The condition is a fact about how this box is mounted, not about its operating system, and it
/// is the only one of these skips whose probe reads the filesystem without writing to it —
/// <see cref="VolumeProbe"/> enumerates the drives and asks each for its free space. The row and
/// this attribute share that one implementation precisely so that the skip cannot claim a volume
/// the row then fails to find.
/// </para>
///
/// <para>
/// <b>The probe runs once</b>, at discovery, for the whole assembly, from the static field below.
/// It cannot throw: an attribute constructor that throws fails discovery for the entire class, so
/// a host whose drive table cannot be read is treated as single-volume and the row stands down.
/// </para>
/// </summary>
public sealed class SecondVolumeFactAttribute : FactAttribute
{
    private static readonly bool HostHasOne = ProbeForASecondVolume();

    /// <param name="reason">Why this row needs a second volume, in the row's own terms. Shown by
    /// the runner against the skipped row and carried into the TRX.</param>
    public SecondVolumeFactAttribute(string reason)
    {
        if (!HostHasOne)
        {
            Skip = reason;
        }
    }

    private static bool ProbeForASecondVolume()
    {
        try
        {
            return VolumeProbe.FindAVolumeDistinctFrom(VolumeProbe.RootFilesystem()) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
