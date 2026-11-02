using System.IO;

namespace Daqifi.Desktop.Configuration;

/// <summary>The two fields of a Windows Firewall rule that identify it as this app's own.</summary>
public sealed record FirewallRuleInfo(string? Name, string? ApplicationName);

/// <summary>
/// Decides whether this app has already added its firewall rule for this copy of itself (#353).
/// Kept apart from the COM reading so it can be tested on any OS.
/// </summary>
/// <remarks>
/// <para>
/// Upstream matched on the rule name alone. A rule is bound to one executable, so on a PC that
/// already had a rule of that name for another program (the WPF app's, or another copy of this
/// one) this copy never got a rule. The match is now name <b>and</b> program.
/// </para>
/// <para>
/// Only the app's own rule counts, never someone else's rule for the same program. Deciding
/// whether another rule really admits discovery means reading every way Windows lets a rule
/// narrow traffic (addresses, ports, adapters, services, IPsec authentication, authorised users
/// and more), and missing one leaves discovery silently blocked. The app's own rule is known:
/// it created it. When another rule exists the app adds its own as well, which costs one extra
/// rule and nothing else. And once its own rule exists the app leaves it alone, even if someone
/// has since disabled or narrowed it: that is their decision, not one to undo at every launch.
/// </para>
/// </remarks>
public static class FirewallRuleMatch
{
    /// <summary><c>true</c> when <paramref name="rule"/> is named <paramref name="ruleName"/> and
    /// bound to <paramref name="applicationPath"/>.</summary>
    public static bool IsOwnRule(FirewallRuleInfo rule, string ruleName, string applicationPath) =>
        string.Equals(rule.Name, ruleName, StringComparison.Ordinal)
        && IsSameProgram(rule.ApplicationName, applicationPath);

    /// <summary>
    /// Firewall program paths are case-insensitive and may hold environment variables
    /// (<c>%ProgramFiles%\...</c>). A rule with no program applies to every program and is never
    /// this app's own.
    /// </summary>
    internal static bool IsSameProgram(string? ruleProgram, string applicationPath)
    {
        if (string.IsNullOrWhiteSpace(ruleProgram) || string.IsNullOrWhiteSpace(applicationPath))
            return false;

        return string.Equals(Normalize(ruleProgram), Normalize(applicationPath), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        try
        {
            return Path.GetFullPath(expanded);
        }
        catch (Exception)
        {
            // A path the runtime cannot parse cannot be this app's; compare it as written.
            return expanded;
        }
    }
}
