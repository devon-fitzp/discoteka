using System;
using System.IO;
using System.Text.Json;
using Discoteka.Core.Database;

namespace Discoteka.Desktop.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to a JSON file in the same directory as the database.
/// </summary>
public static class AppSettingsService
{
    private const string FileName = "settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Loads settings from disk, returning defaults if the file is missing or unreadable.</summary>
    public static AppSettings Load()
    {
        var path = GetSettingsPath();
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Settings] Failed to load settings from {path}: {ex.Message}. Using defaults.");
            return new AppSettings();
        }
    }

    /// <summary>Saves <paramref name="settings"/> to disk. Silently swallows I/O errors.</summary>
    public static void Save(AppSettings settings)
    {
        var path = GetSettingsPath();
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Settings] Failed to save settings to {path}: {ex.Message}");
        }
    }

    private static string GetSettingsPath()
    {
        var dbPath = DbPaths.GetDefaultDbPath();
        var dir = Path.GetDirectoryName(dbPath)!;
        return Path.Combine(dir, FileName);
    }
}
