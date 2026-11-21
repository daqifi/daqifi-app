using Daqifi.Core.Firmware;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device.Firmware;

/// <summary>
/// A failed PIC32 flash as Daqifi.Core 1.10.0 reports it: one <see cref="FirmwareUpdateException"/>
/// for each <see cref="Pic32FlashCondition"/>, and the text the user must then read (issue #672).
/// <para>
/// Each exception carries the failed state, the operation, the message and the recovery guidance
/// that Core gives for that condition. Core makes them in internal code
/// (<c>Pic32FirmwareUpdater.GetFlashCondition</c> and <c>BuildRecoveryGuidance</c>,
/// <c>FirmwareUpdateContext.CreateFirmwareUpdateException</c> and its per-state guidance,
/// <c>src/Daqifi.Core/Firmware/</c> at tag <c>v1.10.0</c>), so the values are copied here. A test
/// cannot make Core produce them without a real failed flash, and a flash is on the bench board's
/// destructive list.
/// </para>
/// <para>
/// The expected text is written out in full, not computed by the app's own mapping: both the device
/// drawer's update (<c>FirmwareUpdateOutcomeTests</c>) and the bootloader dialog
/// (<c>FirmwareDialogFailureGuidanceTests</c>) must show exactly this text, so a change to either
/// window that makes them differ fails a test.
/// </para>
/// </summary>
internal static class CoreFlashFailures
{
    /// <summary>One Core condition, the exception Core throws for it, and what the user must see.</summary>
    /// <param name="Exception">The exception Core 1.10.0 throws for this condition.</param>
    /// <param name="FirmwareInstalled">True when the image is programmed and verified, so the run is an installed one.</param>
    /// <param name="ExpectedText">The text both firmware windows must show.</param>
    /// <param name="ExpectedErrorLog">The Error line (a Sentry event) for a genuine failure; null when nothing may be logged at Error.</param>
    internal sealed record Case(
        FirmwareUpdateException Exception,
        bool FirmwareInstalled,
        string ExpectedText,
        string? ExpectedErrorLog);

    /// <summary>The text for a programmed-and-verified flash whose device did not come back.</summary>
    internal const string InstalledText =
        "Firmware was installed successfully, but the device did not return to normal mode on its " +
        "own. Please power-cycle the device (unplug and replug its USB cable), then reconnect.";

    /// <summary>
    /// The case for <paramref name="condition"/>. A condition that a later Core adds has no case, and
    /// throws here, so the tests that enumerate the conditions fail instead of skipping it.
    /// </summary>
    internal static Case For(Pic32FlashCondition condition) => condition switch
    {
        // The update stopped before the erase command was written (daqifi-core#952).
        Pic32FlashCondition.NotChanged => Failure(
            condition,
            FirmwareUpdateState.ErasingFlash,
            "Erasing PIC32 flash.",
            "The update stopped before the erase command was sent, so the device's application flash " +
            "was not changed. Re-run the firmware update, or power-cycle the device to return to its " +
            "existing firmware."),

        // Programming failed, and the automatic re-erase succeeded.
        Pic32FlashCondition.CleanBootloader => Failure(
            condition,
            FirmwareUpdateState.Programming,
            "Programming flash records.",
            "The update did not complete, but the device's application flash was automatically " +
            "re-erased and it is now in a clean bootloader state — safe to re-flash. Simply re-run " +
            "the firmware update."),

        // Programming failed, and the automatic re-erase could not run.
        Pic32FlashCondition.PossiblyHalfFlashed => Failure(
            condition,
            FirmwareUpdateState.Programming,
            "Programming flash records.",
            "The update failed and the automatic re-erase cleanup could not complete, so the device " +
            "may be in a half-flashed state. Power-cycle the device into bootloader mode and re-run " +
            "the firmware update; the next erase will restore a clean state."),

        // Erase, program and CRC-verify all passed; the jump back to the application failed.
        Pic32FlashCondition.ProgrammedNotConfirmed => new Case(
            CoreException(
                condition,
                FirmwareUpdateState.JumpingToApp,
                "Jumping to application firmware.",
                "The device did not return to application mode. Power-cycle the device and reconnect."),
            FirmwareInstalled: true,
            InstalledText,
            ExpectedErrorLog: null),

        _ => throw new ArgumentOutOfRangeException(
            nameof(condition),
            condition,
            $"Core reports a flash condition that {nameof(CoreFlashFailures)} has no case for. Add one, " +
            "with the state and guidance Core gives it, and check what both firmware windows show."),
    };

    /// <summary>Every condition Core can report.</summary>
    public static TheoryData<Pic32FlashCondition> All() => [.. Enum.GetValues<Pic32FlashCondition>()];

    /// <summary>The conditions that leave the firmware not installed: a genuine failure.</summary>
    public static TheoryData<Pic32FlashCondition> NotInstalled() =>
        [.. Enum.GetValues<Pic32FlashCondition>().Where(condition => !For(condition).FirmwareInstalled)];

    private static Case Failure(
        Pic32FlashCondition condition,
        FirmwareUpdateState state,
        string operation,
        string guidance)
    {
        var summary = $"Firmware update failed during '{operation}' ({state}).";
        return new Case(
            CoreException(condition, state, operation, guidance),
            FirmwareInstalled: false,
            $"{summary}{Environment.NewLine}{Environment.NewLine}Suggested recovery: {guidance}",
            summary);
    }

    /// <summary>The exception as Core's <c>CreateFirmwareUpdateException</c> builds it.</summary>
    private static FirmwareUpdateException CoreException(
        Pic32FlashCondition condition,
        FirmwareUpdateState state,
        string operation,
        string guidance) =>
        new(state,
            operation,
            $"Firmware update failed in state '{state}' while {operation}.",
            guidance,
            new TimeoutException($"State '{state}' timed out."))
        {
            FlashCondition = condition,
        };
}
