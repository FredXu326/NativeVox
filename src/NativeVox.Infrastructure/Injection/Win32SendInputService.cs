namespace NativeVox.Infrastructure.Injection;

using System.Runtime.InteropServices;
using System.Windows;
using Serilog;
using NativeVox.Core.Services;

public class Win32SendInputService : ITextInjectionService
{
    private string _lastInjectedText = string.Empty;
    private readonly object _lock = new();

    public event EventHandler<string>? FallbackToClipboardTriggered;

    #region Win32 API Definitions

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_BACK = 0x08;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    #endregion

    public void InjectProgressiveText(string updatedText)
    {
        if (updatedText == null) return;

        lock (_lock)
        {
            if (UIPIElevationDetector.WillUIPIBlockInjection())
            {
                Log.Warning("Target window is elevated while NativeVox is standard user. Diverting text to clipboard due to UIPI.");
                _lastInjectedText = updatedText;
                SetClipboardTextSafely(updatedText);
                FallbackToClipboardTriggered?.Invoke(this, updatedText);
                return;
            }

            var diff = DiffEngine.ComputeDiff(_lastInjectedText, updatedText);

            if (diff.BackspacesNeeded > 0)
            {
                SendBackspaces(diff.BackspacesNeeded);
            }

            if (!string.IsNullOrEmpty(diff.TextToAppend))
            {
                SendUnicodeString(diff.TextToAppend);
            }

            _lastInjectedText = updatedText;
        }
    }

    public void FinalizeSession()
    {
        lock (_lock)
        {
            _lastInjectedText = string.Empty;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _lastInjectedText = string.Empty;
        }
    }

    private static void SendBackspaces(int count)
    {
        if (count <= 0) return;

        var inputs = new INPUT[count * 2];
        int inputSize = Marshal.SizeOf<INPUT>();

        for (int i = 0; i < count; i++)
        {
            // Key down
            inputs[i * 2] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = VK_BACK,
                        wScan = 0,
                        dwFlags = 0,
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero
                    }
                }
            };

            // Key up
            inputs[i * 2 + 1] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = VK_BACK,
                        wScan = 0,
                        dwFlags = KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero
                    }
                }
            };
        }

        SendInput((uint)inputs.Length, inputs, inputSize);
    }

    private static void SendUnicodeString(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var inputs = new INPUT[text.Length * 2];
        int inputSize = Marshal.SizeOf<INPUT>();

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            // Key down
            inputs[i * 2] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero
                    }
                }
            };

            // Key up
            inputs[i * 2 + 1] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero
                    }
                }
            };
        }

        SendInput((uint)inputs.Length, inputs, inputSize);
    }

    private static void SetClipboardTextSafely(string text)
    {
        try
        {
            // Run on STA thread if current thread is MTA
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                Clipboard.SetText(text);
            }
            else
            {
                var thread = new Thread(() =>
                {
                    try { Clipboard.SetText(text); } catch { /* ignore */ }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join(500);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to copy text to clipboard as UIPI fallback.");
        }
    }
}
