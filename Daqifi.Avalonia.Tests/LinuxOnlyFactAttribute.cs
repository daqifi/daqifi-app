using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> for a row that can only run on Linux, which the runner reports
/// as <b>Skipped</b>, with the reason given, instead of silently as Passed.
/// <see cref="UnixOnlyFactAttribute"/> explains the mechanism (a skip decided at discovery time,
/// because xunit 2.9.3 has no dynamic skip) and why the reason belongs to the row.
/// </summary>
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    /// <param name="reason">Why this row cannot run on Windows or macOS, in the row's own terms.
    /// Shown by the runner against the skipped row and carried into the TRX.</param>
    public LinuxOnlyFactAttribute(string reason)
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = reason;
        }
    }
}
