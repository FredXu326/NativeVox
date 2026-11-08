namespace NativeVox.Core.Models;

/// <summary>
/// Persistent application settings stored in %AppData%\NativeVox\settings.json.
/// </summary>
public class AppSettings
{
    public ModelSize SelectedModel { get; set; } = ModelSize.Base;

    public ExecutionProvider Provider { get; set; } = ExecutionProvider.Auto;

    public HotkeyConfig Hotkey { get; set; } = new(KeyModifiers.Control, 0x20); // Ctrl + Space

    public int InputDeviceIndex { get; set; } = -1; // -1 = Default recording device

    public bool DirectProgressiveTypingEnabled { get; set; } = true;

    public bool EnablePulsingHUD { get; set; } = true;

    public bool FirstRunCompleted { get; set; } = false;

    public string Language { get; set; } = "en"; // Ready for future multilingual extension
}
