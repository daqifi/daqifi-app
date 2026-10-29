using Daqifi.Desktop.Common;
using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> for a row that may only run in an <b>un-elevated</b> process,
/// which the runner reports as <b>Skipped</b>, with the reason given, instead of silently as
/// Passed. <see cref="UnixOnlyFactAttribute"/> explains the mechanism and why xunit 2.9.3 leaves
/// no alternative to a discovery-time skip.
///
/// <para>
/// <b>This is the row where the in-body guard has teeth.</b> Past
/// <c>FirewallConfiguration.InitializeFirewallRules</c>'s own elevation check the method goes on
/// to add a <b>real Windows Firewall rule</b>, so on an administrator-token Windows host calling
/// it at all would mutate the developer's actual firewall configuration. The guard therefore has
/// to wrap the <i>call</i>, not merely an assertion in front of it, and this attribute does not
/// replace it: the attribute makes the <i>reporting</i> honest, the guard is what stops the
/// write. Deleting the guard because "the attribute handles it" would reintroduce exactly the
/// side effect it exists to prevent, on a host where this attribute is the only thing standing
/// between the suite and the firewall.
/// </para>
///
/// <para>
/// <b>Why it fails safe.</b> Reading <see cref="AppDataPaths.IsElevated"/> runs that type's
/// static initialization, which is written not to throw but is reached here at <i>discovery</i>
/// time rather than from a test body. If it ever did throw, an unhandled exception in an
/// attribute constructor fails discovery for the whole class; worse, a swallowed one that
/// answered "un-elevated" would hand the row a firewall write on a host that might be elevated.
/// So an unanswerable question is treated as elevated, and the row stands down.
/// </para>
/// </summary>
public sealed class UnelevatedFactAttribute : FactAttribute
{
    /// <param name="reason">Why this row cannot run elevated, in the row's own terms. Shown by
    /// the runner against the skipped row and carried into the TRX.</param>
    public UnelevatedFactAttribute(string reason)
    {
        if (IsElevatedOrUnknowable())
        {
            Skip = reason;
        }
    }

    private static bool IsElevatedOrUnknowable()
    {
        try
        {
            return AppDataPaths.IsElevated;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
