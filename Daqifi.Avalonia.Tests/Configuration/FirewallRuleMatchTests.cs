using Daqifi.Desktop.Configuration;
using Xunit;

namespace Daqifi.Avalonia.Tests.Configuration;

/// <summary>
/// Which existing firewall rules count as already letting discovery reach this app (#353).
/// Upstream asked only whether a rule named "DAQiFi Desktop" existed, so a PC with the WPF
/// app's rule, bound to a different exe, never got one for this app.
/// </summary>
public class FirewallRuleMatchTests
{
    private const string App = @"C:\Program Files\Daqifi.App\current\Daqifi.Avalonia.Desktop.exe";
    private const int Port = FirewallConfiguration.DiscoveryPort;

    private static FirewallRuleInfo Rule(
        string? program = App, int direction = 1, int action = 1, bool enabled = true,
        int protocol = 17, string? ports = "30303", string? remote = "*") =>
        new(program, direction, action, enabled, protocol, ports, remote);

    [Fact]
    public void Our_own_rule_matches() =>
        Assert.True(FirewallRuleMatch.AllowsInboundUdp(Rule(), App, Port));

    /// <summary>The bug: a rule with the old name but another program does not cover this app.</summary>
    [Fact]
    public void The_WPF_apps_rule_does_not_match() =>
        Assert.False(FirewallRuleMatch.AllowsInboundUdp(
            Rule(program: @"C:\Program Files (x86)\DAQiFi\DAQiFi Desktop\DAQiFi.exe"), App, Port));

    /// <summary>Windows' own prompt writes lower-case paths and allows every UDP port.</summary>
    [Fact]
    public void A_rule_from_the_Windows_prompt_matches() =>
        Assert.True(FirewallRuleMatch.AllowsInboundUdp(
            Rule(program: App.ToLowerInvariant(), ports: "*"), App, Port));

    [Fact]
    public void Any_protocol_matches() =>
        Assert.True(FirewallRuleMatch.AllowsInboundUdp(Rule(protocol: 256), App, Port));

    [Theory]
    [InlineData(6)]   // TCP
    [InlineData(1)]   // ICMP
    public void Another_protocol_does_not_match(int protocol) =>
        Assert.False(FirewallRuleMatch.AllowsInboundUdp(Rule(protocol: protocol), App, Port));

    [Fact]
    public void A_disabled_rule_does_not_match() =>
        Assert.False(FirewallRuleMatch.AllowsInboundUdp(Rule(enabled: false), App, Port));

    [Fact]
    public void An_outbound_rule_does_not_match() =>
        Assert.False(FirewallRuleMatch.AllowsInboundUdp(Rule(direction: 2), App, Port));

    [Fact]
    public void A_block_rule_does_not_match() =>
        Assert.False(FirewallRuleMatch.AllowsInboundUdp(Rule(action: 0), App, Port));

    /// <summary>A rule for every program is broader than this app should treat as its own.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_rule_for_no_particular_program_does_not_match(string? program) =>
        Assert.False(FirewallRuleMatch.AllowsInboundUdp(Rule(program: program), App, Port));

    /// <summary>
    /// A rule open only to some senders still drops discovery replies from devices elsewhere on
    /// the network, so it must not stop the app adding its own.
    /// </summary>
    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("LocalSubnet")]
    [InlineData("10.0.0.0/255.0.0.0")]
    public void A_rule_limited_to_some_senders_does_not_match(string remote) =>
        Assert.False(FirewallRuleMatch.AllowsInboundUdp(Rule(remote: remote), App, Port));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    public void A_rule_open_to_every_sender_matches(string? remote) =>
        Assert.True(FirewallRuleMatch.AllowsInboundUdp(Rule(remote: remote), App, Port));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("*", true)]
    [InlineData("30303", true)]
    [InlineData("80, 30303", true)]
    [InlineData("30000-31000", true)]
    [InlineData("30303-30303", true)]
    [InlineData("30304", false)]
    [InlineData("1-30302", false)]
    [InlineData("RPC", false)]
    [InlineData("30303x", false)]
    [InlineData("-30303", false)]
    public void Local_ports(string? ports, bool expected) =>
        Assert.Equal(expected, FirewallRuleMatch.PortsInclude(ports, Port));

    [Fact]
    public void Environment_variables_in_the_rule_program_are_expanded()
    {
        var name = "DAQIFI_FW_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, @"C:\Program Files");
        try
        {
            Assert.True(FirewallRuleMatch.IsSameProgram($@"%{name}%\Daqifi.App\current\Daqifi.Avalonia.Desktop.exe", App));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
