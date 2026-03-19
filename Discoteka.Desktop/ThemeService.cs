using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;

namespace Discoteka.Desktop;

public static class ThemeService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Discoteka.Desktop",
        "settings.json");

    private static readonly string[] BrushKeys = ["AppBg", "PanelBg", "PanelBgAlt", "PanelBorder", "TextPrimary", "TextMuted", "Accent"];

    public static void ApplyTheme(Window window, ThemeDefinition theme)
    {
        var values = new[] { theme.AppBg, theme.PanelBg, theme.PanelBgAlt, theme.PanelBorder, theme.TextPrimary, theme.TextMuted, theme.Accent };
        for (var i = 0; i < BrushKeys.Length; i++)
            window.Resources[BrushKeys[i]] = new SolidColorBrush(Color.Parse(values[i]));
    }

    public static ThemeDefinition LoadPreference()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("theme", out var prop))
                {
                    var name = prop.GetString();
                    var match = ThemeDefinition.All.FirstOrDefault(t => t.Name == name);
                    if (match != null) return match;
                }
            }
        }
        catch { /* fall through to default */ }

        return ThemeDefinition.All[0]; // Midnight
    }

    public static void SavePreference(string themeName)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(new { theme = themeName });
            File.WriteAllText(SettingsPath, json);
        }
        catch { /* best-effort */ }
    }
}
