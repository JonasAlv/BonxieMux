using Avalonia.Media;
using Skua.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Skua.Shared.Avalonia.Services;

public sealed record VsCodeThemePalette(
    string Name,
    bool IsDark,
    Color Page,
    Color Surface,
    Color SurfaceAlt,
    Color SurfaceSubtle,
    Color NavHover,
    Color Pressed,
    Color Selection,
    Color Border,
    Color BorderStrong,
    Color WindowBorder,
    Color Text,
    Color MutedText,
    Color Accent,
    Color AccentForeground,
    Color AccentHover,
    Color AccentPressed,
    Color Danger,
    Color Warning,
    Color Success,
    Color ScrollThumb,
    Color ScrollThumbHover,
    Color ScrollThumbPressed);

public static class VsCodeThemeLoader
{
    public const string DarkModernName = "Dark Modern";
    public const string LightModernName = "Light Modern";

    public static VsCodeThemePalette DarkModern { get; } = new(
        Name: DarkModernName,
        IsDark: true,
        Page: Color.Parse("#1F1F1F"),
        Surface: Color.Parse("#181818"),
        SurfaceAlt: Color.Parse("#252526"),
        SurfaceSubtle: Color.Parse("#2B2B2B"),
        NavHover: Color.Parse("#2A2D2E"),
        Pressed: Color.Parse("#37373D"),
        Selection: Color.Parse("#400078D4"),
        Border: Color.Parse("#3C3C3C"),
        BorderStrong: Color.Parse("#454545"),
        WindowBorder: Color.Parse("#454545"),
        Text: Color.Parse("#CCCCCC"),
        MutedText: Color.Parse("#9D9D9D"),
        Accent: Color.Parse("#0078D4"),
        AccentForeground: Color.Parse("#FFFFFF"),
        AccentHover: Color.Parse("#026EC1"),
        AccentPressed: Color.Parse("#005FB8"),
        Danger: Color.Parse("#F85149"),
        Warning: Color.Parse("#FBBF24"),
        Success: Color.Parse("#34D399"),
        ScrollThumb: Color.Parse("#424242"),
        ScrollThumbHover: Color.Parse("#5A5A5A"),
        ScrollThumbPressed: Color.Parse("#757575"));

    public static VsCodeThemePalette LightModern { get; } = new(
        Name: LightModernName,
        IsDark: false,
        Page: Color.Parse("#FFFFFF"),
        Surface: Color.Parse("#F8F8F8"),
        SurfaceAlt: Color.Parse("#F3F3F3"),
        SurfaceSubtle: Color.Parse("#EBEBEB"),
        NavHover: Color.Parse("#E8E8E8"),
        Pressed: Color.Parse("#D6D6D6"),
        Selection: Color.Parse("#33005FB8"),
        Border: Color.Parse("#CECECE"),
        BorderStrong: Color.Parse("#B4B4B4"),
        WindowBorder: Color.Parse("#B4B4B4"),
        Text: Color.Parse("#1F1F1F"),
        MutedText: Color.Parse("#616161"),
        Accent: Color.Parse("#005FB8"),
        AccentForeground: Color.Parse("#FFFFFF"),
        AccentHover: Color.Parse("#0258A8"),
        AccentPressed: Color.Parse("#004C94"),
        Danger: Color.Parse("#E51400"),
        Warning: Color.Parse("#B45309"),
        Success: Color.Parse("#059669"),
        ScrollThumb: Color.Parse("#C1C1C1"),
        ScrollThumbHover: Color.Parse("#9E9E9E"),
        ScrollThumbPressed: Color.Parse("#757575"));

    public static Color GetContrastForeground(Color bg)
    {
        double luminance = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
        return luminance > 0.58 ? Color.Parse("#1F1F1F") : Color.Parse("#FFFFFF");
    }

    public static IReadOnlyList<string> DiscoverAvailableThemeNames()
    {
        List<string> names = [DarkModernName, LightModernName];
        HashSet<string> seen = new(names, StringComparer.OrdinalIgnoreCase);

        foreach ((string name, _) in DiscoverThemeFiles())
        {
            if (seen.Add(name))
                names.Add(name);
        }

        return names;
    }

    public static (IReadOnlyList<string> DarkThemes, IReadOnlyList<string> LightThemes) DiscoverThemesByKind()
    {
        List<string> dark = [DarkModernName];
        List<string> light = [LightModernName];
        HashSet<string> seenDark = new(dark, StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenLight = new(light, StringComparer.OrdinalIgnoreCase);

        foreach ((string name, string path) in DiscoverThemeFiles())
        {
            if (TryLoadFromFile(path, name, out VsCodeThemePalette? pal) && pal is not null)
            {
                if (pal.IsDark)
                {
                    if (seenDark.Add(name))
                        dark.Add(name);
                }
                else
                {
                    if (seenLight.Add(name))
                        light.Add(name);
                }
            }
        }

        return (dark, light);
    }

    public static VsCodeThemePalette LoadByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, DarkModernName, StringComparison.OrdinalIgnoreCase))
            return DarkModern;
        if (string.Equals(name, LightModernName, StringComparison.OrdinalIgnoreCase))
            return LightModern;

        foreach ((string themeName, string path) in DiscoverThemeFiles())
        {
            if (string.Equals(themeName, name, StringComparison.OrdinalIgnoreCase))
            {
                if (TryLoadFromFile(path, themeName, out VsCodeThemePalette? palette) && palette is not null)
                    return palette;
            }
        }

        return DarkModern;
    }

    public static string GetUserThemesDirectory()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string configRoot = !string.IsNullOrWhiteSpace(xdg)
            ? xdg
            : Path.Combine(home, ".config");
        return Path.Combine(configRoot, "bonxiemux", "themes");
    }

    private static IEnumerable<(string Name, string Path)> DiscoverThemeFiles()
    {
        string[] dirs =
        [
            GetUserThemesDirectory(),
            Path.Combine(ClientFileSources.SkuaDIR, "themes")
        ];

        foreach (string dir in dirs.Where(Directory.Exists))
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*.json*", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(Path.GetFileName(file), "package.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return (ReadThemeDisplayName(file), file);
            }
        }
    }

    private static string ReadThemeDisplayName(string path)
    {
        try
        {
            string clean = StripJsonCommentsAndTrailingCommas(File.ReadAllText(path));
            using JsonDocument doc = JsonDocument.Parse(clean);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("name", out JsonElement nameEl) &&
                nameEl.ValueKind == JsonValueKind.String)
            {
                string? val = nameEl.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                    return val.Trim();
            }
        }
        catch { }

        return Path.GetFileNameWithoutExtension(path);
    }

    private static bool TryLoadFromFile(string path, string displayName, out VsCodeThemePalette? palette)
    {
        palette = null;
        try
        {
            Dictionary<string, string> colors = new(StringComparer.OrdinalIgnoreCase);
            string? themeType = null;
            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
            MergeThemeFile(path, colors, ref themeType, visited);

            if (colors.Count == 0)
                return false;

            Color editorBg = TryGetColor(colors, "editor.background") ?? DarkModern.Page;
            bool isDark = themeType?.Equals("light", StringComparison.OrdinalIgnoreCase) == true
                ? false
                : themeType?.Equals("dark", StringComparison.OrdinalIgnoreCase) == true
                    ? true
                    : (0.299 * editorBg.R + 0.587 * editorBg.G + 0.114 * editorBg.B) / 255.0 < 0.5;

            VsCodeThemePalette fallback = isDark ? DarkModern : LightModern;

            Color page = FirstColor(colors, fallback.Page, "editor.background");
            Color surface = FirstColor(colors, fallback.Surface, "sideBar.background", "titleBar.activeBackground", "activityBar.background", "tab.inactiveBackground");
            Color surfaceAlt = FirstColor(colors, fallback.SurfaceAlt, "input.background", "dropdown.background", "editorWidget.background");
            Color surfaceSubtle = FirstColor(colors, fallback.SurfaceSubtle, "button.secondaryBackground", "badge.background", "tab.hoverBackground");
            Color navHover = FirstColor(colors, fallback.NavHover, "list.hoverBackground", "toolbar.hoverBackground", "menubar.selectionBackground");
            Color pressed = FirstColor(colors, fallback.Pressed, "list.activeSelectionBackground", "list.focusBackground");
            Color border = FirstColor(colors, fallback.Border, "panel.border", "sideBar.border", "input.border", "tab.border", "contrastBorder");
            Color borderStrong = FirstColor(colors, fallback.BorderStrong, "menu.border", "widget.border", "titleBar.border", "panel.border");
            Color windowBorder = FirstColor(colors, borderStrong, "contrastBorder", "window.activeBorder", "menu.border");
            Color text = FirstColor(colors, fallback.Text, "foreground", "editor.foreground", "sideBar.foreground");
            Color mutedText = FirstColor(colors, fallback.MutedText, "descriptionForeground", "tab.inactiveForeground", "disabledForeground");
            Color accent = FirstColor(colors, fallback.Accent, "button.background", "focusBorder", "activityBarBadge.background", "textLink.foreground");
            Color accentFg = TryGetColor(colors, "button.foreground") ?? GetContrastForeground(accent);
            Color accentHover = TryGetColor(colors, "button.hoverBackground") ?? ShiftColor(accent, isDark ? 0.10 : -0.10);
            Color accentPressed = ShiftColor(accent, -0.12);
            Color selection = TryGetColor(colors, "list.activeSelectionBackground")
                ?? Color.FromArgb((byte)(isDark ? 0x40 : 0x33), accent.R, accent.G, accent.B);
            Color danger = FirstColor(colors, fallback.Danger, "errorForeground", "editorError.foreground");
            Color warning = FirstColor(colors, fallback.Warning, "editorWarning.foreground", "list.warningForeground");
            Color success = FirstColor(colors, fallback.Success, "gitDecoration.addedResourceForeground", "terminal.ansiGreen");
            Color scrollThumb = FirstColor(colors, fallback.ScrollThumb, "scrollbarSlider.background");
            Color scrollThumbHover = FirstColor(colors, fallback.ScrollThumbHover, "scrollbarSlider.hoverBackground");
            Color scrollThumbPressed = FirstColor(colors, fallback.ScrollThumbPressed, "scrollbarSlider.activeBackground");

            palette = new VsCodeThemePalette(
                displayName,
                isDark,
                page,
                surface,
                surfaceAlt,
                surfaceSubtle,
                navHover,
                pressed,
                selection,
                border,
                borderStrong,
                windowBorder,
                text,
                mutedText,
                accent,
                accentFg,
                accentHover,
                accentPressed,
                danger,
                warning,
                success,
                scrollThumb,
                scrollThumbHover,
                scrollThumbPressed);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void MergeThemeFile(string path, Dictionary<string, string> colors, ref string? themeType, HashSet<string> visited)
    {
        string full = Path.GetFullPath(path);
        if (!visited.Add(full) || !File.Exists(full))
            return;

        string clean = StripJsonCommentsAndTrailingCommas(File.ReadAllText(full));
        using JsonDocument doc = JsonDocument.Parse(clean);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return;

        if (doc.RootElement.TryGetProperty("include", out JsonElement incEl) && incEl.ValueKind == JsonValueKind.String)
        {
            string? incRel = incEl.GetString();
            if (!string.IsNullOrWhiteSpace(incRel))
            {
                string? dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir))
                    MergeThemeFile(Path.Combine(dir, incRel), colors, ref themeType, visited);
            }
        }

        if (doc.RootElement.TryGetProperty("type", out JsonElement typeEl) && typeEl.ValueKind == JsonValueKind.String)
            themeType = typeEl.GetString();

        if (doc.RootElement.TryGetProperty("colors", out JsonElement colorsEl) && colorsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in colorsEl.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    string? hex = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(hex))
                        colors[prop.Name] = hex.Trim();
                }
            }
        }
    }

    private static Color FirstColor(Dictionary<string, string> colors, Color fallback, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (TryGetColor(colors, key) is { } c)
                return c;
        }
        return fallback;
    }

    private static Color? TryGetColor(Dictionary<string, string> colors, string key)
    {
        if (!colors.TryGetValue(key, out string? hex) || string.IsNullOrWhiteSpace(hex))
            return null;

        string s = hex.Trim();
        if (!s.StartsWith('#'))
            return null;

        // VS Code uses #RRGGBBAA; Avalonia Color.TryParse uses #AARRGGBB.
        if (s.Length == 9)
        {
            string rrggbb = s.Substring(1, 6);
            string aa = s.Substring(7, 2);
            s = $"#{aa}{rrggbb}";
        }

        return Color.TryParse(s, out Color parsed) ? parsed : null;
    }

    private static Color ShiftColor(Color c, double amount)
    {
        if (amount >= 0)
        {
            return Color.FromArgb(
                c.A,
                (byte)Math.Clamp(c.R + (255 - c.R) * amount, 0, 255),
                (byte)Math.Clamp(c.G + (255 - c.G) * amount, 0, 255),
                (byte)Math.Clamp(c.B + (255 - c.B) * amount, 0, 255));
        }

        double f = 1.0 + amount;
        return Color.FromArgb(
            c.A,
            (byte)Math.Clamp(c.R * f, 0, 255),
            (byte)Math.Clamp(c.G * f, 0, 255),
            (byte)Math.Clamp(c.B * f, 0, 255));
    }

    private static string StripJsonCommentsAndTrailingCommas(string input)
    {
        StringBuilder sb = new(input.Length);
        bool inString = false;
        bool escape = false;
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (inString)
            {
                sb.Append(c);
                if (escape)
                    escape = false;
                else if (c == '\\')
                    escape = true;
                else if (c == '"')
                    inString = false;
                i++;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                i++;
                continue;
            }

            if (c == '/' && i + 1 < input.Length && input[i + 1] == '/')
            {
                i += 2;
                while (i < input.Length && input[i] != '\n')
                    i++;
                continue;
            }

            if (c == '/' && i + 1 < input.Length && input[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < input.Length && !(input[i] == '*' && input[i + 1] == '/'))
                    i++;
                i += 2;
                continue;
            }

            if (c == ',')
            {
                int j = i + 1;
                while (j < input.Length && char.IsWhiteSpace(input[j]))
                    j++;
                if (j < input.Length && (input[j] == '}' || input[j] == ']'))
                {
                    i++;
                    continue;
                }
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
