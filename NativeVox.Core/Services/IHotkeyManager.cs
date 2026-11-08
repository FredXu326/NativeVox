namespace NativeVox.Core.Services;

using NativeVox.Core.Models;

/// <summary>
/// Service managing global system hotkey registration and toggle events.
/// </summary>
public interface IHotkeyManager : IDisposable
{
    event EventHandler? HotkeyTriggered;

    bool Register(HotkeyConfig config, IntPtr windowHandle);

    void Unregister();

    bool IsRegistered { get; }
}
