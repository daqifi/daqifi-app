// Avalonia-only: no upstream counterpart, so no @port marker.

using System.Globalization;
using Avalonia.Data.Converters;
using Daqifi.Desktop.Device;

namespace Daqifi.Desktop.Converters;

/// <summary>
/// Shows a stored (decimal) device serial in the hex form <c>*IDN?</c> reports (issue #493).
/// </summary>
/// <remarks>
/// Every view that binds a serial goes through this, so the bound property keeps the stored value —
/// the session key and the profile identity — and only the text on screen changes. The rule itself
/// is <see cref="DeviceSerials.ToDisplay"/>; this is only its binding adapter.
/// </remarks>
public sealed class SerialDisplayConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DeviceSerials.ToDisplay(value as string);

    /// <summary>
    /// Not supported: every serial binding is one-way display text.
    /// </summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
