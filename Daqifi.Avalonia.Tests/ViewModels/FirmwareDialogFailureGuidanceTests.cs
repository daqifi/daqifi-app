using System.Text.RegularExpressions;
using System.Xml.Linq;
using Daqifi.Avalonia.Tests.Device.Firmware;
using Daqifi.Core.Firmware;
using Daqifi.Desktop.Device.Firmware;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Issue #672: a failed flash from the bootloader dialog showed one fixed sentence, "Sorry, something
/// went wrong uploading the firmware. :(", for every failure, and logged every failure at Error (a
/// Sentry event). The device drawer's update follows two rules, and the dialog now follows both
/// through the same mapping (<see cref="FirmwareFailureClassifier.Report"/>):
/// <list type="number">
/// <item>A genuine failure shows a summary and Core's recovery guidance, which since Core 1.10.0 says
/// what the failed flash left on the board: not changed, a clean bootloader that is safe to re-flash,
/// or possibly half-flashed.</item>
/// <item>A flash that is programmed and verified, but whose device did not come back, is a success
/// with a power-cycle hint, logged at Warning (no Sentry event).</item>
/// </list>
/// The flash is a fake that throws the exception Core throws for each condition
/// (<see cref="CoreFlashFailures"/>). No test reaches a device.
/// <para>
/// The text is read from the VIEW (<see cref="ShownErrorText"/>, <see cref="ShownCompletionTexts"/>):
/// the markup says which member each line shows, and that member is then read from the view model.
/// Compiled bindings prove that a bound member exists, never that it is the member with the message.
/// Reading through the markup also lets these tests run against the dialog before the fix, where the
/// error line was a literal: there they fail on the text, not on a missing member.
/// </para>
/// </summary>
public sealed class FirmwareDialogFailureGuidanceTests : IDisposable
{
    private const string View = "Daqifi.Avalonia/Daqifi.Desktop/View/FirmwareDialog.axaml";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ScriptedUpdateService _updates = new();
    private readonly RecordingAppLogger _logger = new();
    private readonly string _hexFile = Path.Combine(Path.GetTempPath(), $"daqifi-672-{Guid.NewGuid():N}.hex");

    public FirmwareDialogFailureGuidanceTests()
    {
        File.WriteAllText(_hexFile, ":00000001FF\n");
    }

    public void Dispose()
    {
        File.Delete(_hexFile);
    }

    private FirmwareDialogViewModel CreateViewModel() =>
        new(hidDeviceName: "DAQiFi Bootloader",
            firmwareDownloadService: new NoReleaseDownloadService(),
            firmwareUpdateService: _updates,
            appLogger: _logger)
        {
            FirmwareFilePath = _hexFile,
        };

    private static async Task UploadAsync(FirmwareDialogViewModel viewModel) =>
        await viewModel.UploadFirmwareCommand.ExecuteAsync(null).WaitAsync(Timeout);

    #region Rule 1: a genuine failure shows Core's recovery guidance
    /// <summary>
    /// The issue's main result: the user must see what the failed flash left on the board. The text
    /// is the device drawer's text for the same exception, word for word.
    /// </summary>
    [Theory]
    [MemberData(nameof(CoreFlashFailures.NotInstalled), MemberType = typeof(CoreFlashFailures))]
    public async Task A_failed_flash_shows_Core_s_recovery_guidance(Pic32FlashCondition condition)
    {
        var failure = CoreFlashFailures.For(condition);
        _updates.Failure = failure.Exception;
        var viewModel = CreateViewModel();

        await UploadAsync(viewModel);

        Assert.Equal(failure.ExpectedText, ShownErrorText(viewModel));
        Assert.False(viewModel.IsUploadComplete);
    }

    /// <summary>A failure that Core did not describe has no guidance to show, so the fixed sentence stays.</summary>
    [Fact]
    public async Task A_failure_Core_did_not_describe_shows_the_fixed_sentence()
    {
        _updates.Failure = new InvalidOperationException("The HID device was removed.");
        var viewModel = CreateViewModel();

        await UploadAsync(viewModel);

        Assert.Equal("Sorry, something went wrong uploading the firmware.  :(", ShownErrorText(viewModel));
        Assert.False(viewModel.IsUploadComplete);
    }

    /// <summary>
    /// The guidance belongs to the upload that failed. A retry starts with no error line, so the user
    /// never reads the earlier board state as the state of the run in progress.
    /// </summary>
    [Fact]
    public async Task A_new_upload_starts_without_the_previous_guidance()
    {
        _updates.Failure = CoreFlashFailures.For(Pic32FlashCondition.PossiblyHalfFlashed).Exception;
        var viewModel = CreateViewModel();
        await UploadAsync(viewModel);
        Assert.NotNull(ShownErrorText(viewModel));

        _updates.Failure = null;
        _updates.ParkUntilCancelled = true;
        var retry = viewModel.UploadFirmwareCommand.ExecuteAsync(null);
        await _updates.Started.Task.WaitAsync(Timeout);

        Assert.Null(ShownErrorText(viewModel));

        viewModel.CancelUploadFirmwareCommand.Execute(null);
        await retry.WaitAsync(Timeout);
        Assert.Null(ShownErrorText(viewModel));
    }
    #endregion

    #region Rule 2: installed but did not come back is a success with a power-cycle hint
    /// <summary>
    /// The image is programmed and CRC-verified, and only the jump back to the application failed.
    /// The firmware is on the board: the dialog says so, and asks for a power-cycle. It does not tell
    /// the user to re-flash firmware that is already installed.
    /// </summary>
    [Fact]
    public async Task Installed_but_not_back_is_a_success_with_a_power_cycle_hint()
    {
        var failure = CoreFlashFailures.For(Pic32FlashCondition.ProgrammedNotConfirmed);
        _updates.Failure = failure.Exception;
        var viewModel = CreateViewModel();

        await UploadAsync(viewModel);

        Assert.True(viewModel.IsUploadComplete);
        Assert.Null(ShownErrorText(viewModel));
        Assert.Equal(["Upload Complete", failure.ExpectedText], ShownCompletionTexts(viewModel));
    }

    /// <summary>
    /// The control for the hint: an ordinary success shows "Upload Complete" alone. Without it, a hint
    /// line that is always visible would pass the test above.
    /// </summary>
    [Fact]
    public async Task A_clean_success_shows_no_power_cycle_hint()
    {
        var viewModel = CreateViewModel();

        await UploadAsync(viewModel);

        Assert.True(viewModel.IsUploadComplete);
        Assert.Null(ShownErrorText(viewModel));
        Assert.Equal(["Upload Complete"], ShownCompletionTexts(viewModel));
    }
    #endregion

    #region Both rules: the log level
    /// <summary>
    /// A genuine failure is an Error (a Sentry event) with Core's guidance beside it. An installed
    /// flash whose device did not come back is a Warning, which sends no Sentry event: the device
    /// drawer's update deliberately files none for a device that only needs a power-cycle, and the
    /// dialog filed one.
    /// </summary>
    [Theory]
    [MemberData(nameof(CoreFlashFailures.All), MemberType = typeof(CoreFlashFailures))]
    public async Task The_failure_is_logged_at_the_level_the_device_drawer_uses(Pic32FlashCondition condition)
    {
        var failure = CoreFlashFailures.For(condition);
        _updates.Failure = failure.Exception;
        var viewModel = CreateViewModel();

        await UploadAsync(viewModel);

        if (failure.ExpectedErrorLog is null)
        {
            Assert.Empty(_logger.Errors);
            Assert.Contains(
                _logger.Warnings,
                warning => warning.StartsWith("Firmware was installed and verified", StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal([failure.ExpectedErrorLog], _logger.Errors);
            Assert.Same(failure.Exception, Assert.Single(_logger.ErrorExceptions));
            Assert.Contains($"Firmware recovery guidance: {failure.Exception.RecoveryGuidance}", _logger.Warnings);
        }
    }
    #endregion

    #region Reading the view
    private static XElement Root() =>
        XDocument.Parse(BindingFacts.Source(View)).Root
        ?? throw new InvalidOperationException($"{View} has no root element.");

    /// <summary>
    /// The text of the error line while it is visible, or null while it is hidden. The line is the
    /// TextBlock whose <c>IsVisible</c> follows <c>HasErrorOccured</c>.
    /// </summary>
    private static string? ShownErrorText(FirmwareDialogViewModel viewModel)
    {
        var line = Root().Descendants().Single(element =>
            element.Name.LocalName == "TextBlock"
            && element.Attribute("IsVisible")?.Value == "{Binding HasErrorOccured}");

        return Evaluate(line.Attribute("IsVisible")!.Value, viewModel) is true
            ? (string?)Evaluate(line.Attribute("Text")?.Value, viewModel)
            : null;
    }

    /// <summary>
    /// The lines the completed panel shows: each TextBlock in the panel gated by
    /// <c>IsUploadComplete</c> whose own <c>IsVisible</c>, if it has one, is true.
    /// </summary>
    private static IReadOnlyList<string?> ShownCompletionTexts(FirmwareDialogViewModel viewModel)
    {
        var panel = Root().Elements().Single(element => element.Name.LocalName == "Grid")
            .Elements().Single(element => element.Attribute("IsVisible")?.Value == "{Binding IsUploadComplete}");

        return panel.Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .Where(element => element.Attribute("IsVisible") is not { } gate || Evaluate(gate.Value, viewModel) is true)
            .Select(element => (string?)Evaluate(element.Attribute("Text")?.Value, viewModel))
            .ToList();
    }

    private static readonly Regex SimpleBinding = new(@"^\{Binding (!?)(\w+)\}$");

    /// <summary>
    /// A literal attribute value as written, or the value of a <c>{Binding Member}</c> or
    /// <c>{Binding !Member}</c> on the view model. Any other markup extension throws: these tests
    /// must not guess what a converter or a longer path would show.
    /// </summary>
    private static object? Evaluate(string? attribute, FirmwareDialogViewModel viewModel)
    {
        if (attribute is null || !attribute.StartsWith('{'))
        {
            return attribute;
        }

        var match = SimpleBinding.Match(attribute);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"{View}: '{attribute}' is not a binding these tests can read. Extend Evaluate for it.");
        }

        var property = typeof(FirmwareDialogViewModel).GetProperty(match.Groups[2].Value)
            ?? throw new InvalidOperationException(
                $"{View}: '{attribute}' names no public member of {nameof(FirmwareDialogViewModel)}.");
        var value = property.GetValue(viewModel);
        return match.Groups[1].Value == "!" ? !(bool)value! : value;
    }
    #endregion

    #region Fakes
    /// <summary>
    /// A PIC32 flash that throws <see cref="Failure"/>, succeeds when it is null, or with
    /// <see cref="ParkUntilCancelled"/> runs until it is cancelled.
    /// </summary>
    private sealed class ScriptedUpdateService : IFirmwareUpdateService
    {
        public Exception? Failure { get; set; }

        public bool ParkUntilCancelled { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FirmwareUpdateState CurrentState => FirmwareUpdateState.Idle;

#pragma warning disable CS0067 // Part of the interface; nothing in these tests raises it.
        public event EventHandler<FirmwareUpdateStateChangedEventArgs>? StateChanged;
#pragma warning restore CS0067

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default) => Flash(cancellationToken);

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress,
            string? targetDevicePath,
            string? targetLocationKey,
            CancellationToken cancellationToken = default) => Flash(cancellationToken);

        public Task UpdateWifiModuleAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string firmwarePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default,
            bool skipVersionCheck = false) => throw new NotSupportedException("The bootloader dialog never flashes the WiFi module.");

        public Task<WifiFirmwareStatus> CheckWifiFirmwareStatusAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private Task Flash(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            if (Failure is not null)
            {
                return Task.FromException(Failure);
            }

            return ParkUntilCancelled
                ? Task.Delay(System.Threading.Timeout.Infinite, cancellationToken)
                : Task.CompletedTask;
        }
    }

    /// <summary>A release feed with no release, so the dialog's option load finishes at once.</summary>
    private sealed class NoReleaseDownloadService : IFirmwareDownloadService
    {
        public Task<string?> DownloadLatestFirmwareAsync(
            string destinationDirectory,
            bool includePreRelease = false,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException("These uploads flash a browsed file.");

        public Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> DownloadFirmwareByTagAsync(
            string tagName,
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(
            bool includePreRelease = false,
            CancellationToken cancellationToken = default) => Task.FromResult<FirmwareReleaseInfo?>(null);

        public Task<FirmwareReleaseInfo?> GetLatestWifiReleaseAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(
            string deviceVersionString,
            bool includePreRelease = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void InvalidateCache() { }
    }
    #endregion
}
