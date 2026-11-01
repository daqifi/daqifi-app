using Daqifi.Desktop.View;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// The decisions behind the startup splash (#501). Showing it and its first paint need a real
/// windowing platform, which this project does not have, so those are checked by eye on Windows.
/// </summary>
public class SplashWindowTests
{
    [Theory]
    [InlineData(true, false, true)]    // a normal Windows launch
    [InlineData(true, true, false)]    // test mode: no topmost window over a capture
    [InlineData(false, false, false)]  // upstream's splash was Windows-only
    [InlineData(false, true, false)]
    public void ShouldShowSplash_OnlyOnWindowsOutsideTestMode(bool isWindows, bool isTestMode, bool expected)
    {
        Assert.Equal(expected, Daqifi.Desktop.App.ShouldShowSplash(isWindows, isTestMode));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(SplashWindow.FADE_MS / 2, 0.5)]
    [InlineData(SplashWindow.FADE_MS, 0)]
    [InlineData(SplashWindow.FADE_MS * 2, 0)]  // a late timer tick must not go negative
    [InlineData(-1, 1)]
    public void FadeOpacity_FallsFromOneToZeroOverTheFade(double elapsedMs, double expected)
    {
        Assert.Equal(expected, SplashWindow.FadeOpacity(elapsedMs), 3);
    }
}
