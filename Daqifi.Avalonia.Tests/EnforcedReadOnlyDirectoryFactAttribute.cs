using System.Runtime.Versioning;
using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> for a row that needs a directory the current user genuinely
/// cannot write to, which the runner reports as <b>Skipped</b>, with the reason given, instead of
/// silently as Passed. <see cref="UnixOnlyFactAttribute"/> explains the mechanism and why xunit
/// 2.9.3 leaves no alternative to a discovery-time skip.
///
/// <para>
/// Two environments cannot supply one: Windows, which has no <c>UnixFileMode</c> at all, and any
/// run as root, where 0555 is advisory and the write succeeds anyway. Neither is knowable from
/// the platform alone — the second is a fact about the current user — so this probes a throwaway
/// directory under the temp root and asks the filesystem, exactly as the rows' own
/// <c>MakeReadOnly</c> helper does. That is the same reasoning the helper gives for testing
/// enforcement rather than inferring it from an identity: a row built on an inference can pass
/// for the wrong reason.
/// </para>
///
/// <para>
/// <b>The probe runs once</b> — at discovery, for the whole assembly, from the static field below
/// — and cleans up after itself. It cannot throw: an attribute constructor that throws fails
/// discovery for the entire class, so every failure mode is read as "this host cannot enforce
/// it", which stands the row down rather than running it on a false premise.
/// </para>
///
/// <para>
/// <b>It does not replace the in-body guard.</b> Those rows call <c>File.SetUnixFileMode</c>, and
/// <c>CA1416</c> narrows a method's supported platforms by control flow rather than by attribute,
/// so deleting the guard fails the build. The guard stays, and becomes an assertion rather than a
/// silent return: if this probe and the row's own ever disagree, the row says so loudly instead
/// of going back to reporting Passed without having run.
/// </para>
/// </summary>
public sealed class EnforcedReadOnlyDirectoryFactAttribute : FactAttribute
{
    private static readonly bool HostEnforcesIt = ProbeForEnforcement();

    /// <param name="reason">Why this row needs an enforced read-only directory, in the row's own
    /// terms. Shown by the runner against the skipped row and carried into the TRX.</param>
    public EnforcedReadOnlyDirectoryFactAttribute(string reason)
    {
        if (!HostEnforcesIt)
        {
            Skip = reason;
        }
    }

    private static bool ProbeForEnforcement()
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        var directory = Path.Combine(
            Path.GetTempPath(), "daqifi-readonly-probe-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(directory);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            try
            {
                File.Create(Path.Combine(directory, "enforcement-probe")).Dispose();

                // The write went through: this is a run as root, where 0555 does not bind.
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unusable temp root, or a mode the filesystem would not take. Either way this
            // host cannot be shown to enforce it.
            return false;
        }
        finally
        {
            Clean(directory);
        }
    }

    // Writable again first: a 0555 directory accepts no deletions from its own entries, and the
    // root case above left a file inside it.
    [UnsupportedOSPlatform("windows")]
    private static void Clean(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            File.SetUnixFileMode(
                directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leaked probe directory under the temp root is not worth failing discovery for.
        }
    }
}
