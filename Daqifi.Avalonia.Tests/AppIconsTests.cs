using Daqifi.Avalonia.Services;
using Optris.Icons.Avalonia;
using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// Any host that builds the app must get the icon set. MainWindow threw "No IIconProvider with
/// prefix matching mdi-cog" when a host's entry point had not registered it (Sentry
/// DAQIFI-AVALONIA-D). Registering twice throws, so the helper must tolerate either order.
/// </summary>
public class AppIconsTests
{
    [Fact]
    public void AfterEnsureRegistered_TheSettingsIconResolves()
    {
        AppIcons.EnsureRegistered();

        Assert.NotNull(IconProvider.Current.GetIcon("mdi-cog"));
    }

    [Fact]
    public void CallingItAgain_DoesNotThrow()
    {
        AppIcons.EnsureRegistered();

        Assert.Null(Record.Exception(AppIcons.EnsureRegistered));
    }
}
