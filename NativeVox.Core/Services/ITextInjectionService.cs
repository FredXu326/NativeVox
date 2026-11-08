namespace NativeVox.Core.Services;

/// <summary>
/// Service responsible for injecting text into the active foreground window
/// using SendInput Unicode keystrokes and diff-and-backspace progressive updates.
/// </summary>
public interface ITextInjectionService
{
    /// <summary>
    /// Progressively updates the text in the active window.
    /// Emits backspaces for revised trailing characters and emits Unicode keystrokes for additions.
    /// </summary>
    void InjectProgressiveText(string updatedText);

    /// <summary>
    /// Finalizes the current injection session and resets diff tracking state.
    /// </summary>
    void FinalizeSession();

    /// <summary>
    /// Resets the current diff state without injecting anything.
    /// </summary>
    void Reset();

    /// <summary>
    /// Event triggered when text injection fails due to Windows UIPI elevation barriers.
    /// The argument contains the text that was diverted to the Windows Clipboard.
    /// </summary>
    event EventHandler<string>? FallbackToClipboardTriggered;
}
