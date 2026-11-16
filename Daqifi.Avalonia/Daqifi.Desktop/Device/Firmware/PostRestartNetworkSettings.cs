// Downstream-only: no upstream counterpart. Issues #642 and #644.

using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Network;

namespace Daqifi.Desktop.Device.Firmware;

/// <summary>
/// The WiFi settings that one status reply of the device states. A null field is a value that the
/// reply did not state, not a value of "none".
/// </summary>
/// <param name="Mode">The WiFi mode, or null when the reply had no valid mode.</param>
/// <param name="Ssid">The SSID, or null when the reply had no SSID.</param>
/// <param name="SecurityType">
/// The security type. Never null in a value that <see cref="PostRestartNetworkSettings.FromStatus"/>
/// makes: a full status that has no security field means an open network (see there).
/// </param>
public sealed record ReportedNetworkSettings(WifiMode? Mode, string? Ssid, WifiSecurityType? SecurityType);

/// <summary>
/// Reads the WiFi settings from a status reply that the device sends AFTER a firmware update, when
/// the device has completed its start (issue #642).
/// </summary>
/// <remarks>
/// <para>
/// The drawer showed wrong WiFi settings after an update for two reasons that are not in this app:
/// </para>
/// <list type="bullet">
/// <item>Core's <c>DeviceMetadata.UpdateFromProtobuf</c> applies the security mode only when it is
/// more than 0, and proto3 does not send a 0. So an "open" device kept the "WPA" from before the
/// update (daqifi/daqifi-core#986).</item>
/// <item>For a short time after a restart the firmware reports the SSID without its MAC suffix,
/// "DAQiFi" instead of "DAQiFi-95AC" (daqifi/daqifi-nyquist-firmware#1266). The status that Core
/// reads when it connects again after the update is in that window.</item>
/// </list>
/// <para>
/// So this waits until the window is over, asks for one status reply and takes the values from that
/// reply itself, not from Core's metadata. Like <see cref="PostRestartFirmwareVersion"/>, call it
/// only after the write has returned.
/// </para>
/// </remarks>
public static class PostRestartNetworkSettings
{
    /// <summary>
    /// How long <see cref="ReadAsync"/> waits before it asks, when no delay is given.
    /// </summary>
    /// <remarks>
    /// Measured on the bench (Nyquist 1, firmware 3.9.1, macOS, 2026-11-16) after
    /// <c>SYSTem:REboot</c>: the USB port came back 1.7 s after the command, the first status
    /// 0.3 s later had "DAQiFi", and every status from 0.9 s on had "DAQiFi-95AC". The write
    /// returns only after Core has connected again and initialized, so 4 s from there is a wide
    /// margin over that window and costs the user 4 s once per update.
    /// </remarks>
    public static readonly TimeSpan DefaultSettleDelay = TimeSpan.FromSeconds(4);

    /// <summary>How long <see cref="ReadAsync"/> waits for the reply when no timeout is given.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Waits <paramref name="settleDelay"/>, asks <paramref name="coreDevice"/> for a status reply and
    /// returns the WiFi settings in it.
    /// </summary>
    /// <returns>
    /// The settings the device reported, or <c>null</c> when they are not known: the reply was not a
    /// full status, it had no WiFi settings, no reply came in time, or the device is not connected.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled. A timeout is not a cancellation; it
    /// returns <c>null</c>.
    /// </exception>
    public static async Task<ReportedNetworkSettings?> ReadAsync(
        DaqifiDevice coreDevice,
        TimeSpan? settleDelay = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coreDevice);

        // A device that is not connected cannot answer; do not make the user wait for the delay.
        if (!coreDevice.IsConnected)
        {
            return null;
        }

        var delay = settleDelay ?? DefaultSettleDelay;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        var wait = timeout ?? DefaultTimeout;
        var reply = new TaskCompletionSource<DaqifiOutMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStatus(DaqifiOutMessage message) => reply.TrySetResult(message);

        // Subscribe before the request, so a device that answers immediately cannot be missed.
        coreDevice.StatusMessageReceived += OnStatus;
        try
        {
            await coreDevice.RefreshDeviceStatusAsync(wait, cancellationToken).ConfigureAwait(false);

            // Core does not promise that its own wait completes after this handler has run.
            var message = await reply.Task.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            return FromStatus(message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No reply, a device that is not connected, or a transport fault: the settings are unknown.
            return null;
        }
        finally
        {
            coreDevice.StatusMessageReceived -= OnStatus;
        }
    }

    /// <summary>
    /// The WiFi settings in one status reply, or <c>null</c> when the reply is not a full status or
    /// states no WiFi settings.
    /// </summary>
    /// <remarks>
    /// A full status is one that identifies the device (a serial number or a part number): it is
    /// the reply to <c>SYSTem:SYSInfoPB?</c>, which carries every field the device has a value for.
    /// In such a reply an absent security field is the proto3 encoding of 0, which is an open
    /// network. Core reads the same absence as "not reported" and keeps the old value
    /// (daqifi/daqifi-core#986); this reads it as open. Remove this rule, and read Core's metadata,
    /// when a Core release with the #986 correction is pinned.
    /// </remarks>
    public static ReportedNetworkSettings? FromStatus(DaqifiOutMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var isFullStatus = message.DeviceSn != 0 || !string.IsNullOrWhiteSpace(message.DevicePn);
        if (!isFullStatus)
        {
            return null;
        }

        WifiMode? mode = message.WifiInfMode switch
        {
            (uint)WifiMode.ExistingNetwork => WifiMode.ExistingNetwork,
            (uint)WifiMode.SelfHosted => WifiMode.SelfHosted,
            _ => null,
        };
        var ssid = string.IsNullOrWhiteSpace(message.Ssid) ? null : message.Ssid.Trim();

        // A reply with neither a mode nor an SSID has no WiFi section to read: its absent security
        // field then says nothing about the network.
        if (mode is null && ssid is null)
        {
            return null;
        }

        // Workaround for daqifi/daqifi-core#986: 0 (absent) is open, not "keep the old value".
        var security = (WifiSecurityType)message.WifiSecurityMode;
        return new ReportedNetworkSettings(mode, ssid, security);
    }
}
