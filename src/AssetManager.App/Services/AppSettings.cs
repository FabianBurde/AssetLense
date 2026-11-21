using System.IO;
using System.Text.Json;

namespace AssetManager.App.Services;

/// <summary>Small per-user settings file (%LOCALAPPDATA%\AssetManager\settings.json). Read before the window opens.</summary>
public sealed class AppSettings
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AssetManager", "settings.json");

    /// <summary>Null until the user toggles the theme once; then the system setting is followed on first start.</summary>
    public bool? DarkMode { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file just means defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to remember the theme is not worth interrupting the user.
        }
    }
}
