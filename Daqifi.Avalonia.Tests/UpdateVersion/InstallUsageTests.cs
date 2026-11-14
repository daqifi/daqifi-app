using Daqifi.Desktop.UpdateVersion;
using Xunit;

namespace Daqifi.Avalonia.Tests.UpdateVersion;

/// <summary>
/// Which processes block an update (#503 review): installing ends every process under the install
/// folder, in every session, so any of them other than this one must stop the install.
/// </summary>
public class InstallUsageTests
{
    private const int SELF = 100;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "Daqifi.App");
    private static readonly string[] Names = ["Daqifi.Avalonia.Desktop", "DAQiFi"];

    private static string Under(params string[] parts) => Path.Combine([Root, .. parts]);

    private static bool InUse(params RunningProcess[] processes) =>
        InstallUsage.IsInUseElsewhere(processes, SELF, Root, Names, appliesForAllUsers: true);

    [Fact]
    public void OnlyThisProcess_IsNotInUse() =>
        Assert.False(InUse(new RunningProcess(SELF, "Daqifi.Avalonia.Desktop", Under("current", "Daqifi.Avalonia.Desktop.exe"))));

    [Fact]
    public void AnotherCopyFromThisInstall_IsInUse() =>
        Assert.True(InUse(new RunningProcess(7, "Daqifi.Avalonia.Desktop", Under("current", "Daqifi.Avalonia.Desktop.exe"))));

    /// <summary>The updater matches by folder, not name, so any process under it counts.</summary>
    [Fact]
    public void AnyProcessUnderTheInstall_IsInUse() =>
        Assert.True(InUse(new RunningProcess(7, "helper", Under("current", "tools", "helper.exe"))));

    [Fact]
    public void PathCaseDiffers_IsStillInUse() =>
        Assert.True(InUse(new RunningProcess(7, "Daqifi.Avalonia.Desktop", Under("current", "Daqifi.Avalonia.Desktop.exe").ToUpperInvariant())));

    /// <summary>A dev build or a copy elsewhere is not something the updater would close.</summary>
    [Fact]
    public void SameNameFromAnotherFolder_IsNotInUse() =>
        Assert.False(InUse(new RunningProcess(7, "Daqifi.Avalonia.Desktop",
            Path.Combine(Path.GetTempPath(), "dev", "Daqifi.Avalonia.Desktop.exe"))));

    /// <summary>A sibling folder sharing the prefix (Daqifi.App2) is not under Daqifi.App.</summary>
    [Fact]
    public void SiblingFolderWithSamePrefix_IsNotInUse() =>
        Assert.False(InUse(new RunningProcess(7, "Daqifi.Avalonia.Desktop", Root + "2" + Path.DirectorySeparatorChar + "x.exe")));

    /// <summary>
    /// Another user's process usually cannot be read without administrator rights. With DAQiFi's
    /// name it most likely is that user's copy, so it counts.
    /// </summary>
    [Theory]
    [InlineData("Daqifi.Avalonia.Desktop")]
    [InlineData("daqifi.avalonia.desktop")]
    [InlineData("DAQiFi")]
    public void UnreadableProcessWithAppName_IsInUse(string name) =>
        Assert.True(InUse(new RunningProcess(7, name, null)));

    /// <summary>
    /// An install for one user updates as that user, so another user's unreadable DAQiFi is never
    /// closed by it and must not hold the update (#503 audit).
    /// </summary>
    [Fact]
    public void UnreadableProcessWithAppName_ForOneUserInstall_IsNotInUse() =>
        Assert.False(InstallUsage.IsInUseElsewhere(
            [new RunningProcess(7, "DAQiFi", null)], SELF, Root, Names, appliesForAllUsers: false));

    /// <summary>A readable process under the folder still counts for an install for one user.</summary>
    [Fact]
    public void ProcessUnderTheInstall_ForOneUserInstall_IsInUse() =>
        Assert.True(InstallUsage.IsInUseElsewhere(
            [new RunningProcess(7, "x", Under("current", "x.exe"))], SELF, Root, Names, appliesForAllUsers: false));

    [Fact]
    public void UnreadableProcessWithOtherName_IsNotInUse() =>
        Assert.False(InUse(new RunningProcess(4, "System", null), new RunningProcess(8, "svchost", null)));

    [Fact]
    public void UnreadableSelf_IsIgnored() =>
        Assert.False(InUse(new RunningProcess(SELF, "Daqifi.Avalonia.Desktop", null)));

    [Fact]
    public void RootGivenWithTrailingSeparator_MatchesTheSame() =>
        Assert.True(InstallUsage.IsInUseElsewhere(
            [new RunningProcess(7, "x", Under("current", "x.exe"))], SELF, Root + Path.DirectorySeparatorChar, Names, true));

    [Fact]
    public void MalformedPath_IsNotInUse() =>
        Assert.False(InUse(new RunningProcess(7, "x", "\0bad")));
}
