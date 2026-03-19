using System.Collections.Generic;

namespace Discoteka.Desktop;

public sealed record ThemeDefinition(
    string Name,
    string AppBg,
    string PanelBg,
    string PanelBgAlt,
    string PanelBorder,
    string TextPrimary,
    string TextMuted,
    string Accent)
{
    public static readonly IReadOnlyList<ThemeDefinition> All = new[]
    {
        new ThemeDefinition("Midnight", "#1D222A", "#242A33", "#1B2028", "#2C333D", "#E6E9EF", "#94A0B2", "#4C8DFF"),
        new ThemeDefinition("Obsidian", "#111114", "#191920", "#0D0D12", "#28282F", "#F0F0F5", "#888899", "#E8A020"),
        new ThemeDefinition("Forest",   "#131C17", "#1C2920", "#101810", "#2A3B2E", "#DCE8DF", "#7FA38A", "#4CAF7D"),
        new ThemeDefinition("Rose",     "#1C1820", "#251E2C", "#17131D", "#352A3E", "#EDE6F5", "#9E88B5", "#E06BBF"),
    };
}
