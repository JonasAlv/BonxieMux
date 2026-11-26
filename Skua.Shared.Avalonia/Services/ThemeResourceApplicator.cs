using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using System;

namespace Skua.Shared.Avalonia.Services;

public static class ThemeResourceApplicator
{
    public static void ApplyTitleBarTheme(Application app, bool isDark)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyTitleBarTheme(app, isDark));
            return;
        }

        ThemeVariant targetVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (app.RequestedThemeVariant != targetVariant)
            app.RequestedThemeVariant = targetVariant;

        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (Window window in desktop.Windows.ToArray())
            {
                ApplyTitleBarTheme(window, isDark);
            }
        }
    }

    public static void ApplyTitleBarTheme(Window window, bool? isDarkTheme = null)
    {
        WindowResizeHelper.Attach(window);

        bool isDark = isDarkTheme 
            ?? (Application.Current?.RequestedThemeVariant == ThemeVariant.Light ? false :
               (Application.Current?.RequestedThemeVariant == ThemeVariant.Dark ? true :
                window.ActualThemeVariant == ThemeVariant.Dark));

        ThemeVariant targetVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (window.RequestedThemeVariant != targetVariant)
            window.RequestedThemeVariant = targetVariant;
    }

    public static void ApplyAccentBrushes(
        Application app,
        Color primary,
        Color primaryForeground,
        Color? secondary = null,
        Color? secondaryForeground = null,
        bool? isDarkTheme = null,
        Color? accentHover = null,
        Color? accentPressed = null)
    {
        SetOrUpdateColor(app.Resources, "SkuaAccentColor", primary);
        SetOrUpdateColor(app.Resources, "SkuaAccentForegroundColor", primaryForeground);
        SetOrUpdateBrush(app.Resources, "SkuaAccentBrush", primary);
        SetOrUpdateBrush(app.Resources, "SkuaAccentForegroundBrush", primaryForeground);
        SetOrUpdateBrush(app.Resources, "SkuaAccentHoverBrush", accentHover ?? Lighten(primary, 0.1));
        SetOrUpdateBrush(app.Resources, "SkuaAccentPressedBrush", accentPressed ?? Darken(primary, 0.12));

        ApplyFluentAccent(app, primary, primaryForeground);

        Color selectionLight = Color.FromArgb(0x33, primary.R, primary.G, primary.B);
        Color selectionDark = Color.FromArgb(0x66, primary.R, primary.G, primary.B);
        bool useDarkSelection = isDarkTheme ?? false;
        SetOrUpdateBrush(app.Resources, "SkuaSelectionBrush", useDarkSelection ? selectionDark : selectionLight);

        SetOrUpdateBrush(app.Resources, "SkuaAccentTintBrush", Color.FromArgb(0x24, primary.R, primary.G, primary.B));
        SetOrUpdateBrush(app.Resources, "SkuaAccentTintStrongBrush", Color.FromArgb(0x38, primary.R, primary.G, primary.B));

        Color tagBg = Color.FromArgb(0x22, primary.R, primary.G, primary.B);
        Color tagBgHover = Color.FromArgb(0x3B, primary.R, primary.G, primary.B);
        Color tagBorder = Color.FromArgb(0x4D, primary.R, primary.G, primary.B);
        Color tagBorderHover = Color.FromArgb(0x80, primary.R, primary.G, primary.B);
        Color tagText = (isDarkTheme ?? true) ? Lighten(primary, 0.18) : Darken(primary, 0.18);

        SetOrUpdateBrush(app.Resources, "SkuaTagBackgroundBrush", tagBg);
        SetOrUpdateBrush(app.Resources, "SkuaTagBackgroundHoverBrush", tagBgHover);
        SetOrUpdateBrush(app.Resources, "SkuaTagBorderBrush", tagBorder);
        SetOrUpdateBrush(app.Resources, "SkuaTagBorderHoverBrush", tagBorderHover);
        SetOrUpdateBrush(app.Resources, "SkuaTagTextBrush", tagText);

        if (app.Resources.ThemeDictionaries.TryGetValue(ThemeVariant.Light, out IThemeVariantProvider? selLightProvider) && selLightProvider is IResourceDictionary selLightDict)
        {
            SetOrUpdateBrush(selLightDict, "SkuaSelectionBrush", selectionLight);
            SetOrUpdateBrush(selLightDict, "SkuaTagBackgroundBrush", Color.FromArgb(0x1F, primary.R, primary.G, primary.B));
            SetOrUpdateBrush(selLightDict, "SkuaTagBackgroundHoverBrush", Color.FromArgb(0x35, primary.R, primary.G, primary.B));
            SetOrUpdateBrush(selLightDict, "SkuaTagBorderBrush", Color.FromArgb(0x45, primary.R, primary.G, primary.B));
            SetOrUpdateBrush(selLightDict, "SkuaTagBorderHoverBrush", Color.FromArgb(0x75, primary.R, primary.G, primary.B));
            SetOrUpdateBrush(selLightDict, "SkuaTagTextBrush", Darken(primary, 0.20));
        }
        if (app.Resources.ThemeDictionaries.TryGetValue(ThemeVariant.Dark, out IThemeVariantProvider? selDarkProvider) && selDarkProvider is IResourceDictionary selDarkDict)
        {
            SetOrUpdateBrush(selDarkDict, "SkuaSelectionBrush", selectionDark);
            SetOrUpdateBrush(selDarkDict, "SkuaTagBackgroundBrush", Color.FromArgb(0x22, primary.R, primary.G, primary.B));
            SetOrUpdateBrush(selDarkDict, "SkuaTagBackgroundHoverBrush", Color.FromArgb(0x3B, primary.R, primary.G, primary.B));
            SetOrUpdateBrush(selDarkDict, "SkuaTagBorderBrush", Color.FromArgb(0x4D, primary.R, primary.G, primary.B));
            SetOrUpdateBrush(selDarkDict, "SkuaTagBorderHoverBrush", Color.FromArgb(0x80, primary.R, primary.G, primary.B));
            SetOrUpdateBrush(selDarkDict, "SkuaTagTextBrush", Lighten(primary, 0.18));
        }
    }

    public static void ApplyAccentBrushes(
        Application app,
        string? accentHex = null,
        string? accentForegroundHex = null,
        string? secondaryHex = null,
        string? secondaryForegroundHex = null,
        string fallbackAccentHex = "#0078D4",
        bool? isDarkTheme = null)
    {
        Color primary = ParseColorSafe(accentHex, GetResourceColor(app, "SkuaAccentColor", fallbackAccentHex));
        Color primaryForeground = ParseColorSafe(accentForegroundHex, GetResourceColor(app, "SkuaAccentForegroundColor", "#FFFFFFFF"));
        Color secondary = ParseColorSafe(secondaryHex, primary);
        Color secondaryForeground = ParseColorSafe(secondaryForegroundHex, primaryForeground);

        ApplyAccentBrushes(app, primary, primaryForeground, secondary, secondaryForeground, isDarkTheme);
    }

    private static readonly string[] FluentOnAccentBrushKeys =
    [
        "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed",
        "CheckBoxCheckGlyphForegroundIndeterminate", "CheckBoxCheckGlyphForegroundIndeterminatePointerOver", "CheckBoxCheckGlyphForegroundIndeterminatePressed",
        "RadioButtonCheckGlyphFill", "RadioButtonCheckGlyphFillPointerOver", "RadioButtonCheckGlyphFillPressed",
        "ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonForegroundCheckedPressed",
        "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed"
    ];

    private static void ApplyFluentAccent(Application app, Color primary, Color primaryForeground)
    {
        foreach (string key in FluentOnAccentBrushKeys)
            SetOrUpdateBrush(app.Resources, key, primaryForeground);

        SetOrUpdateColor(app.Resources, "SystemAccentColor", primary);
        SetOrUpdateColor(app.Resources, "SystemAccentColorLight1", Lighten(primary, 0.1));
        SetOrUpdateColor(app.Resources, "SystemAccentColorLight2", Lighten(primary, 0.2));
        SetOrUpdateColor(app.Resources, "SystemAccentColorLight3", Lighten(primary, 0.3));
        SetOrUpdateColor(app.Resources, "SystemAccentColorDark1", Darken(primary, 0.15));
        SetOrUpdateColor(app.Resources, "SystemAccentColorDark2", Darken(primary, 0.3));
        SetOrUpdateColor(app.Resources, "SystemAccentColorDark3", Darken(primary, 0.45));
    }

    private static void SetOrUpdateBrush(IResourceDictionary dict, string key, Color color)
    {
        if (dict.TryGetValue(key, out object? existing) && existing is SolidColorBrush brush)
        {
            if (brush.Color != color)
                brush.Color = color;
        }
        else
        {
            dict[key] = new SolidColorBrush(color);
        }
    }

    private static void SetOrUpdateColor(IResourceDictionary dict, string key, Color color)
    {
        if (dict.TryGetValue(key, out object? existing) && existing is Color c && c == color)
            return;

        dict[key] = color;
    }

    private static Color Lighten(Color color, double amount)
    {
        byte L(byte c) => (byte)Math.Clamp(c + (255 - c) * amount, 0, 255);
        return Color.FromArgb(color.A, L(color.R), L(color.G), L(color.B));
    }

    private static Color Darken(Color color, double amount)
    {
        byte D(byte c) => (byte)Math.Clamp(c * (1 - amount), 0, 255);
        return Color.FromArgb(color.A, D(color.R), D(color.G), D(color.B));
    }

    public static void ApplyVsCodePalette(Application app, VsCodeThemePalette palette)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyVsCodePalette(app, palette));
            return;
        }

        ThemeVariant targetVariant = palette.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        SetOrUpdateThemeBrush(app, targetVariant, "SkuaPageBrush", palette.Page);
        SetOrUpdateThemeBrush(app, targetVariant, "SkuaSurfaceBrush", palette.Surface);
        SetOrUpdateThemeBrush(app, targetVariant, "SkuaSurfaceAltBrush", palette.SurfaceAlt);
        SetOrUpdateThemeBrush(app, targetVariant, "SkuaSurfaceSubtleBrush", palette.SurfaceSubtle);
        SetOrUpdateThemeBrush(app, targetVariant, "SkuaNavHoverBrush", palette.NavHover);
        SetOrUpdateThemeBrush(app, targetVariant, "SkuaPressedBrush", palette.Pressed);
        SetOrUpdateThemeBrush(app, targetVariant, "SkuaSelectionBrush", palette.Selection);
        SetOrUpdateThemeBrush(app, targetVariant, "SkuaBorderBrush", palette.Border);
        SetOrUpdateThemeBrush(app, targetVariant, "SkuaBorderStrongBrush", palette.BorderStrong);
        SetOrUpdateThemeBrush(app, targetVariant, "SkuaWindowBorderBrush", palette.WindowBorder);
        SetOrUpdateThemeBrush(app, targetVariant, "SkuaTextBrush", palette.Text);
        SetOrUpdateThemeBrush(app, targetVariant, "SkuaMutedTextBrush", palette.MutedText);
        SetOrUpdateThemeBrush(app, targetVariant, "SkuaSuccessBrush", palette.Success);
        SetOrUpdateThemeBrush(app, targetVariant, "SkuaWarningBrush", palette.Warning);
        SetOrUpdateThemeBrush(app, targetVariant, "SkuaScrollThumbBrush", palette.ScrollThumb);
        SetOrUpdateThemeBrush(app, targetVariant, "SkuaScrollThumbHoverBrush", palette.ScrollThumbHover);
        SetOrUpdateThemeBrush(app, targetVariant, "SkuaScrollThumbPressedBrush", palette.ScrollThumbPressed);

        SetOrUpdateBrush(app.Resources, "SkuaDangerBrush", palette.Danger);
        SetOrUpdateBrush(app.Resources, "SkuaDangerHoverBrush", Darken(palette.Danger, 0.12));
        SetOrUpdateBrush(app.Resources, "SkuaDangerPressedBrush", Darken(palette.Danger, 0.22));
        SetOrUpdateBrush(app.Resources, "SkuaDangerForegroundBrush", VsCodeThemeLoader.GetContrastForeground(palette.Danger));
        SetOrUpdateBrush(app.Resources, "SkuaDangerTintBrush", Color.FromArgb(0x1F, palette.Danger.R, palette.Danger.G, palette.Danger.B));
        SetOrUpdateBrush(app.Resources, "SkuaDangerTintStrongBrush", Color.FromArgb(0x33, palette.Danger.R, palette.Danger.G, palette.Danger.B));
        SetOrUpdateBrush(app.Resources, "SkuaDangerSoftBorderBrush", Color.FromArgb(0x73, palette.Danger.R, palette.Danger.G, palette.Danger.B));
        SetOrUpdateBrush(app.Resources, "SkuaSuccessTintBrush", Color.FromArgb(0x1F, palette.Success.R, palette.Success.G, palette.Success.B));
        SetOrUpdateBrush(app.Resources, "SkuaSuccessTintBorderBrush", Color.FromArgb(0x59, palette.Success.R, palette.Success.G, palette.Success.B));
        SetOrUpdateBrush(app.Resources, "SkuaWarningTintBrush", Color.FromArgb(0x1F, palette.Warning.R, palette.Warning.G, palette.Warning.B));
        SetOrUpdateBrush(app.Resources, "SkuaWarningTintBorderBrush", Color.FromArgb(0x59, palette.Warning.R, palette.Warning.G, palette.Warning.B));
    }

    private static void SetOrUpdateThemeBrush(Application app, ThemeVariant variant, string key, Color color)
    {
        if (app.TryGetResource(key, variant, out object? res) && res is SolidColorBrush brush)
        {
            if (brush.Color != color)
                brush.Color = color;
            return;
        }

        if (app.Resources.ThemeDictionaries.TryGetValue(variant, out IThemeVariantProvider? provider) &&
            provider is IResourceDictionary dict)
        {
            SetOrUpdateBrush(dict, key, color);
        }
    }

    private static Color ParseColorSafe(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return fallback;

        return Color.TryParse(hex, out Color parsed) ? parsed : fallback;
    }

    private static Color GetResourceColor(Application app, string resourceKey, string fallbackHex)
    {
        if (app.Resources.TryGetValue(resourceKey, out object? value))
        {
            if (value is Color c)
                return c;
            if (value is SolidColorBrush b)
                return b.Color;
            if (value is string s && Color.TryParse(s, out Color parsed))
                return parsed;
        }

        return Color.TryParse(fallbackHex, out Color fallback)
            ? fallback
            : Color.FromArgb(255, 16, 185, 129);
    }

    public static void AttachThemeService(
        Application app,
        Core.Interfaces.IThemeService themeService,
        Action<AvaloniaThemeService>? onApplied = null)
    {
        void Apply(object? themeArg = null, Core.Models.ColorScheme? scheme = null)
        {
            bool isDark = themeService.IsDarkTheme;
            ThemeVariant targetVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
            if (app.RequestedThemeVariant != targetVariant)
                app.RequestedThemeVariant = targetVariant;

            if (themeService is AvaloniaThemeService ats)
            {
                ApplyVsCodePalette(app, ats.ActivePalette);
                ApplyUiDensity(app, ats.IsComfortableDensity);
                ApplyTitleBarTheme(app, isDark);
                ApplyAccentBrushes(
                    app,
                    ats.PrimaryColor,
                    ats.PrimaryForegroundColor,
                    ats.SecondaryColor,
                    ats.SecondaryForegroundColor,
                    isDarkTheme: isDark,
                    accentHover: ats.UseThemeAccentColor ? ats.ActivePalette.AccentHover : null,
                    accentPressed: ats.UseThemeAccentColor ? ats.ActivePalette.AccentPressed : null);
                onApplied?.Invoke(ats);
                return;
            }

            ApplyTitleBarTheme(app, isDark);
            if (scheme == Core.Models.ColorScheme.PrimaryForeground)
                ApplyAccentBrushes(app, accentForegroundHex: themeArg?.ToString(), isDarkTheme: isDark);
            else
                ApplyAccentBrushes(app, accentHex: themeArg?.ToString() ?? themeService.SelectedColor?.ToString(), isDarkTheme: isDark);
        }

        themeService.ThemeChanged += theme => Apply(themeArg: theme);
        themeService.SchemeChanged += (scheme, color) => Apply(themeArg: color, scheme: scheme);
        Apply();
    }

    public static void ApplyUiDensity(Application app, bool comfortable)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyUiDensity(app, comfortable));
            return;
        }

        double titleBarHeight = comfortable ? 35.0 : 30.0;
        IResourceDictionary res = app.Resources;
        res["UiTitleBarHeight"] = titleBarHeight;
        res["UiTabBarHeight"] = comfortable ? 35.0 : 25.0;
        res["UiActivityBarWidth"] = comfortable ? 48.0 : 36.0;
        res["UiControlHeight"] = comfortable ? 32.0 : 28.0;
        res["UiSmallControlHeight"] = comfortable ? 28.0 : 24.0;
        res["UiChipHeight"] = comfortable ? 24.0 : 20.0;
        res["UiMenuItemHeight"] = comfortable ? 28.0 : 26.0;
        res["UiListItemMinHeight"] = comfortable ? 28.0 : 26.0;
        res["UiScrollbarSize"] = comfortable ? 8.0 : 6.0;

        res["UiFontMicro"] = 10.0;
        res["UiFontSmall"] = comfortable ? 12.0 : 11.0;
        res["UiFontBody"] = comfortable ? 13.0 : 12.0;
        res["UiFontHeading"] = 14.0;
        res["UiFontDisplay"] = 18.0;

        res["UiGap2xs"] = comfortable ? 4.0 : 2.0;
        res["UiGapXs"] = comfortable ? 6.0 : 4.0;
        res["UiGapSm"] = comfortable ? 8.0 : 6.0;
        res["UiGapMd"] = comfortable ? 10.0 : 8.0;
        res["UiGapBase"] = comfortable ? 12.0 : 10.0;
        res["UiGapLg"] = comfortable ? 16.0 : 12.0;
        res["UiGapXl"] = comfortable ? 20.0 : 16.0;

        res["UiPaddingXs"] = comfortable ? new Thickness(6) : new Thickness(4);
        res["UiPaddingSm"] = comfortable ? new Thickness(8) : new Thickness(6);
        res["UiCardPadding"] = comfortable ? new Thickness(14) : new Thickness(10);
        res["UiCardCompactPadding"] = comfortable ? new Thickness(14, 12) : new Thickness(12, 10);
        res["UiPaddingLg"] = comfortable ? new Thickness(16) : new Thickness(12);
        res["UiControlPadding"] = comfortable ? new Thickness(12, 0) : new Thickness(10, 0);
        res["UiButtonPadding"] = comfortable ? new Thickness(12, 0) : new Thickness(8, 0);

        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (Window window in desktop.Windows.ToArray())
            {
                if (window.ExtendClientAreaToDecorationsHint)
                    window.ExtendClientAreaTitleBarHeightHint = titleBarHeight;
            }
        }
    }
}
