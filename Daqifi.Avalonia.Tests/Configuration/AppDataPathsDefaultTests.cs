using Daqifi.Desktop.Common;
using Xunit;

namespace Daqifi.Avalonia.Tests.Configuration;

/// <summary>
/// The default data folder is per-user whether or not the process is elevated (#353). Upstream
/// moved elevated runs to <c>%ProgramData%</c>, where the release WPF app keeps its database, and
/// an install for the whole PC runs un-elevated, so following elevation would split one person's
/// data across two folders.
/// </summary>
public class AppDataPathsDefaultTests
{
    [Fact]
    public void The_default_is_DAQiFi_under_the_per_user_folder()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "DAQiFi");

        Assert.Equal(expected, AppDataPaths.DefaultDataDirectory());
    }

    /// <summary>
    /// #351: on Linux, .NET's GetFolderPath returns "" for a folder that does not exist unless it is
    /// told not to verify it, and a new account has no <c>~/.local/share</c> yet. The default was then
    /// the relative path <c>DAQiFi</c>, and the AppImage stopped at startup because SQLite could not
    /// open a database under it. The stand-in below behaves the way .NET does on that account.
    /// </summary>
    [Fact]
    public void The_default_is_an_absolute_path_before_the_per_user_folder_exists()
    {
        const string notCreatedYet = "/home/new-user/.local/share";
        static string NewLinuxAccount(Environment.SpecialFolder folder, Environment.SpecialFolderOption option) =>
            folder == Environment.SpecialFolder.LocalApplicationData
            && option == Environment.SpecialFolderOption.DoNotVerify
                ? notCreatedYet
                : string.Empty;

        var directory = AppDataPaths.DefaultDataDirectory(NewLinuxAccount);

        Assert.True(Path.IsPathRooted(directory), $"The default data folder '{directory}' is a relative path.");
        Assert.Equal(Path.Combine(notCreatedYet, "DAQiFi"), directory);
    }

    private const string XdgOnlyOnLinux =
        "XDG_DATA_HOME moves LocalApplicationData only on Linux; Windows and macOS take it from the account.";

    /// <summary>
    /// The same defect through the real .NET call, not a stand-in. <c>XDG_DATA_HOME</c> is pointed at
    /// a folder that does not exist, the way <c>~/.local/share</c> is missing on a new account. The
    /// first assertion is the premise: the call the code made before #351 returns "" there, so the
    /// default was the relative path <c>DAQiFi</c> and this row was red. The second is the fix.
    /// </summary>
    /// <remarks>
    /// The variable is set only around the two calls and restored in <c>finally</c>. Nothing else in
    /// this assembly reads <c>LocalApplicationData</c> (<c>AppDataPaths</c> resolved its own folder
    /// from the <c>DAQIFI_DATA_DIR</c> override at module start), and xunit runs the rows of one class
    /// one at a time.
    /// </remarks>
    [LinuxOnlyFact(XdgOnlyOnLinux)]
    public void On_Linux_the_default_is_absolute_when_the_data_home_does_not_exist_yet()
    {
        Assert.True(OperatingSystem.IsLinux(), XdgOnlyOnLinux);
        var missing = Path.Combine(Path.GetTempPath(), "daqifi-no-data-home-" + Guid.NewGuid().ToString("N"));
        var saved = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string beforeTheFix;
        string directory;
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", missing);
            beforeTheFix = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            directory = AppDataPaths.DefaultDataDirectory();
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", saved);
        }

        Assert.Equal(string.Empty, beforeTheFix);
        Assert.Equal(Path.Combine(missing, "DAQiFi"), directory);
        Assert.False(Directory.Exists(missing), "Resolving the default must not create any folder.");
    }

    [Fact]
    public void The_default_is_never_the_machine_wide_folder()
    {
        var machineWide = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        Assert.False(
            !string.IsNullOrEmpty(machineWide)
            && AppDataPaths.DefaultDataDirectory().StartsWith(machineWide, StringComparison.OrdinalIgnoreCase),
            "The default data folder must not follow elevation into the machine-wide folder.");
    }
}
