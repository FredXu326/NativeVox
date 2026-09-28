namespace NativeVox.Infrastructure.Injection;

using System.Runtime.InteropServices;
using System.Security.Principal;

public static class UIPIElevationDetector
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;

    private enum TOKEN_INFORMATION_CLASS
    {
        TokenElevation = 20
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, TOKEN_INFORMATION_CLASS tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Returns true if the current NativeVox process is running with Administrator elevation.
    /// </summary>
    public static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Checks whether the current foreground target window belongs to an elevated process.
    /// If target is elevated and current process is NOT elevated, SendInput will be blocked by UIPI.
    /// </summary>
    public static bool IsForegroundWindowElevated()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return false;

        GetWindowThreadProcessId(hWnd, out var processId);
        if (processId == 0 || processId == (uint)Environment.ProcessId) return false;

        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero)
        {
            // If we cannot even open the process handle with limited query, it's often a protected or elevated system process
            return true;
        }

        try
        {
            if (!OpenProcessToken(hProcess, TOKEN_QUERY, out var hToken))
            {
                return true;
            }

            try
            {
                int elevationSize = Marshal.SizeOf<TOKEN_ELEVATION>();
                IntPtr elevationPtr = Marshal.AllocHGlobal(elevationSize);
                try
                {
                    if (GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenElevation, elevationPtr, (uint)elevationSize, out _))
                    {
                        var elevation = Marshal.PtrToStructure<TOKEN_ELEVATION>(elevationPtr);
                        return elevation.TokenIsElevated != 0;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(elevationPtr);
                }
            }
            finally
            {
                CloseHandle(hToken);
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }

        return false;
    }

    /// <summary>
    /// Returns true if UIPI barrier will block synthetic keystrokes to the foreground window.
    /// </summary>
    public static bool WillUIPIBlockInjection()
    {
        return !IsCurrentProcessElevated() && IsForegroundWindowElevated();
    }
}
