namespace NativeVox.Infrastructure.Storage;

using System.IO;
using System.Text.Json;
using Serilog;
using NativeVox.Core.Models;

/// <summary>
/// Manages application data directories, paths, and persistent settings under %AppData%\NativeVox.
/// </summary>
public static class AppPathResolver
{
    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NativeVox"
    );

    public static string RootDirectory => AppDataFolder;
    public static string ModelsDirectory => Path.Combine(AppDataFolder, "models");
    public static string LogsDirectory => Path.Combine(AppDataFolder, "logs");
    public static string SettingsFilePath => Path.Combine(AppDataFolder, "settings.json");

    static AppPathResolver()
    {
        EnsureDirectoriesExist();
    }

    public static void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(AppDataFolder);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public static AppSettings LoadSettings()
    {
        EnsureDirectoriesExist();

        if (!File.Exists(SettingsFilePath))
        {
            Log.Debug("Settings file not found at {Path}; creating defaults.", SettingsFilePath);
            var defaultSettings = new AppSettings();
            SaveSettings(defaultSettings);
            return defaultSettings;
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            if (settings == null)
            {
                Log.Warning("Settings file {Path} deserialized to null; using defaults.", SettingsFilePath);
                return new AppSettings();
            }

            Log.Debug("Settings loaded: Model={Model}, Provider={Provider}, Hotkey={Hotkey}, InputDevice={Device}, ProgressiveTyping={Progressive}, HUD={Hud}, Language={Language}.",
                settings.SelectedModel, settings.Provider, settings.Hotkey, settings.InputDeviceIndex,
                settings.DirectProgressiveTypingEnabled, settings.EnablePulsingHUD, settings.Language);
            return settings;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read settings from {Path}; using defaults.", SettingsFilePath);
            return new AppSettings();
        }
    }

    public static void SaveSettings(AppSettings settings)
    {
        EnsureDirectoriesExist();
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsFilePath, json);
        Log.Debug("Settings saved to {Path}.", SettingsFilePath);
        Log.Verbose("Saved settings JSON: {Json}", json);
    }
}
