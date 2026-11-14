// Downstream-only: no upstream counterpart. Issues #566 and #570.
//
// This file is added, byte for byte the same, by the PR for #566 and by the PR for #570, so that
// whichever merges second merges cleanly. Do not edit it in one of them only.

using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;

namespace Daqifi.Desktop.Device.Firmware;

/// <summary>
/// Reads the firmware version from a status reply that the device sends AFTER a firmware write.
/// </summary>
/// <remarks>
/// <para>
/// <c>DaqifiDevice.Metadata.FirmwareVersion</c> cannot answer "which version does the device run
/// now". Core writes that field only when a status reply carries a version, and keeps the old
/// value otherwise. Core can also complete a firmware write when its initialization after the
/// reconnect fails. In both cases the field still holds the version from before the write, and a
/// caller cannot tell it from a new reading.
/// </para>
/// <para>
/// So this asks the device for one status reply and takes the version from that reply itself. It
/// does not read or write the shared metadata. Call it only after the write has returned: the
/// subscription starts here, so each reply it can see comes from the restarted device.
/// </para>
/// </remarks>
public static class PostRestartFirmwareVersion
{
    /// <summary>How long <see cref="ReadAsync"/> waits for the reply when no timeout is given.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Asks <paramref name="coreDevice"/> for a status reply and returns the firmware version in it.
    /// </summary>
    /// <returns>
    /// The version the device reported, or <c>null</c> when the version is not known: the reply had
    /// no version, no reply came in time, or the device is not connected.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled. A timeout is not a cancellation; it
    /// returns <c>null</c>.
    /// </exception>
    public static async Task<string?> ReadAsync(
        DaqifiDevice coreDevice,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coreDevice);

        var wait = timeout ?? DefaultTimeout;
        var reply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStatus(DaqifiOutMessage message) => reply.TrySetResult(message.DeviceFwRev);

        // Subscribe before the request, so a device that answers immediately cannot be missed.
        coreDevice.StatusMessageReceived += OnStatus;
        try
        {
            await coreDevice.RefreshDeviceStatusAsync(wait, cancellationToken).ConfigureAwait(false);

            // Core completes its own wait and raises this event from the same reply, but it does
            // not promise the order. Wait for this handler's copy and do not assume it has run.
            var version = await reply.Task.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No reply, a device that is not connected, or a transport fault: the version is unknown.
            return null;
        }
        finally
        {
            coreDevice.StatusMessageReceived -= OnStatus;
        }
    }
}
