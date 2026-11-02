using Microsoft.Win32;

namespace Daqifi.Avalonia;

/// <summary>
/// Settings an administrator sets for every user of a PC, under
/// <c>HKLM\SOFTWARE\Policies\DAQiFi</c>, typically by Group Policy (#480).
/// </summary>
/// <remarks>
/// Windows only, read once, and never throws: a missing key, a value of the wrong type, or a
/// registry that cannot be read all mean the policy is not set.
/// </remarks>
internal static class MachinePolicy
{
    private const string KEY = @"SOFTWARE\Policies\DAQiFi";

    /// <summary>
    /// <c>DisableUpdates</c> (DWORD, non-zero = on): the app neither checks for nor offers updates.
    /// For PCs where IT deploys new versions itself.
    /// </summary>
    public static bool DisableUpdates { get; } = ReadFlag("DisableUpdates");

    private static bool ReadFlag(string name)
    {
        if (!OperatingSystem.IsWindows()) { return false; }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KEY);
            return key?.GetValue(name) is int value && value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
