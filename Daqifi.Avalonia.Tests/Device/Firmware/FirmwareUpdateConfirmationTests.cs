using System.Collections.ObjectModel;
using Daqifi.Core.Device;
using Daqifi.Core.Firmware;
using Daqifi.Desktop.Device.Firmware;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.Models;
using Daqifi.Avalonia.Tests.ViewModels;
using Daqifi.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DesktopStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.Device.Firmware;

/// <summary>
/// Pins issue #564: one click on UPDATE in the device drawer started a firmware write, with no
/// prompt, and nothing told the user that the update can erase the device's WiFi settings.
/// <para>
/// The coordinator now asks the host to confirm after its guards and before anything touches the
/// device, and adds a "set the WiFi settings again" notification after a successful write. The
/// device here is the app's real <see cref="SerialStreamingDevice"/> over a connected Core device on
/// a <see cref="CapturingTransport"/>, so a confirmed run really reaches
/// <see cref="IFirmwareUpdateService.UpdateFirmwareAsync(Daqifi.Core.Device.IStreamingDevice,string,IProgress{FirmwareUpdateProgress}?,string?,string?,CancellationToken)"/>
/// — which is what makes "a cancel never reaches it" a fact rather than a run that stopped early
/// for some other reason. Every run is a manual (PIC32-only) upload, so no WiFi step is involved.
/// </para>
/// </summary>
public sealed class FirmwareUpdateConfirmationTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly PromptHost _host = new();
    private readonly CountingUpdateService _updates = new();
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "daqifi-firmware-confirm-tests", Guid.NewGuid().ToString("N"));
    private readonly CountingDownloadService _downloads;
    private readonly CapturingTransport _transport = new();
    private readonly DaqifiStreamingDevice _core;
    private readonly SerialStreamingDevice _device;

    public FirmwareUpdateConfirmationTests()
    {
        _downloads = new CountingDownloadService(Path.Combine(_dataDirectory, "downloads"));
        _core = new DaqifiStreamingDevice("core", _transport, NullLogger.Instance);
        _core.Connect();
        _device = new SerialStreamingDevice("COM-TEST-564", _core)
        {
            DeviceSerialNo = "SN564",
            DeviceVersion = "3.8.0",
            FriendlyName = "Bench Nq1",
        };
        _host.SelectedDevice = _device;
        _host.FirmwareFilePath = FirmwareFile();
    }

    public void Dispose()
    {
        _transport.CloseStream();
        _core.Dispose();
        _transport.Dispose();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    private FirmwareUpdateCoordinator CreateCoordinator() =>
        new(_host,
            _updates,
            _downloads,
            NullLogger<FirmwareUpdateService>.Instance,
            new RecordingAppLogger(),
            _dataDirectory,
            wifiUpdateModeSettleDelay: TimeSpan.Zero,
            canFlashWifiModule: false,
            // The version read after the write (issue #570) asks the device for a status reply. The
            // devices here never answer, so the real reader would wait its full timeout in every
            // run that writes. These facts are about the prompt and the write, not that read.
            readPostRestartVersion: (_, _) => Task.FromResult<string?>(null));

    #region The write starts only after the user confirms
    /// <summary>
    /// The ticket's first verification: a cancel never reaches the write and the uploading flag never
    /// goes up. "Untouched" is asserted as broadly as the host can see it — no status text, no WiFi
    /// probe quiesce, no download, no notification, and the user's file selection kept for a retry.
    /// </summary>
    [Fact]
    public async Task A_cancel_leaves_the_device_untouched()
    {
        _host.Answer = false;
        var selectedFile = _host.FirmwareFilePath;

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Equal(1, _host.PromptCount);
        Assert.Equal(0, _updates.Pic32FlashCount);
        Assert.Equal(0, _downloads.DownloadCount);
        Assert.Equal(0, _host.QuiesceCount);
        Assert.False(_host.IsFirmwareUploading);
        Assert.False(_host.FlagWasEverRaised);
        Assert.Empty(_host.StatusWrites);
        Assert.Empty(_host.Notifications);
        Assert.Equal(0, _host.SucceededCount);
        Assert.Equal(selectedFile, _host.FirmwareFilePath);
    }

    /// <summary>The ticket's second verification: a confirm starts the write, once.</summary>
    [Fact]
    public async Task A_confirm_starts_the_write_once()
    {
        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Equal(1, _host.PromptCount);
        Assert.Equal(1, _updates.Pic32FlashCount);
        Assert.True(_host.IsUploadComplete);
        Assert.Equal(1, _host.SucceededCount);
        Assert.False(_host.IsFirmwareUploading);
    }

    /// <summary>
    /// While the prompt is open nothing has started: the flag is down, so the drawer shows no Cancel
    /// for a run that does not exist, and the write waits for the answer.
    /// </summary>
    [Fact]
    public async Task Nothing_runs_while_the_prompt_is_open()
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.PendingAnswer = answer.Task;

        var run = CreateCoordinator().UploadFirmwareAsync();

        Assert.False(run.IsCompleted);
        Assert.Equal(1, _host.PromptCount);
        Assert.False(_host.FlagWasEverRaised);
        Assert.Empty(_host.StatusWrites);
        Assert.Equal(0, _updates.Pic32FlashCount);

        answer.SetResult(true);
        await run.WaitAsync(Timeout);

        Assert.Equal(1, _updates.Pic32FlashCount);
    }

    /// <summary>
    /// The prompt is awaited, so the drawer can be pointed at another device under it. A "yes" given
    /// for one device must not write the device that happens to be selected when it arrives — nor
    /// the one it was given for, which the drawer no longer shows.
    /// </summary>
    [Fact]
    public async Task A_confirm_for_a_device_that_is_no_longer_selected_writes_nothing()
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.PendingAnswer = answer.Task;

        var run = CreateCoordinator().UploadFirmwareAsync();
        _host.SelectedDevice = new SerialStreamingDevice("COM-TEST-OTHER");
        answer.SetResult(true);
        await run.WaitAsync(Timeout);

        Assert.Equal(0, _updates.Pic32FlashCount);
        Assert.False(_host.FlagWasEverRaised);
    }

    /// <summary>
    /// Another run that started while the prompt was open wins; the late "yes" adds nothing to it.
    /// </summary>
    [Fact]
    public async Task A_confirm_that_arrives_during_another_run_writes_nothing()
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.PendingAnswer = answer.Task;

        var run = CreateCoordinator().UploadFirmwareAsync();
        _host.IsFirmwareUploading = true;
        answer.SetResult(true);
        await run.WaitAsync(Timeout);

        Assert.Equal(0, _updates.Pic32FlashCount);
    }

    /// <summary>
    /// The prompt comes after the coordinator's guards: a device the update cannot run on gets no
    /// prompt, because confirming would then do nothing.
    /// </summary>
    [Fact]
    public async Task A_device_the_update_cannot_run_on_gets_no_prompt()
    {
        _host.SelectedDevice = null;

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Equal(0, _host.PromptCount);
    }

    /// <summary>A run already in progress is not interrupted by a prompt for a second one.</summary>
    [Fact]
    public async Task A_run_in_progress_gets_no_second_prompt()
    {
        _host.IsFirmwareUploading = true;

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Equal(0, _host.PromptCount);
        Assert.Equal(0, _updates.Pic32FlashCount);
    }
    #endregion

    #region What the prompt says
    /// <summary>
    /// The ticket's list: the device name, the installed and new versions, the duration, the cable
    /// warning and the erase warning — read off what the coordinator actually handed the host.
    /// </summary>
    [Fact]
    public async Task The_prompt_names_the_device_the_versions_and_the_risks()
    {
        _host.Answer = false;

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Equal("Update firmware?", _host.LastTitle);
        var message = _host.LastMessage;
        Assert.Contains("Device: Bench Nq1", message);
        Assert.Contains("Installed version: 3.8.0", message);
        Assert.Contains("New version: the file firmware.hex", message);
        Assert.Contains("approximately 1 minute", message);
        Assert.Contains("Do not disconnect the USB cable", message);
        Assert.Contains("can erase the WiFi settings and the calibration", message);
    }

    /// <summary>An auto-update names the release resolved for it.</summary>
    [Fact]
    public void An_auto_update_names_the_resolved_release()
    {
        var message = FirmwareUpdateCoordinator.DescribeFirmwareUpdate(_device, "3.9.0", manualFirmwarePath: string.Empty);

        Assert.Contains("Installed version: 3.8.0", message);
        Assert.Contains("New version: 3.9.0", message);
    }

    /// <summary>
    /// A device that has not reported its version gets "unknown" in words rather than an empty field.
    /// </summary>
    [Fact]
    public void An_unknown_installed_version_is_named_rather_than_left_blank()
    {
        var device = new SerialStreamingDevice("COM-TEST-564-BLANK");

        var message = FirmwareUpdateCoordinator.DescribeFirmwareUpdate(device, "3.9.0", string.Empty);

        Assert.Contains("Installed version: unknown", message);
    }
    #endregion

    #region The notification after the write
    /// <summary>
    /// A successful write leaves a notification, owned by the device, that tells the user to set the
    /// WiFi settings again — the success dialog closes and the status line hides when the run ends.
    /// </summary>
    [Fact]
    public async Task A_successful_write_tells_the_user_to_set_the_wifi_settings_again()
    {
        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        var notification = Assert.Single(_host.Notifications);
        Assert.Equal("SN564", notification.DeviceSerialNo);
        Assert.StartsWith("Bench Nq1:", notification.Message);
        Assert.Contains("Set the WiFi settings of the device again.", notification.Message);
        Assert.False(notification.IsFirmwareUpdate);
        Assert.Equal(1, _host.RefreshCount);
    }

    /// <summary>A failed write changed nothing the user must redo, so it adds no such notification.</summary>
    [Fact]
    public async Task A_failed_write_adds_no_wifi_notification()
    {
        _updates.Fail = true;

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Equal(1, _updates.Pic32FlashCount);
        Assert.Empty(_host.Notifications);
    }

    /// <summary>Two updates of one device leave one reminder, not two.</summary>
    [Fact]
    public async Task A_second_update_does_not_stack_a_second_notification()
    {
        var coordinator = CreateCoordinator();

        await coordinator.UploadFirmwareAsync().WaitAsync(Timeout);
        _host.FirmwareFilePath = FirmwareFile();
        await coordinator.UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Equal(2, _updates.Pic32FlashCount);
        Assert.Single(_host.Notifications);
    }
    #endregion

    #region The write is the firmware the prompt named
    /// <summary>
    /// Review finding 1 on PR #575, automatic path: release A is the latest when the prompt opens and
    /// release B is published before the user answers. The write must be A — the version the user
    /// agreed to — downloaded by its tag, never "whatever is latest now".
    /// </summary>
    [Fact]
    public async Task An_auto_update_writes_the_release_the_prompt_named_even_if_a_newer_one_appears()
    {
        _host.FirmwareFilePath = string.Empty;
        _downloads.Releases.Add(Release("3.9.0"));
        _downloads.Releases.Add(Release("3.9.1"));

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Contains("New version: 3.9.0", _host.LastMessage);
        Assert.Equal(["v3.9.0"], _downloads.DownloadedTags);
        Assert.Equal(0, _downloads.LatestFirmwareDownloadCount);
        Assert.Equal([_downloads.PathFor("v3.9.0")], _updates.FlashedPaths);
    }

    /// <summary>
    /// Review finding 1, manual path: the path is read once, before the prompt. A path that changes
    /// while the prompt is open does not redirect the write to a file the prompt never named.
    /// </summary>
    [Fact]
    public async Task A_manual_update_writes_the_file_the_prompt_named_even_if_the_path_changes()
    {
        var namedFile = _host.FirmwareFilePath;
        var otherFile = Path.Combine(_dataDirectory, "other.hex");
        File.WriteAllText(otherFile, ":00000001FF\n");
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.PendingAnswer = answer.Task;

        var run = CreateCoordinator().UploadFirmwareAsync();
        _host.FirmwareFilePath = otherFile;
        answer.SetResult(true);
        await run.WaitAsync(Timeout);

        Assert.Contains("New version: the file firmware.hex", _host.LastMessage);
        Assert.Equal([namedFile], _updates.FlashedPaths);
    }

    /// <summary>
    /// Review finding 1, no release: a prompt that cannot name the version must not start a write. No
    /// prompt opens, nothing is written, the pane is not left "uploading", and the user is told why.
    /// </summary>
    [Fact]
    public async Task No_release_means_no_prompt_and_no_write()
    {
        _host.FirmwareFilePath = string.Empty;

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        AssertStoppedBeforeThePrompt();
        Assert.Equal([FirmwareUpdateCoordinator.ReleaseNotFoundMessage], _host.Errors);
    }

    /// <summary>The same when the lookup itself fails, which is what no internet connection looks like.</summary>
    [Fact]
    public async Task A_failed_release_lookup_means_no_prompt_and_no_write()
    {
        _host.FirmwareFilePath = string.Empty;
        _downloads.LookupFailure = new HttpRequestException("no route to host");

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        AssertStoppedBeforeThePrompt();
        Assert.Equal([FirmwareUpdateCoordinator.ReleaseNotFoundMessage], _host.Errors);
    }

    /// <summary>
    /// A selected file that is gone is found before the prompt, so the user is not asked to confirm a
    /// write that would then fail.
    /// </summary>
    [Fact]
    public async Task A_missing_manual_file_means_no_prompt_and_no_write()
    {
        _host.FirmwareFilePath = Path.Combine(_dataDirectory, "deleted.hex");

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        AssertStoppedBeforeThePrompt();
        var error = Assert.Single(_host.Errors);
        Assert.Contains("deleted.hex", error);
    }

    private void AssertStoppedBeforeThePrompt()
    {
        Assert.Equal(0, _host.PromptCount);
        Assert.Equal(0, _updates.Pic32FlashCount);
        Assert.Equal(0, _downloads.DownloadCount);
        Assert.False(_host.FlagWasEverRaised);
        Assert.False(_host.IsFirmwareUploading);
        Assert.Empty(_host.StatusWrites);
    }
    #endregion

    #region The success dialog
    /// <summary>
    /// Review finding 2 on PR #575: the success dialog itself tells the user to set the WiFi settings
    /// again, in the same words as the notification, after the completion text.
    /// </summary>
    [Fact]
    public async Task The_success_dialog_tells_the_user_to_set_the_wifi_settings_again()
    {
        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        // The first paragraph is the version text of issue #570 (here the reader gives no version, so
        // "unknown"); the WiFi instruction of issue #564 follows after an empty line.
        var device = Assert.IsAssignableFrom<Daqifi.Desktop.Device.IStreamingDevice>(_host.SelectedDevice);
        var message = Assert.Single(_host.SuccessMessages);
        Assert.Equal(
            FirmwareUpdateMessages.UpdateSucceeded(
                FirmwareUpdateMessages.DeviceNameFor(device), device.DeviceVersion, reportedVersion: null)
            + "\n\n" + FirmwareUpdateCoordinator.WifiSettingsMayBeErasedInstruction,
            message);
        Assert.EndsWith(FirmwareUpdateCoordinator.WifiSettingsMayBeErasedInstruction, Assert.Single(_host.Notifications).Message);
    }
    #endregion

    #region Leaving while the update is being prepared (coordinator)
    /// <summary>
    /// Review finding 3 on PR #575: the release lookup awaits the network before the prompt opens.
    /// A cancel during it ends the run at once — even though this lookup ignores its token — and the
    /// lookup finishing afterwards opens nothing: no prompt, no error, no write.
    /// </summary>
    [Fact]
    public async Task A_cancel_during_the_lookup_ends_the_run_and_a_late_lookup_opens_nothing()
    {
        _host.FirmwareFilePath = string.Empty;
        var lookup = HoldTheLookup();
        var coordinator = CreateCoordinator();

        var run = coordinator.UploadFirmwareAsync();
        Assert.False(run.IsCompleted);
        coordinator.CancelPreparation();
        await run.WaitAsync(Timeout);

        Assert.True(_downloads.LookupTokens.Single().IsCancellationRequested);
        lookup.SetResult(Release("3.9.0"));
        await Task.Yield();

        Assert.Equal(0, _host.PromptCount);
        Assert.Empty(_host.Errors);
        Assert.Equal(0, _downloads.DownloadCount);
        Assert.Equal(0, _updates.Pic32FlashCount);
        Assert.False(_host.FlagWasEverRaised);
    }

    /// <summary>
    /// The check after the lookup does not depend on the cancel alone: a different device selected
    /// during the lookup, with no cancel at all, still means no prompt.
    /// </summary>
    [Fact]
    public async Task A_different_device_selected_during_the_lookup_means_no_prompt()
    {
        _host.FirmwareFilePath = string.Empty;
        var lookup = HoldTheLookup();

        var run = CreateCoordinator().UploadFirmwareAsync();
        _host.SelectedDevice = new SerialStreamingDevice("COM-TEST-OTHER");
        lookup.SetResult(Release("3.9.0"));
        await run.WaitAsync(Timeout);

        Assert.Equal(0, _host.PromptCount);
        Assert.Empty(_host.Errors);
        Assert.Equal(0, _updates.Pic32FlashCount);
    }

    /// <summary>
    /// A lookup that completes with nothing changed opens the prompt as before, and the write uses
    /// the tag that lookup resolved.
    /// </summary>
    [Fact]
    public async Task A_lookup_that_completes_with_nothing_changed_prompts_and_writes_that_tag()
    {
        _host.FirmwareFilePath = string.Empty;
        var lookup = HoldTheLookup();

        var run = CreateCoordinator().UploadFirmwareAsync();
        lookup.SetResult(Release("3.9.0"));
        await run.WaitAsync(Timeout);

        Assert.Equal(1, _host.PromptCount);
        Assert.Contains("New version: 3.9.0", _host.LastMessage);
        Assert.Equal(["v3.9.0"], _downloads.DownloadedTags);
        Assert.Equal([_downloads.PathFor("v3.9.0")], _updates.FlashedPaths);
    }

    /// <summary>
    /// The rule for a second UPDATE during a pending lookup: it is ignored. It returns at once, it
    /// starts no second lookup, and the first request goes on to one prompt and one write.
    /// </summary>
    [Fact]
    public async Task A_second_update_during_a_pending_lookup_is_ignored()
    {
        _host.FirmwareFilePath = string.Empty;
        var lookup = HoldTheLookup();
        var coordinator = CreateCoordinator();

        var first = coordinator.UploadFirmwareAsync();
        var second = coordinator.UploadFirmwareAsync();
        await second.WaitAsync(Timeout);
        Assert.Equal(1, _downloads.LookupCount);

        lookup.SetResult(Release("3.9.0"));
        await first.WaitAsync(Timeout);

        Assert.Equal(1, _host.PromptCount);
        Assert.Equal(1, _updates.Pic32FlashCount);
    }

    /// <summary>
    /// The manual path has no lookup and keeps its behaviour: the prompt opens at once, and a cancel
    /// of the preparation closes it with "no" and writes nothing — what a tab change did before.
    /// </summary>
    [Fact]
    public async Task A_manual_update_has_no_lookup_and_a_cancel_closes_its_prompt()
    {
        var coordinator = CreateCoordinator();
        _host.PendingAnswer = new TaskCompletionSource<bool>().Task;

        var run = coordinator.UploadFirmwareAsync();
        Assert.True(_host.IsPromptOpen);
        coordinator.CancelPreparation();
        await run.WaitAsync(Timeout);

        Assert.Equal(0, _downloads.LookupCount);
        Assert.False(_host.IsPromptOpen);
        Assert.Equal(0, _updates.Pic32FlashCount);
        Assert.False(_host.FlagWasEverRaised);
    }

    /// <summary>A cancel after a run has started does not touch the run.</summary>
    [Fact]
    public async Task A_cancel_of_the_preparation_does_not_stop_a_started_write()
    {
        var coordinator = CreateCoordinator();
        _updates.OnFlash = coordinator.CancelPreparation;

        await coordinator.UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Equal(1, _updates.Pic32FlashCount);
        Assert.Equal(1, _host.SucceededCount);
    }
    #endregion

    #region Leaving while the update is being prepared (the real view models)
    /// <summary>
    /// The finding as the user meets it, on the real <see cref="DaqifiViewModel"/>: UPDATE, then
    /// another tab while the lookup runs. The UPDATE command is no longer busy, nothing is written,
    /// the late lookup opens no prompt, and going back to Devices shows none.
    /// </summary>
    /// <remarks>
    /// Nothing here pumps <c>Dispatcher.UIThread</c>, which the test project cannot do: the command,
    /// the coordinator and the overlay view model are all plain awaits and property writes.
    /// </remarks>
    [Fact]
    public async Task Leaving_the_devices_tab_during_the_lookup_frees_the_command_and_opens_no_prompt()
    {
        var lookup = HoldTheLookup();
        var shell = ShellOnDevicesTab();

        var run = shell.UploadFirmwareCommand.ExecuteAsync(null);
        Assert.True(shell.UploadFirmwareCommand.IsRunning);
        shell.SelectedIndex = 0;
        await run.WaitAsync(Timeout);

        Assert.False(shell.UploadFirmwareCommand.IsRunning);
        lookup.SetResult(Release("3.9.0"));
        await Task.Yield();
        shell.SelectedIndex = 3;

        Assert.False(shell.FirmwareUpdateConfirm.IsOpen);
        Assert.False(shell.IsFirmwareUploading);
        Assert.Equal(0, _updates.Pic32FlashCount);
    }

    /// <summary>The drawer closed during the lookup: no prompt.</summary>
    [Fact]
    public async Task Closing_the_drawer_during_the_lookup_opens_no_prompt()
    {
        var lookup = HoldTheLookup();
        var shell = ShellOnDevicesTab();
        using var pane = new DevicesPaneViewModel(shell);
        pane.OpenSettingsCommand.Execute(new DeviceTileViewModel(_device));

        var run = shell.UploadFirmwareCommand.ExecuteAsync(null);
        pane.CloseSettingsCommand.Execute(null);
        await run.WaitAsync(Timeout);
        lookup.SetResult(Release("3.9.0"));
        await Task.Yield();

        Assert.False(shell.UploadFirmwareCommand.IsRunning);
        Assert.False(shell.FirmwareUpdateConfirm.IsOpen);
        Assert.Equal(0, _updates.Pic32FlashCount);
    }

    /// <summary>Another device selected during the lookup: the command ends at once, no prompt.</summary>
    [Fact]
    public async Task Selecting_another_device_during_the_lookup_opens_no_prompt()
    {
        var lookup = HoldTheLookup();
        var shell = ShellOnDevicesTab();

        var run = shell.UploadFirmwareCommand.ExecuteAsync(null);
        shell.SelectedDevice = new SerialStreamingDevice("COM-TEST-OTHER");
        await run.WaitAsync(Timeout);
        lookup.SetResult(Release("3.9.0"));
        await Task.Yield();

        Assert.False(shell.FirmwareUpdateConfirm.IsOpen);
        Assert.Equal(0, _updates.Pic32FlashCount);
    }

    /// <summary>
    /// Nothing changed during the lookup: the prompt opens as before and names the resolved
    /// release. (The write that follows a "yes" is pinned at the coordinator level above; here the
    /// test answers CANCEL, because the success path shows a dialog through the UI dispatcher.)
    /// </summary>
    [Fact]
    public async Task A_lookup_with_nothing_changed_opens_the_prompt_on_the_real_view_model()
    {
        var lookup = HoldTheLookup();
        var shell = ShellOnDevicesTab();

        var run = shell.UploadFirmwareCommand.ExecuteAsync(null);
        lookup.SetResult(Release("3.9.0"));
        for (var i = 0; i < 100 && !shell.FirmwareUpdateConfirm.IsOpen; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(shell.FirmwareUpdateConfirm.IsOpen);
        Assert.Contains("New version: 3.9.0", shell.FirmwareUpdateConfirm.Message);

        shell.FirmwareUpdateConfirm.NegativeCommand.Execute(null);
        await run.WaitAsync(Timeout);
        Assert.Equal(0, _updates.Pic32FlashCount);
    }

    /// <summary>
    /// What a tab change did before the lookup existed still holds, now through the preparation's
    /// token: an open prompt (here a manual upload, which has no lookup) closes, and the command ends.
    /// </summary>
    [Fact]
    public async Task Leaving_the_devices_tab_closes_an_open_prompt_on_the_real_view_model()
    {
        var shell = ShellOnDevicesTab();
        shell.FirmwareFilePath = FirmwareFile();

        var run = shell.UploadFirmwareCommand.ExecuteAsync(null);
        Assert.True(shell.FirmwareUpdateConfirm.IsOpen);
        shell.SelectedIndex = 0;
        await run.WaitAsync(Timeout);

        Assert.False(shell.FirmwareUpdateConfirm.IsOpen);
        Assert.False(shell.UploadFirmwareCommand.IsRunning);
        Assert.Equal(0, _downloads.LookupCount);
        Assert.Equal(0, _updates.Pic32FlashCount);
    }

    /// <summary>
    /// A shell over this test's services, with the device selected and the Devices tab showing —
    /// the state the drawer's UPDATE button is clicked in. An auto-update, so the lookup runs.
    /// </summary>
    private DaqifiViewModel ShellOnDevicesTab()
    {
        var shell = new DaqifiViewModel(new NullDialogService(), _updates, _downloads)
        {
            SelectedIndex = 3,
            SelectedDevice = _device,
        };
        shell.FirmwareFilePath = string.Empty;
        return shell;
    }
    #endregion

    #region Helpers
    /// <summary>Makes every release lookup wait until the test completes the returned source.</summary>
    private TaskCompletionSource<FirmwareReleaseInfo?> HoldTheLookup()
    {
        var lookup = new TaskCompletionSource<FirmwareReleaseInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _downloads.PendingLookup = lookup;
        return lookup;
    }

    private static FirmwareReleaseInfo Release(string version)
    {
        Assert.True(FirmwareVersion.TryParse(version, out var parsed));
        return new FirmwareReleaseInfo { Version = parsed, TagName = $"v{version}", IsPreRelease = false };
    }

    /// <summary>A stand-in PIC32 image: the coordinator checks only that the file exists.</summary>
    private string FirmwareFile()
    {
        Directory.CreateDirectory(_dataDirectory);
        var path = Path.Combine(_dataDirectory, "firmware.hex");
        File.WriteAllText(path, ":00000001FF\n");
        return path;
    }

    /// <summary>Host that answers the prompt as told and records what the run did to it.</summary>
    private sealed class PromptHost : IFirmwareUpdateHost
    {
        private bool _isFirmwareUploading;

        /// <summary>The answer given at once, used when <see cref="PendingAnswer"/> is null.</summary>
        public bool Answer { get; set; } = true;

        /// <summary>An answer the test resolves later, to hold the prompt open.</summary>
        public Task<bool>? PendingAnswer { get; set; }

        public int PromptCount { get; private set; }

        public string LastTitle { get; private set; } = string.Empty;

        public string LastMessage { get; private set; } = string.Empty;

        public bool FlagWasEverRaised { get; private set; }

        public int QuiesceCount { get; private set; }

        public int RefreshCount { get; private set; }

        public List<string> StatusWrites { get; } = [];

        public DesktopStreamingDevice? SelectedDevice { get; set; }

        public IReadOnlyList<DesktopStreamingDevice> ConnectedDevices { get; } = [];

        public string FirmwareFilePath { get; set; } = string.Empty;

        public bool SelectedDeviceSupportsFirmwareUpdate { get; set; }

        public bool IsFirmwareUploading
        {
            get => _isFirmwareUploading;
            set
            {
                _isFirmwareUploading = value;
                FlagWasEverRaised |= value;
            }
        }

        public bool IsUploadComplete { get; set; }

        public bool HasErrorOccured { get; set; }

        public int UploadFirmwareProgress { get; set; }

        public int UploadWiFiProgress { get; set; }

        public string FirmwareUpdateStatusText
        {
            set => StatusWrites.Add(value);
        }

        public ObservableCollection<Notifications> Notifications { get; } = [];

        public DesktopStreamingDevice? DeviceBeingUpdated { get; set; }

        public void RefreshNotificationCount() => RefreshCount++;

        public void ShowFirmwareError(string message) => Errors.Add(message);

        public List<string> SuccessMessages { get; } = [];

        public List<string> Errors { get; } = [];

        public int SucceededCount => SuccessMessages.Count;

        public void ShowFirmwareUpdateSucceeded(string message) => SuccessMessages.Add(message);

        /// <summary>True while a prompt is open: shown and neither answered nor closed.</summary>
        public bool IsPromptOpen { get; private set; }

        /// <summary>
        /// Behaves as the real host does: a cancelled token opens nothing, and cancelling the token
        /// closes an open prompt with "no".
        /// </summary>
        public async Task<bool> ConfirmFirmwareUpdateAsync(
            string title,
            string message,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            PromptCount++;
            LastTitle = title;
            LastMessage = message;
            IsPromptOpen = true;
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (cancellationToken.Register(() => closed.TrySetResult(false)))
            {
                var answer = await Task.WhenAny(PendingAnswer ?? Task.FromResult(Answer), closed.Task);
                IsPromptOpen = false;
                return await answer;
            }
        }

        public Task QuiesceWifiFirmwareProbeAsync(CancellationToken cancellationToken = default)
        {
            QuiesceCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>PIC32 update service that counts writes and can fail one.</summary>
    private sealed class CountingUpdateService : IFirmwareUpdateService
    {
        public int Pic32FlashCount => FlashedPaths.Count;

        public List<string> FlashedPaths { get; } = [];

        public bool Fail { get; set; }

        public FirmwareUpdateState CurrentState => FirmwareUpdateState.Idle;

#pragma warning disable CS0067 // Part of the interface; nothing in these tests raises it.
        public event EventHandler<FirmwareUpdateStateChangedEventArgs>? StateChanged;
#pragma warning restore CS0067

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default) => Flash(hexFilePath);

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress,
            string? targetDevicePath,
            string? targetLocationKey,
            CancellationToken cancellationToken = default) => Flash(hexFilePath);

        public Task UpdateWifiModuleAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string firmwarePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default,
            bool skipVersionCheck = false) => throw new NotSupportedException("These runs never flash the WiFi module.");

        public Task<WifiFirmwareStatus> CheckWifiFirmwareStatusAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>Runs inside the flash, while the write is in progress.</summary>
        public Action? OnFlash { get; set; }

        private Task Flash(string hexFilePath)
        {
            FlashedPaths.Add(hexFilePath);
            OnFlash?.Invoke();
            return Fail
                ? Task.FromException(new InvalidOperationException("simulated flash failure"))
                : Task.CompletedTask;
        }
    }

    /// <summary>
    /// Download service with a scripted release feed. <see cref="Releases"/> is what successive
    /// <c>GetLatestReleaseAsync</c> calls return, the last entry repeating, and what
    /// <c>DownloadLatestFirmwareAsync</c> treats as "latest now" — so "A, then B" models a release
    /// published while the prompt is open. Both download methods write a stand-in file named after the
    /// release they fetched, so the path the flash receives says which release it was.
    /// </summary>
    private sealed class CountingDownloadService(string directory) : IFirmwareDownloadService
    {
        private int _lookups;

        public List<FirmwareReleaseInfo?> Releases { get; } = [];

        public Exception? LookupFailure { get; set; }

        /// <summary>When set, every lookup returns this task, which the test completes by hand.</summary>
        public TaskCompletionSource<FirmwareReleaseInfo?>? PendingLookup { get; set; }

        public int LookupCount { get; private set; }

        public List<CancellationToken> LookupTokens { get; } = [];

        public int LatestFirmwareDownloadCount { get; private set; }

        public List<string> DownloadedTags { get; } = [];

        public int DownloadCount => LatestFirmwareDownloadCount + DownloadedTags.Count;

        public Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> DownloadLatestFirmwareAsync(
            string destinationDirectory,
            bool includePreRelease = false,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            LatestFirmwareDownloadCount++;
            var latest = Releases.Count == 0 ? null : Releases[^1];
            return Task.FromResult(latest == null ? null : WriteFile(latest.TagName));
        }

        public Task<string?> DownloadFirmwareByTagAsync(
            string tagName,
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadedTags.Add(tagName);
            return Task.FromResult<string?>(WriteFile(tagName));
        }

        public Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(
            bool includePreRelease = false,
            CancellationToken cancellationToken = default)
        {
            LookupCount++;
            LookupTokens.Add(cancellationToken);
            if (LookupFailure != null)
            {
                return Task.FromException<FirmwareReleaseInfo?>(LookupFailure);
            }

            // A lookup the test completes by hand. It deliberately ignores the token, the worst
            // case: the coordinator must stop waiting on its own when the preparation is cancelled.
            if (PendingLookup != null)
            {
                return PendingLookup.Task;
            }

            if (Releases.Count == 0)
            {
                return Task.FromResult<FirmwareReleaseInfo?>(null);
            }

            var release = Releases[Math.Min(_lookups, Releases.Count - 1)];
            _lookups++;
            return Task.FromResult(release);
        }

        public Task<FirmwareReleaseInfo?> GetLatestWifiReleaseAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<FirmwareReleaseInfo?>(null);

        public Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(
            string deviceVersionString,
            bool includePreRelease = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void InvalidateCache() { }

        /// <summary>The path a download of <paramref name="tagName"/> lands at.</summary>
        public string PathFor(string tagName) => Path.Combine(directory, $"release-{tagName}.hex");

        private string WriteFile(string tagName)
        {
            Directory.CreateDirectory(directory);
            var path = PathFor(tagName);
            File.WriteAllText(path, ":00000001FF\n");
            return path;
        }
    }
    #endregion
}
