using System.Globalization;
using System.IO;

namespace Daqifi.Desktop.Configuration;

/// <summary>
/// The fields of one Windows Firewall rule that decide whether it already lets discovery
/// traffic reach this app. Values are the raw <c>INetFwRule</c> ones: <see cref="Direction"/>
/// 1 = inbound, <see cref="Action"/> 1 = allow, <see cref="Protocol"/> 17 = UDP, 256 = any.
/// </summary>
public sealed record FirewallRuleInfo(
    string? ApplicationName,
    int Direction,
    int Action,
    bool Enabled,
    int Protocol,
    string? LocalPorts,
    string? RemoteAddresses = "*");

/// <summary>
/// Decides whether an existing firewall rule already covers this app's discovery port, so the
/// app adds its own only when nothing does (#353). Kept apart from the COM reading so the rules
/// can be tested on any OS.
/// </summary>
public static class FirewallRuleMatch
{
    private const int Inbound = 1;
    private const int Allow = 1;
    private const int Udp = 17;
    private const int AnyProtocol = 256;

    /// <summary>
    /// <c>true</c> when <paramref name="rule"/> is an enabled inbound allow rule for
    /// <paramref name="applicationPath"/> that admits UDP on <paramref name="port"/>.
    /// </summary>
    /// <remarks>
    /// Profiles are not checked: a rule Windows created from its own prompt covers only the
    /// network types the user ticked, and this app does not second-guess that choice. A block
    /// rule for the same program still wins over any allow rule; that is Windows' decision too.
    /// </remarks>
    public static bool AllowsInboundUdp(FirewallRuleInfo rule, string applicationPath, int port)
    {
        if (!rule.Enabled || rule.Direction != Inbound || rule.Action != Allow)
            return false;

        if (rule.Protocol != Udp && rule.Protocol != AnyProtocol)
            return false;

        return IsSameProgram(rule.ApplicationName, applicationPath)
               && PortsInclude(rule.LocalPorts, port)
               && IsAnyAddress(rule.RemoteAddresses);
    }

    /// <summary>
    /// Devices can be at any address on the network, so only a rule open to every sender counts.
    /// A rule limited to some addresses (say, one a user made for a single device) is left alone,
    /// and the app adds its own; at worst that is one rule more than needed.
    /// </summary>
    internal static bool IsAnyAddress(string? remoteAddresses) =>
        string.IsNullOrWhiteSpace(remoteAddresses) || remoteAddresses.Trim() == "*";

    /// <summary>
    /// Firewall program paths are case-insensitive and may hold environment variables
    /// (<c>%ProgramFiles%\...</c>). A rule with no program applies to every program, but that
    /// is a broader rule than this app should count as its own, so it does not match.
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

    /// <summary>
    /// A rule's local ports are <c>*</c> (or empty) for all ports, otherwise a comma-separated
    /// list of numbers and <c>low-high</c> ranges. Keywords such as <c>RPC</c> never match.
    /// </summary>
    internal static bool PortsInclude(string? localPorts, int port)
    {
        if (string.IsNullOrWhiteSpace(localPorts) || localPorts.Trim() == "*")
            return true;

        foreach (var raw in localPorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = raw.IndexOf('-');
            if (dash < 0)
            {
                if (TryPort(raw, out var single) && single == port)
                    return true;
                continue;
            }

            if (TryPort(raw[..dash], out var low) && TryPort(raw[(dash + 1)..], out var high)
                && port >= low && port <= high)
                return true;
        }

        return false;
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port);
}
