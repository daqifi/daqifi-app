// Avalonia-only: no upstream counterpart, so no @port marker.

using System;

namespace Daqifi.Desktop.Device;

/// <summary>
/// The one rule for "do these two serial numbers name the same device?" (issue #339).
/// </summary>
/// <remarks>
/// <para>
/// The serial is the identity key for notification de-duplication, for matching a saved profile
/// entry to a connected board, and for deciding whether the device a drawer is open on is still
/// there. Before this helper each of those sites spelled the comparison out for itself, and they
/// disagreed: <c>FirmwareUpdateCoordinator</c> used <c>==</c> (ordinal) while the newer
/// <c>DaqifiViewModel</c> notification code used <see cref="StringComparison.OrdinalIgnoreCase"/>,
/// so the same question resolved differently depending on which file asked it. A bare
/// <c>OrdinalIgnoreCase</c> repeated at each site would be the same latent inconsistency, one
/// convention later; the point of a named helper is that the next site cannot drift.
/// </para>
/// <para>
/// <see cref="StringComparison.OrdinalIgnoreCase"/> is the rule because serials printed on hardware
/// labels are not reliably cased, and because case-folding can only ever collapse two serials that
/// differ in case alone — never two genuinely distinct devices, whose serials differ in some
/// character that is not a case variant of the other's. Erring that way is also the safe direction
/// here: a missed match shows the owner a duplicate notice, while a wrong match would hide one.
/// </para>
/// <para>
/// This decides the comparison only, not whether a serial identifies a device at all. Whether a
/// null or blank serial should take part is a separate question each caller already answers for
/// itself — a notification with no serial is the app-update notice, not an unidentified device —
/// so callers keep their own null/blank guards and this never introduces one.
/// </para>
/// <para>
/// Case is not the only axis a serial can vary on: Core's <c>DeviceIdentity.Matches</c> also
/// reconciles the decimal and hex renderings of one board's identity, which is why
/// <c>ConnectionManager.CheckForDuplicateDevice</c> uses it instead. Every serial that reaches this
/// helper today is base-10 text copied straight off the connected device, so the two agree; the
/// first change that puts a non-decimal serial into <c>IStreamingDevice.DeviceSerialNo</c> should
/// re-point this one method at <c>DeviceIdentity</c> rather than add a third convention.
/// </para>
/// </remarks>
public static class DeviceSerials
{
    /// <summary>
    /// Whether two serial numbers name the same device.
    /// </summary>
    /// <remarks>
    /// Null-safe in both arguments — this is <see cref="string.Equals(string?, string?, StringComparison)"/>,
    /// the static overload, so neither side is dereferenced and two nulls compare equal exactly as
    /// <c>==</c> did. Callers that must not treat a missing serial as an identity guard for that
    /// themselves, as they did before.
    /// </remarks>
    public static bool SameDevice(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
