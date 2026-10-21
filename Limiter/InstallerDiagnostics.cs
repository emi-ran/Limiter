using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Limiter;

// Failure-only diagnostics survive MSI rollback. No caller-selected elevated file
// writes, registry changes, or message DLLs: CI reads raw EventData, not Message.
internal static class InstallerDiagnostics
{
    internal static void ReportFailure(string operation, Exception exception)
    {
        string details = $"Limiter installer helper: {operation}\n{exception.ToString()}";
        try
        {
            details += "\n" + StartupRegistration.DescribeInstallProtectionFailure();
            Console.Error.WriteLine(details);
            IntPtr source = RegisterEventSourceW(null, "Limiter Installer");
            if (source == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                // Keep below the event service's string/record limits.
                if (details.Length > 16000) details = details[..16000] + "\n[truncated]";
                if (!ReportEventW(source, 1, 0, 1001, IntPtr.Zero, 1, 0, new[] { details }, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { DeregisterEventSource(source); }
        }
        catch (Exception diagnosticError)
        {
            // Diagnostics must never replace the helper's original failure code.
            Console.Error.WriteLine($"{details}\nEvent Log diagnostic failed: {diagnosticError}");
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr RegisterEventSourceW(string? server, string source);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReportEventW(IntPtr source, ushort type, ushort category, uint eventId,
        IntPtr userSid, ushort stringCount, uint dataSize,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 5)] string[] strings,
        IntPtr rawData);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeregisterEventSource(IntPtr source);
}
