using Daqifi.Desktop.Common.Loggers;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Unattended test runs (DAQIFI_TEST_MODE=1) must not report to the production project with the
/// built-in DSN. HeadlessBench on CI sent 364 deliberate failures there, one "user" per run
/// (Sentry DAQIFI-AVALONIA-J).
/// </summary>
public class SentryDsnChoiceTests
{
    private const string BuiltIn = "https://built-in@example.invalid/1";
    private const string Explicit = "https://explicit@example.invalid/2";

    [Fact]
    public void NormalRun_UsesTheBuiltInDsn() =>
        Assert.Equal(BuiltIn, AppLogger.ChooseSentryDsn(null, null, testMode: false, BuiltIn));

    [Fact]
    public void TestModeRun_DoesNotUseTheBuiltInDsn() =>
        Assert.Null(AppLogger.ChooseSentryDsn(null, null, testMode: true, BuiltIn));

    [Fact]
    public void TestModeRun_StillHonoursAnExplicitDsn()
    {
        Assert.Equal(Explicit, AppLogger.ChooseSentryDsn(null, Explicit, testMode: true, BuiltIn));
        Assert.Equal(Explicit, AppLogger.ChooseSentryDsn(Explicit, null, testMode: true, BuiltIn));
    }

    [Fact]
    public void ConfiguredValue_WinsOverTheEnvironment() =>
        Assert.Equal("cfg", AppLogger.ChooseSentryDsn("cfg", Explicit, testMode: false, BuiltIn));
}
