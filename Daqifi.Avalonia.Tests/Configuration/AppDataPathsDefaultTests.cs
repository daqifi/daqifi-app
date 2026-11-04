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
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DAQiFi");

        Assert.Equal(expected, AppDataPaths.DefaultDataDirectory());
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
