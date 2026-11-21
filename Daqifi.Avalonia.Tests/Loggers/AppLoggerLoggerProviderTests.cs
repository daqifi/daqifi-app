using System.Reflection;
using Daqifi.Core.Device;
using Daqifi.Core.Firmware;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Loggers;
using Microsoft.Extensions.Logging;
using Xunit;
using BreadcrumbLevel = Daqifi.Desktop.Common.Loggers.BreadcrumbLevel; // not Sentry's, which a global using also brings in
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Pins what <see cref="AppLoggerLoggerProvider"/> does with each log entry that Daqifi.Core writes
/// through <c>Microsoft.Extensions.Logging</c>. Before this file, no test referred to the provider.
///
/// <para><b>Why this class matters.</b> It is the only path from Core's logs to the app's log
/// file and to Sentry. It makes three decisions that nothing else checks, and a wrong decision is
/// silent in each case:</para>
/// <list type="bullet">
///   <item><description><b>Which levels go to the file.</b> Information and above go. Debug and
///   Trace do not, because Core logs each attempt at Debug.</description></item>
///   <item><description><b>Which errors go to Sentry.</b> An Error or Critical entry from a
///   <c>Daqifi</c> category goes to <c>IAppLogger.Error</c>, which captures a Sentry event. An
///   entry from any other category goes to <c>IAppLogger.Warning</c>, which writes the file and
///   captures no event. Core's firmware-update category is the exception to the first rule:
///   <c>FirmwareUpdateCoordinator</c> decides the Sentry severity of a firmware outcome, so the
///   bridge sends that category to the file only.</description></item>
///   <item><description><b>A logging failure never reaches the caller.</b> The caller can be
///   Core's firmware update, part way through a flash.</description></item>
/// </list>
///
/// <para><b>How the tests see the output.</b> The provider takes no logger argument. It reads
/// <see cref="AppLogger.Instance"/> into a private field, and under a test runner that logger has
/// no NLog target. So each test replaces the field with a <see cref="BridgeTarget"/> that records
/// each call, with its method, exception and message.
/// <see cref="A_new_provider_forwards_to_the_apps_own_logger"/> pins that the field holds the
/// app's logger before the test replaces it, so the tests examine the real target and not a
/// fake that production never uses.</para>
///
/// <para><b>What these tests do not cover.</b> <c>App.cs</c> adds a second filter in front of
/// the provider when it builds the service collection. That filter is an inline lambda, so a test
/// can only copy it, not run it. In production, it stops non-<c>Daqifi</c> entries below Error
/// before they get to the bridge.</para>
/// </summary>
public sealed class AppLoggerLoggerProviderTests
{
    private const string Message = "the entry text";

    /// <summary>The category <c>ILogger&lt;FirmwareUpdateService&gt;</c> has in the pinned Core.</summary>
    private static readonly string FirmwareCategory = typeof(FirmwareUpdateService).FullName!;

    /// <summary>A Core category that is not firmware update.</summary>
    private static readonly string CoreDeviceCategory = typeof(DaqifiDevice).FullName!;

    /// <summary>A framework category, of the kind EF Core writes.</summary>
    private const string FrameworkCategory = "Microsoft.EntityFrameworkCore.Database.Command";

    #region The target

    /// <summary>
    /// The provider must write to the app's own logger. If it does not, every other test in this
    /// file examines a target that production never uses.
    /// </summary>
    [Fact]
    public void A_new_provider_forwards_to_the_apps_own_logger()
    {
        var provider = new AppLoggerLoggerProvider();

        Assert.Same(AppLogger.Instance, TargetField.GetValue(provider));
    }

    #endregion

    #region Which levels go to the file

    [Theory]
    [InlineData(LogLevel.Trace, false)]
    [InlineData(LogLevel.Debug, false)]
    [InlineData(LogLevel.Information, true)]
    [InlineData(LogLevel.Warning, true)]
    [InlineData(LogLevel.Error, true)]
    [InlineData(LogLevel.Critical, true)]
    [InlineData(LogLevel.None, false)]
    public void Information_and_above_are_enabled_and_the_None_sentinel_is_not(LogLevel level, bool enabled)
    {
        var (logger, _) = Bridge(CoreDeviceCategory);

        Assert.Equal(enabled, logger.IsEnabled(level));
    }

    /// <summary>
    /// <c>Log</c> applies the filter itself. A caller that does not ask <c>IsEnabled</c> first
    /// still gets nothing written for these levels. <c>None</c> has the integer value 6, which is
    /// above Information, so only the upper bound keeps it out.
    /// </summary>
    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.None)]
    public void An_entry_below_Information_or_at_None_writes_nothing(LogLevel level)
    {
        var (logger, target) = Bridge(CoreDeviceCategory);

        logger.Log(level, Message);

        Assert.Empty(target.Entries);
    }

    [Fact]
    public void An_Information_entry_is_written_with_its_full_category()
    {
        var (logger, target) = Bridge(CoreDeviceCategory);

        logger.LogInformation(Message);

        var entry = Assert.Single(target.Entries);
        Assert.Equal(new Entry("Information", null, $"[{CoreDeviceCategory}] {Message}"), entry);
    }

    /// <summary>
    /// <c>IAppLogger</c> has no Information overload that takes an exception, so the bridge
    /// appends the exception text. Without that, a stack trace that Core logs at Information is
    /// lost.
    /// </summary>
    [Fact]
    public void An_Information_entry_keeps_its_exception_text()
    {
        var (logger, target) = Bridge(CoreDeviceCategory);
        var exception = Thrown(new InvalidOperationException("retry failed"));

        logger.LogInformation(exception, Message);

        var entry = Assert.Single(target.Entries);
        Assert.Equal("Information", entry.Method);
        Assert.Equal($"[{CoreDeviceCategory}] {Message}{Environment.NewLine}{exception}", entry.Message);
    }

    [Fact]
    public void A_Warning_entry_is_written_as_a_warning_with_no_level_tag()
    {
        var (logger, target) = Bridge(CoreDeviceCategory);

        logger.LogWarning(Message);

        Assert.Equal(new Entry("Warning", null, $"[{CoreDeviceCategory}] {Message}"), Assert.Single(target.Entries));
    }

    [Fact]
    public void A_Warning_entry_keeps_its_exception()
    {
        var (logger, target) = Bridge(CoreDeviceCategory);
        var exception = Thrown(new IOException("port closed"));

        logger.LogWarning(exception, Message);

        Assert.Equal(new Entry("Warning", exception, $"[{CoreDeviceCategory}] {Message}"), Assert.Single(target.Entries));
    }

    #endregion

    #region Which errors go to Sentry

    /// <summary>
    /// A Daqifi error goes to <c>IAppLogger.Error</c>, which captures a Sentry event. Critical is
    /// routed the same way as Error.
    /// </summary>
    [Theory]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Critical)]
    public void A_Daqifi_error_goes_to_Error_with_its_exception(LogLevel level)
    {
        var (logger, target) = Bridge(CoreDeviceCategory);
        var exception = Thrown(new TimeoutException("no reply"));

        logger.Log(level, exception, Message);

        Assert.Equal(new Entry("Error", exception, $"[{CoreDeviceCategory}] {Message}"), Assert.Single(target.Entries));
    }

    [Theory]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Critical)]
    public void A_Daqifi_error_with_no_exception_goes_to_Error(LogLevel level)
    {
        var (logger, target) = Bridge(CoreDeviceCategory);

        logger.Log(level, Message);

        Assert.Equal(new Entry("Error", null, $"[{CoreDeviceCategory}] {Message}"), Assert.Single(target.Entries));
    }

    /// <summary>
    /// The app's own categories start with <c>Daqifi.Desktop</c>, and the comparison ignores
    /// case.
    /// </summary>
    [Theory]
    [InlineData("Daqifi.Desktop.Device.SerialDevice")]
    [InlineData("DAQIFI.Core.Device.Something")]
    [InlineData("daqifi.core.communication.Transport")]
    public void Any_category_that_starts_with_Daqifi_in_any_case_goes_to_Error(string category)
    {
        var (logger, target) = Bridge(category);

        logger.LogError(Message);

        Assert.Equal("Error", Assert.Single(target.Entries).Method);
    }

    /// <summary>
    /// A framework error is kept in the file with its stack trace, but it does not become a
    /// Sentry event. The level tag keeps the severity that the Warning call would lose.
    /// </summary>
    [Theory]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Critical)]
    public void A_framework_error_goes_to_Warning_with_its_level_tag_and_exception(LogLevel level)
    {
        var (logger, target) = Bridge(FrameworkCategory);
        var exception = Thrown(new InvalidOperationException("command failed"));

        logger.Log(level, exception, Message);

        Assert.Equal(
            new Entry("Warning", exception, $"[{level}] [{FrameworkCategory}] {Message}"),
            Assert.Single(target.Entries));
    }

    [Fact]
    public void A_framework_error_with_no_exception_goes_to_Warning()
    {
        var (logger, target) = Bridge(FrameworkCategory);

        logger.LogError(Message);

        Assert.Equal(
            new Entry("Warning", null, $"[Error] [{FrameworkCategory}] {Message}"),
            Assert.Single(target.Entries));
    }

    /// <summary>
    /// The test is "starts with", not "contains". A category that only contains <c>Daqifi</c> is
    /// a framework category.
    /// </summary>
    [Fact]
    public void A_category_that_only_contains_Daqifi_is_a_framework_category()
    {
        var (logger, target) = Bridge("Vendor.Daqifi.Plugin");

        logger.LogError(Message);

        Assert.Equal("Warning", Assert.Single(target.Entries).Method);
    }

    /// <summary>
    /// <c>FirmwareUpdateCoordinator</c> decides the Sentry severity of a firmware outcome, and
    /// captures a real failure itself. If the bridge also sent Core's own Error, Sentry would get
    /// each failure twice, and an expected outcome (a reconnect timeout after a good flash) that
    /// the coordinator reports at a lower severity would come back as an error.
    /// </summary>
    [Theory]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Critical)]
    public void A_Core_firmware_update_error_goes_to_Warning_not_Error(LogLevel level)
    {
        var (logger, target) = Bridge(FirmwareCategory);
        var exception = Thrown(new TimeoutException("device did not reconnect"));

        logger.Log(level, exception, Message);

        Assert.Equal(
            new Entry("Warning", exception, $"[{level}] [{FirmwareCategory}] {Message}"),
            Assert.Single(target.Entries));
    }

    [Fact]
    public void The_firmware_update_exclusion_ignores_case()
    {
        var (logger, target) = Bridge("daqifi.core.firmware.WifiModuleUpdater");

        logger.LogError(Message);

        Assert.Equal("Warning", Assert.Single(target.Entries).Method);
    }

    /// <summary>
    /// The exclusion changes only where errors go. Core's firmware diagnostics at Information,
    /// which include the WiFi flash tool output, still go to the file.
    /// </summary>
    [Fact]
    public void A_Core_firmware_update_Information_entry_is_still_written()
    {
        var (logger, target) = Bridge(FirmwareCategory);

        logger.LogInformation(Message);

        Assert.Equal(new Entry("Information", null, $"[{FirmwareCategory}] {Message}"), Assert.Single(target.Entries));
    }

    #endregion

    #region Category and message edge cases

    /// <summary>
    /// A blank category becomes <c>Unknown</c>, so the log line still has a tag and logger
    /// creation does not throw.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_category_is_tagged_Unknown(string? category)
    {
        var (logger, target) = Bridge(category!);

        logger.LogInformation(Message);

        Assert.Equal(new Entry("Information", null, $"[Unknown] {Message}"), Assert.Single(target.Entries));
    }

    /// <summary><c>Unknown</c> does not start with <c>Daqifi</c>, so its errors go to Warning.</summary>
    [Fact]
    public void An_error_from_a_blank_category_does_not_go_to_Sentry()
    {
        var (logger, target) = Bridge(null!);

        logger.LogError(Message);

        Assert.Equal(new Entry("Warning", null, $"[Error] [Unknown] {Message}"), Assert.Single(target.Entries));
    }

    [Fact]
    public void An_empty_message_with_no_exception_writes_nothing()
    {
        var (logger, target) = Bridge(CoreDeviceCategory);

        logger.Log(LogLevel.Warning, default, "state", null, (_, _) => string.Empty);

        Assert.Empty(target.Entries);
    }

    /// <summary>The exception is the useful part of the entry, so it is written.</summary>
    [Fact]
    public void An_empty_message_with_an_exception_is_still_written()
    {
        var (logger, target) = Bridge(CoreDeviceCategory);
        var exception = Thrown(new IOException("read failed"));

        logger.Log(LogLevel.Warning, default, "state", exception, (_, _) => string.Empty);

        Assert.Equal(new Entry("Warning", exception, $"[{CoreDeviceCategory}] "), Assert.Single(target.Entries));
    }

    [Fact]
    public void A_null_formatter_writes_nothing_and_does_not_throw()
    {
        var (logger, target) = Bridge(CoreDeviceCategory);

        logger.Log<string>(LogLevel.Error, default, "state", null, null!);

        Assert.Empty(target.Entries);
    }

    [Fact]
    public void BeginScope_returns_a_scope_that_can_be_disposed()
    {
        var (logger, _) = Bridge(CoreDeviceCategory);

        var scope = logger.BeginScope("scope state");

        Assert.NotNull(scope);
        scope.Dispose();
    }

    #endregion

    #region A logging failure never reaches the caller

    /// <summary>
    /// A formatter that throws does not get to the caller. The bridge writes one warning that
    /// names the failure and keeps its exception.
    /// </summary>
    [Fact]
    public void A_throwing_formatter_is_reported_as_a_warning_and_not_thrown()
    {
        var (logger, target) = Bridge(CoreDeviceCategory);
        var failure = new FormatException("formatter broke");

        logger.Log(LogLevel.Error, default, "state", null, (_, _) => throw failure);

        Assert.Equal(
            new Entry("Warning", failure, $"[{CoreDeviceCategory}] AppLogger logging bridge failed to forward an entry."),
            Assert.Single(target.Entries));
    }

    /// <summary>
    /// When the logger itself throws, the bridge tries to report that once, and then stops. The
    /// second throw does not get to the caller either.
    /// </summary>
    [Fact]
    public void A_throwing_logger_does_not_throw_into_the_caller()
    {
        var (logger, target) = Bridge(CoreDeviceCategory, new BridgeTarget { ThrowOnEveryCall = true });

        logger.LogError(Message);

        Assert.Collection(
            target.Entries,
            first => Assert.Equal(new Entry("Error", null, $"[{CoreDeviceCategory}] {Message}"), first),
            second =>
            {
                Assert.Equal("Warning", second.Method);
                Assert.Equal($"[{CoreDeviceCategory}] AppLogger logging bridge failed to forward an entry.", second.Message);
                Assert.Equal("sink is down", second.Exception?.Message);
            });
    }

    #endregion

    #region Through the real Microsoft.Extensions.Logging pipeline

    /// <summary>
    /// Core gets its logger as <c>ILogger&lt;FirmwareUpdateService&gt;</c>, and the logger factory
    /// makes the category from the type name. This pins that the category the factory makes in
    /// the pinned Core still starts with the prefix the bridge looks for. If Core moves the type
    /// to a different namespace, this test fails.
    /// </summary>
    [Fact]
    public void Cores_firmware_update_logger_from_the_factory_writes_errors_to_the_file_only()
    {
        var target = new BridgeTarget();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(Provider(target)));
        var exception = Thrown(new TimeoutException("JumpingToApp timed out"));

        factory.CreateLogger<FirmwareUpdateService>().LogError(exception, Message);

        Assert.StartsWith("Daqifi.Core.Firmware", FirmwareCategory, StringComparison.Ordinal);
        Assert.Equal(
            new Entry("Warning", exception, $"[Error] [{FirmwareCategory}] {Message}"),
            Assert.Single(target.Entries));
    }

    [Fact]
    public void Cores_device_logger_from_the_factory_sends_errors_to_Error()
    {
        var target = new BridgeTarget();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(Provider(target)));
        var exception = Thrown(new IOException("transport lost"));

        factory.CreateLogger<DaqifiDevice>().LogError(exception, Message);

        Assert.Equal(new Entry("Error", exception, $"[{CoreDeviceCategory}] {Message}"), Assert.Single(target.Entries));
    }

    /// <summary>
    /// The logger factory collects an exception from any provider and throws it to the caller
    /// as an <see cref="AggregateException"/>. So the bridge's own catch is the only thing that
    /// keeps a formatter failure out of Core's code.
    /// </summary>
    [Fact]
    public void A_throwing_formatter_does_not_reach_Cores_call_site()
    {
        var target = new BridgeTarget();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(Provider(target)));
        var logger = factory.CreateLogger<FirmwareUpdateService>();

        var thrown = Record.Exception(() =>
            logger.Log(LogLevel.Error, default, "state", null, (_, _) => throw new FormatException("formatter broke")));

        Assert.Null(thrown);
        Assert.Equal("Warning", Assert.Single(target.Entries).Method);
    }

    #endregion

    #region Helpers

    private static readonly FieldInfo TargetField =
        typeof(AppLoggerLoggerProvider).GetField("_appLogger", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "AppLoggerLoggerProvider no longer has an _appLogger field. Update these tests to reach its target.");

    private static AppLoggerLoggerProvider Provider(BridgeTarget target)
    {
        var provider = new AppLoggerLoggerProvider();
        TargetField.SetValue(provider, target);
        return provider;
    }

    private static (ILogger Logger, BridgeTarget Target) Bridge(string category, BridgeTarget? target = null)
    {
        target ??= new BridgeTarget();
        return (Provider(target).CreateLogger(category), target);
    }

    /// <summary>Gives the exception a stack trace, as a real one has, so its text is not trivial.</summary>
    private static T Thrown<T>(T exception) where T : Exception
    {
        try
        {
            throw exception;
        }
        catch (T caught)
        {
            return caught;
        }
    }

    /// <summary>One call the bridge made on its <see cref="IAppLogger"/>.</summary>
    private sealed record Entry(string Method, Exception? Exception, string Message);

    /// <summary>
    /// Records each <see cref="IAppLogger"/> call with its method, exception and message.
    /// <see cref="RecordingAppLogger"/> is not used because it drops the exception of
    /// <c>Warning(Exception, string)</c>, and the exception is part of what the bridge decides.
    /// </summary>
    private sealed class BridgeTarget : IAppLogger
    {
        private readonly Lock _gate = new();
        private readonly List<Entry> _entries = [];

        /// <summary>Records the call and then throws, as a broken logging sink does.</summary>
        internal bool ThrowOnEveryCall { get; init; }

        internal IReadOnlyList<Entry> Entries
        {
            get
            {
                lock (_gate) { return [.. _entries]; }
            }
        }

        public void Information(string message) => Record("Information", null, message);

        public void Warning(string message) => Record("Warning", null, message);

        public void Warning(Exception ex, string message) => Record("Warning", ex, message);

        public void Error(string message) => Record("Error", null, message);

        public void Error(Exception ex, string message) => Record("Error", ex, message);

        public void AddBreadcrumb(string category, string message, BreadcrumbLevel level = BreadcrumbLevel.Info) { }

        public void SetDeviceContext(string model, string serialNumber, string firmwareVersion, string connectionType, int activeChannels) { }

        public void ClearDeviceContext() { }

        public void Shutdown() { }

        private void Record(string method, Exception? exception, string message)
        {
            lock (_gate) { _entries.Add(new Entry(method, exception, message)); }

            if (ThrowOnEveryCall)
            {
                throw new InvalidOperationException("sink is down");
            }
        }
    }

    #endregion
}
