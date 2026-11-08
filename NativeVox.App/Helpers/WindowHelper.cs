namespace NativeVox.App.Helpers;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

public static class WindowHelper
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>
    /// Ensures that the window never takes keyboard focus when made visible.
    /// This prevents disrupting the user's typing in the target application.
    /// </summary>
    public static void EnableNoActivate(Window window)
    {
        var helper = new WindowInteropHelper(window);
        var hWnd = helper.EnsureHandle();

        var exStyle = (long)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
        exStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(exStyle));
    }
}
