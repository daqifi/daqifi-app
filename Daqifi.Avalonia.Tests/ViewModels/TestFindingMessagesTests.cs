using Daqifi.Desktop;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>Two messages found unclear on a real install of the MSI (#532, #533).</summary>
public class TestFindingMessagesTests
{
    [Theory]
    [InlineData("3.3.0-beta.39", "DAQiFi v3.3.0-beta.39")]
    [InlineData("3.3.0", "DAQiFi v3.3.0")]
    public void WindowTitle_KeepsThePrereleaseLabel(string semantic, string expected) =>
        Assert.Equal(expected, MainWindow.WindowTitle(semantic, new Version(3, 3, 0, 0)));

    [Fact]
    public void WindowTitle_WithoutAnInformationalVersion_FallsBackToTheAssemblyVersion()
    {
        Assert.Equal("DAQiFi v3.3.0", MainWindow.WindowTitle(null, new Version(3, 3, 0, 0)));
        Assert.Equal("DAQiFi", MainWindow.WindowTitle(null, null));
    }

    [Theory]
    [InlineData(138_766_995_456L, "129 GB")]   // the database the MSI test met
    [InlineData(3_221_225_472L, "3 GB")]
    [InlineData(1_610_612_736L, "1.5 GB")]
    [InlineData(5_000_000L, "5 MB")]
    [InlineData(10L, "1 MB")]
    public void NotEnoughSpace_SaysHowMuch(long bytes, string size)
    {
        var message = DaqifiViewModel.NotEnoughSpaceNotice(bytes);
        Assert.Equal($"Not enough disk space to import: it needs about {size}.", message);
        Assert.True(message.Length <= 90, message);
    }
}
