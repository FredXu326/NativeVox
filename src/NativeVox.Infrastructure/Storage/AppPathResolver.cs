namespace NativeVox.Infrastructure.Storage;

using System.IO;
using System.Text.Json;
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
            var defaultSettings = new AppSettings();
            SaveSettings(defaultSettings);
            return defaultSettings;
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            return settings ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void SaveSettings(AppSettings settings)
    {
        EnsureDirectoriesExist();
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsFilePath, json);
    }
}
