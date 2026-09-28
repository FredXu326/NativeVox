namespace NativeVox.Infrastructure.Hotkey;

using System.Runtime.InteropServices;
using System.Windows.Interop;
using Serilog;
using NativeVox.Core.Models;
using NativeVox.Core.Services;

public class Win32HotkeyManager : IHotkeyManager
{
    private const int HOTKEY_ID = 9001;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr _windowHandle = IntPtr.Zero;
    private HwndSource? _hwndSource;
    private bool _isRegistered;
    private bool _disposed;

    public event EventHandler? HotkeyTriggered;

    public bool IsRegistered => _isRegistered;

    public bool Register(HotkeyConfig config, IntPtr windowHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Unregister();

        _windowHandle = windowHandle;
        if (_windowHandle == IntPtr.Zero)
        {
            Log.Warning("Cannot register hotkey: Window handle is IntPtr.Zero.");
            return false;
        }

        uint fsModifiers = 0;
        if (config.Modifiers.HasFlag(KeyModifiers.Alt)) fsModifiers |= 0x0001;
        if (config.Modifiers.HasFlag(KeyModifiers.Control)) fsModifiers |= 0x0002;
        if (config.Modifiers.HasFlag(KeyModifiers.Shift)) fsModifiers |= 0x0004;
        if (config.Modifiers.HasFlag(KeyModifiers.Windows)) fsModifiers |= 0x0008;

        fsModifiers |= MOD_NOREPEAT;

        _hwndSource = HwndSource.FromHwnd(_windowHandle);
        _hwndSource?.AddHook(WndProc);

        _isRegistered = RegisterHotKey(_windowHandle, HOTKEY_ID, fsModifiers, (uint)config.VirtualKey);

        if (_isRegistered)
        {
            Log.Information("Global hotkey registered successfully: {Config}", config);
        }
        else
        {
            int errorCode = Marshal.GetLastWin32Error();
            Log.Error("Failed to register global hotkey {Config}. Win32 error code: {Code}", config, errorCode);
        }

        return _isRegistered;
    }

    public void Unregister()
    {
        if (_isRegistered && _windowHandle != IntPtr.Zero)
        {
            UnregisterHotKey(_windowHandle, HOTKEY_ID);
            _isRegistered = false;
        }

        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            handled = true;
            HotkeyTriggered?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
    }
}
