using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Daqifi.Desktop.UpdateVersion;

namespace Daqifi.Avalonia;

/// <summary>Every process on the PC, from all sessions, with its image path where readable.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsProcesses
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>
    /// Opens each process the way Velopack's apply step does (query limited information) and reads
    /// its image path. A process this one may not open, typically another user's, keeps its name and
    /// a <c>null</c> path.
    /// </summary>
    public static IReadOnlyList<RunningProcess> Snapshot()
    {
        var result = new List<RunningProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try
                {
                    name = process.ProcessName;
                }
                catch (InvalidOperationException)
                {
                    continue; // exited since the list was taken
                }

                result.Add(new RunningProcess(process.Id, name, ImagePath(process.Id)));
            }
        }

        return result;
    }

    private static string? ImagePath(int processId)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle == IntPtr.Zero) { return null; }

        try
        {
            var buffer = new char[32768];
            var length = (uint)buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref length) ? new string(buffer, 0, (int)length) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, [Out] char[] exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
