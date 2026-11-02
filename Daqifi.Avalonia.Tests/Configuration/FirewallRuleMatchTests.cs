using Daqifi.Desktop.Configuration;
using Xunit;

namespace Daqifi.Avalonia.Tests.Configuration;

/// <summary>
/// Which rule counts as this app's own (#353). Upstream asked only whether a rule named
/// "DAQiFi Desktop" existed, so a PC with the WPF app's rule of that name, bound to a different
/// exe, never got one for this app.
/// </summary>
public class FirewallRuleMatchTests
{
    private const string App = @"C:\Program Files\Daqifi.App\current\Daqifi.Avalonia.Desktop.exe";
    private const string Name = "DAQiFi";

    [Fact]
    public void Our_rule_for_this_exe_matches() =>
        Assert.True(FirewallRuleMatch.IsOwnRule(new(Name, App), Name, App));

    /// <summary>The bug: the right name on another program does not cover this copy.</summary>
    [Fact]
    public void Our_name_on_another_program_does_not_match() =>
        Assert.False(FirewallRuleMatch.IsOwnRule(
            new(Name, @"C:\Program Files (x86)\DAQiFi\DAQiFi Desktop\DAQiFi.exe"), Name, App));

    /// <summary>
    /// Another rule for this exe (say, one Windows made from its own prompt) is not ours. Whether
    /// it really admits discovery would depend on every way a rule can be narrowed, so the app adds
    /// its own instead.
    /// </summary>
    [Theory]
    [InlineData("daqifi.avalonia.desktop.exe")]
    [InlineData("DAQiFi Desktop")]
    [InlineData("daqifi")]
    public void Another_rule_for_this_exe_does_not_match(string otherName) =>
        Assert.False(FirewallRuleMatch.IsOwnRule(new(otherName, App), Name, App));

    /// <summary>Windows stores program paths in whatever case they were given.</summary>
    [Fact]
    public void Program_paths_compare_without_case() =>
        Assert.True(FirewallRuleMatch.IsOwnRule(new(Name, App.ToLowerInvariant()), Name, App));

    /// <summary>A rule for every program is never this app's own.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_rule_for_no_particular_program_does_not_match(string? program) =>
        Assert.False(FirewallRuleMatch.IsOwnRule(new(Name, program), Name, App));

    [Fact]
    public void Environment_variables_in_the_rule_program_are_expanded()
    {
        var variable = "DAQIFI_FW_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, @"C:\Program Files");
        try
        {
            Assert.True(FirewallRuleMatch.IsSameProgram(
                $@"%{variable}%\Daqifi.App\current\Daqifi.Avalonia.Desktop.exe", App));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }
}
