using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> for a row that needs to create a directory symbolic link, which
/// the runner reports as <b>Skipped</b>, with the reason given, instead of silently as Passed.
/// <see cref="UnixOnlyFactAttribute"/> explains the mechanism and why xunit 2.9.3 leaves no
/// alternative to a discovery-time skip.
///
/// <para>
/// This is <b>not</b> a platform skip, and must not be narrowed into one. Only Windows refuses,
/// and only for want of Developer Mode or elevation — so a Windows box that has either runs the
/// row, and should, because following a link is precisely the behaviour Windows gets wrong by
/// default (<c>DriveInfo</c> reduces a path to its drive letter lexically). The condition is
/// therefore a capability of the box, not its operating system, and the only way to know it is to
/// try.
/// </para>
///
/// <para>
/// <b>The probe runs once</b> — at discovery, for the whole assembly, from the static field below
/// — against a throwaway target under the temp root, and removes both the link and the target
/// afterwards. It cannot throw: an attribute constructor that throws fails discovery for the
/// entire class, so every failure mode is read as "refused", which stands the row down rather
/// than running it on a false premise.
/// </para>
///
/// <para>
/// <b>It does not replace the in-body guard.</b> The row has to create its own link, against its
/// own marked target, and that attempt can still be refused; the guard stays, and becomes an
/// assertion rather than a silent return, so that a disagreement between the two is reported
/// instead of being read as a pass.
/// </para>
/// </summary>
public sealed class DirectorySymbolicLinkFactAttribute : FactAttribute
{
    private static readonly bool HostAllowsThem = ProbeForSymbolicLinkSupport();

    /// <param name="reason">Why this row needs to create a directory symbolic link, in the row's
    /// own terms. Shown by the runner against the skipped row and carried into the TRX.</param>
    public DirectorySymbolicLinkFactAttribute(string reason)
    {
        if (!HostAllowsThem)
        {
            Skip = reason;
        }
    }

    private static bool ProbeForSymbolicLinkSupport()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var target = Path.Combine(Path.GetTempPath(), "daqifi-symlink-probe-target-" + suffix);
        var link = Path.Combine(Path.GetTempPath(), "daqifi-symlink-probe-link-" + suffix);

        try
        {
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            Clean(link, target);
        }
    }

    private static void Clean(string link, string target)
    {
        try
        {
            // Delete the link itself, never through it: a recursive delete on the link would take
            // the target's contents with it on some platforms.
            if (Directory.Exists(link) || new DirectoryInfo(link).LinkTarget is not null)
            {
                Directory.Delete(link);
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leaked probe directory under the temp root is not worth failing discovery for.
        }
    }
}
