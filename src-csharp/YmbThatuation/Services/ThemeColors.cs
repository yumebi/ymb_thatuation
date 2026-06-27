namespace YmbThatuation.Services;

/// <summary>
/// URLバー等のネイティブWPF UIをテーマ(wwwroot/theme.jsのCSS変数)に追従させるための配色表。
/// theme.js側のテーマ名/--bg-page・--bg-chip・--bg-chip-hover・--text-primary・--accent・--borderと同じ値を持つ。
/// </summary>
public record ThemeColors(string Bar, string Button, string ButtonHover, string Text, string Accent, string Border);

public static class ThemePalette
{
    private static readonly Dictionary<string, ThemeColors> Themes = new()
    {
        ["dark"] = new("#1e1f24", "#33353d", "#41434d", "#e4e5ea", "#3a5bbf", "#3a3c44"),
        ["light"] = new("#eef0f3", "#e2e5ea", "#d2d6dd", "#22232a", "#3a5bbf", "#cfd2d8"),
        ["slate"] = new("#1a2230", "#2e3b52", "#3a4965", "#e7ecf5", "#4d8fd6", "#374763"),
        ["midnight"] = new("#0a0e1a", "#1c2240", "#262d52", "#e8eaf6", "#5468e0", "#2a3158"),
        ["forest"] = new("#121a14", "#243524", "#2f452f", "#e6ecdf", "#4f9d5e", "#344a34"),
        ["solarized-dark"] = new("#002b36", "#0d4654", "#115163", "#eee8d5", "#cb4b16", "#0d4654"),
        ["solarized-light"] = new("#fdf6e3", "#e3dcc6", "#d8d0b9", "#073642", "#cb4b16", "#d3cbb2"),
        ["high-contrast"] = new("#000000", "#1f1f1f", "#2e2e2e", "#ffffff", "#3d8bff", "#4a4a4a"),
        ["sepia"] = new("#2b2017", "#4d3e2a", "#5c4b34", "#f0e3cc", "#c98a3e", "#5c4b34"),
        ["ocean"] = new("#0c2027", "#1a404d", "#214d5c", "#def4f5", "#2ab6c4", "#1f4d59"),
    };

    public static ThemeColors Get(string name) => Themes.TryGetValue(name, out var c) ? c : Themes["dark"];
}
