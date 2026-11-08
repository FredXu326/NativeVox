namespace NativeVox.Core.Models;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

/// <summary>
/// Configuration for the global toggle hotkey.
/// </summary>
public record HotkeyConfig(
    KeyModifiers Modifiers = KeyModifiers.Control,
    int VirtualKey = 0x20 // Space
)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Windows)) parts.Add("Win");

        var keyName = VirtualKey switch
        {
            0x20 => "Space",
            0x70 => "F1",
            0x71 => "F2",
            0x72 => "F3",
            0x73 => "F4",
            0x74 => "F5",
            0x75 => "F6",
            0x76 => "F7",
            0x77 => "F8",
            0x78 => "F9",
            0x79 => "F10",
            0x7A => "F11",
            0x7B => "F12",
            _ => $"0x{VirtualKey:X2}"
        };

        parts.Add(keyName);
        return string.Join(" + ", parts);
    }
}
