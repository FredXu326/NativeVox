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

    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12; // Alt
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;

    // Explicit 40-byte 64-bit layout. On Windows x64:
    // 'type' is at offset 0 (4 bytes) + 4 bytes alignment padding = 8.
    // 'ki' begins at offset 8. Total size is exactly 40 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)]
        public uint type;

        [FieldOffset(8)]
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
    private static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray)] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static readonly ushort[] ModifiersToCheck = { VK_CONTROL, VK_SHIFT, VK_MENU, VK_LWIN, VK_RWIN };

    private static List<ushort> GetHeldModifiers()
    {
        var held = new List<ushort>();
        foreach (var vk in ModifiersToCheck)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                held.Add(vk);
            }
        }
        return held;
    }

    private static string DescribeModifiers(List<ushort> modifiers)
    {
        var names = modifiers.Select(vk => vk switch
        {
            VK_CONTROL => "Ctrl",
            VK_SHIFT => "Shift",
            VK_MENU => "Alt",
            VK_LWIN or VK_RWIN => "Win",
            _ => $"0x{vk:X}"
        });
        return string.Join("+", names);
    }

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

            if (diff.BackspacesNeeded == 0 && string.IsNullOrEmpty(diff.TextToAppend))
            {
                Log.Verbose("Injection skipped: text unchanged ({Length} chars).", updatedText.Length);
                return;
            }

            Log.Debug("Injecting into {Target}: {Backspaces} backspace(s), append \"{Append}\".",
                UIPIElevationDetector.DescribeForegroundWindow(), diff.BackspacesNeeded, diff.TextToAppend);

            SendDiffSafely(diff.BackspacesNeeded, diff.TextToAppend);

            _lastInjectedText = updatedText;
        }
    }

    public void FinalizeSession()
    {
        lock (_lock)
        {
            Log.Debug("Injection session finalized ({Length} chars typed in total).", _lastInjectedText.Length);
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

    /// <summary>
    /// Atomically releases any physically held modifier keys (e.g. Ctrl from the Ctrl+Space hotkey),
    /// executes backspaces, and inputs new Unicode characters in a single ordered SendInput operation.
    /// This prevents Ctrl+Backspace (which deletes whole words instead of characters) and accidental shortcuts.
    /// </summary>
    private static void SendDiffSafely(int backspacesNeeded, string textToAppend)
    {
        var heldModifiers = GetHeldModifiers();
        if (heldModifiers.Count > 0)
        {
            Log.Debug("Active modifier keys detected ({Modifiers}); releasing them prior to input injection.",
                DescribeModifiers(heldModifiers));
        }

        int modifierReleases = heldModifiers.Count;
        int backspaceEvents = Math.Max(0, backspacesNeeded) * 2;
        int textEvents = (textToAppend?.Length ?? 0) * 2;
        int totalEvents = modifierReleases + backspaceEvents + textEvents;

        if (totalEvents == 0) return;

        var inputs = new INPUT[totalEvents];
        int idx = 0;

        // 1. Force release any held modifier keys
        foreach (var modVk in heldModifiers)
        {
            inputs[idx++] = new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT
                {
                    wVk = modVk,
                    wScan = 0,
                    dwFlags = KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            };
        }

        // 2. Backspaces (key down + key up for each)
        for (int i = 0; i < backspacesNeeded; i++)
        {
            inputs[idx++] = new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT
                {
                    wVk = VK_BACK,
                    wScan = 0,
                    dwFlags = 0,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            };

            inputs[idx++] = new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT
                {
                    wVk = VK_BACK,
                    wScan = 0,
                    dwFlags = KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            };
        }

        // 3. Unicode characters (key down + key up for each UTF-16 character)
        if (!string.IsNullOrEmpty(textToAppend))
        {
            for (int i = 0; i < textToAppend.Length; i++)
            {
                char c = textToAppend[i];

                inputs[idx++] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero
                    }
                };

                inputs[idx++] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero
                    }
                };
            }
        }

        int inputSize = Marshal.SizeOf<INPUT>();
        uint sent = SendInput((uint)inputs.Length, inputs, inputSize);

        if (sent != inputs.Length)
        {
            int error = Marshal.GetLastWin32Error();
            Log.Warning("SendInput failed to inject all events: sent {Sent}/{Total}. Win32 error: {Error}, cbSize: {Size}",
                sent, inputs.Length, error, inputSize);
        }
        else
        {
            Log.Verbose("SendInput successfully injected {Sent} events ({Backspaces} backspaces, {Chars} chars).",
                sent, backspacesNeeded, textToAppend?.Length ?? 0);
        }
    }

    private static void SetClipboardTextSafely(string text)
    {
        try
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                Clipboard.SetText(text);
            }
            else
            {
                var thread = new Thread(() =>
                {
                    try { Clipboard.SetText(text); }
                    catch (Exception ex) { Log.Warning(ex, "Clipboard.SetText failed on STA helper thread."); }
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
