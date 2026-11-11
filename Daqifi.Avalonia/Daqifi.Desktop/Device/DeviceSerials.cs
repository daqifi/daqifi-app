// Avalonia-only: no upstream counterpart, so no @port marker.

using System;
using System.Globalization;

namespace Daqifi.Desktop.Device;

/// <summary>
/// The one rule for "do these two serial numbers name the same device?" (issue #339), and the one
/// rule for how a serial number is shown to the user (issue #493).
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
/// Case is not the only axis a serial can vary on: one board's serial has a decimal rendering
/// (what Core's protobuf and discovery paths put in <c>IStreamingDevice.DeviceSerialNo</c>, and so
/// what every connected device, saved profile and live-logged session holds) and a hex rendering
/// (what <c>*IDN?</c>, the capability document and the header of an SD-card log report, and —
/// since #493 — what the app shows). The text alone cannot always tell
/// the two apart: a 16-digit hex serial made only of digits is also a valid decimal number, so
/// <c>0000000000000400</c> (stored serial <c>1024</c>) also reads as decimal <c>400</c>. Core's
/// <c>DeviceIdentity.Matches</c> resolves that by reading decimal first, which rejects board
/// <c>1024</c> and accepts board <c>400</c>, and Core 1.9.1 has no way to say which format a value
/// is in. So the format comes from where the value came from, never from its text, and
/// <see cref="SameDevice"/> compares two stored serials and never reads hex. No input in the app
/// accepts a serial that a person types or copies in the hex form yet: matching one copied off the
/// screen, for example into the profiles file, needs a format the app can identify and is issue
/// #553.
/// </para>
/// <para>
/// One kind of stored serial is not decimal. A session imported from an SD-card log keeps the
/// serial from the file header, which the firmware writes with <c>"%016llX"</c> in both the CSV
/// and the JSON format, and Core 1.9.1's SD parsers keep that text as it is. None of
/// <see cref="SameDevice"/>'s callers compares such a serial, so matching is not affected, but
/// <see cref="ToDisplay"/> cannot know where a value came from: it shows such a serial unchanged
/// when it has a letter in it, and converts one made only of the digits 0–9 as if it were decimal,
/// which shows a wrong serial for that session. For uniformly random serials that is about one
/// board in 1,800, and the two Nq1 serials seen so far both have letters in them.
/// </para>
/// <para>
/// This class does not change any stored value. The serial is part of the SQLite key for session
/// device metadata and is what saved profiles hold, so changing it would orphan existing data.
/// Only the rendering changes, through <see cref="ToDisplay"/> and <see cref="ToShortDisplay"/>, at
/// the point a serial is shown.
/// </para>
/// </remarks>
public static class DeviceSerials
{
    /// <summary>
    /// Width of the hex serial the firmware prints in the third <c>*IDN?</c> field
    /// (<c>snprintf(gIdnSerial, ..., "%016llX", boardSerialNumber)</c>): the full 64-bit value,
    /// zero-padded, upper case, no prefix. Printing the same width is what makes the shown serial
    /// the same text as the <c>*IDN?</c> field.
    /// </summary>
    private const int HexWidth = 16;

    /// <summary>
    /// The number of trailing characters the short form keeps.
    /// </summary>
    private const int ShortLength = 4;

    /// <summary>
    /// Whether two STORED serial numbers name the same device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every caller compares two values the app stored itself — a connected device's serial, a
    /// notification's, a saved profile entry's — and each one is the base-10 text of one
    /// <c>ulong</c>, which has exactly one spelling. So equal text is the whole test, and neither
    /// side is ever read as hex: hex text in a stored field (for example a serial copied off the
    /// screen into a profile) matches no board by serial, rather than the board its digits spell in
    /// decimal. The profile matcher then falls back to the first device of the same model, as it
    /// did before #493; matching the copied hex form to its own board is issue #553.
    /// </para>
    /// <para>
    /// Null-safe in both arguments — this is <see cref="string.Equals(string?, string?, StringComparison)"/>,
    /// the static overload, so neither side is dereferenced and two nulls compare equal exactly as
    /// <c>==</c> did. Callers that must not treat a missing serial as an identity guard for that
    /// themselves, as they did before.
    /// </para>
    /// </remarks>
    public static bool SameDevice(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The serial as the user sees it: the 16-digit upper-case hex form that <c>*IDN?</c> reports
    /// (issue #493).
    /// </summary>
    /// <remarks>
    /// Only a value that is a plain base-10 number — the form of every serial Core reads from a
    /// connected device — is converted. Anything else (a USB descriptor string, a placeholder such
    /// as "Unknown", a test fixture, the hex serial of a session imported from an SD-card log) is
    /// returned unchanged. Null becomes the empty string, so a binding shows a blank cell rather
    /// than nothing at all. The text alone does not show its format, so an SD-card hex serial made
    /// only of the digits 0–9 is converted as if it were decimal; the class remarks describe it.
    /// </remarks>
    /// <param name="serial">The stored serial, normally decimal.</param>
    /// <returns>The display form of <paramref name="serial"/>.</returns>
    public static string ToDisplay(string? serial)
    {
        if (ulong.TryParse(serial, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return value.ToString("X" + HexWidth, CultureInfo.InvariantCulture);
        }

        return serial ?? string.Empty;
    }

    /// <summary>
    /// The last four characters of <see cref="ToDisplay"/>, behind an ellipsis — the compact form
    /// the legends and plot tooltips use. A display form of four characters or fewer is shown whole.
    /// </summary>
    /// <param name="serial">The stored serial, normally decimal.</param>
    /// <returns>The short display form of <paramref name="serial"/>.</returns>
    public static string ToShortDisplay(string? serial)
    {
        var display = ToDisplay(serial);
        return display.Length > ShortLength ? "..." + display[^ShortLength..] : display;
    }
}
