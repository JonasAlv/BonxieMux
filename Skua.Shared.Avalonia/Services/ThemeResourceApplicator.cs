using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Skua.Shared.Avalonia.Services;

public static class ThemeResourceApplicator
{
    private static bool _materialChecked;
    private static bool _hasMaterialStyles;



    public static void ApplyTitleBarTheme(Application app, bool isDark, Color? accentColor = null, Color? accentTextColor = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyTitleBarTheme(app, isDark, accentColor, accentTextColor));
            return;
        }

        ThemeVariant targetVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (app.RequestedThemeVariant != targetVariant)
            app.RequestedThemeVariant = targetVariant;

        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (Window window in desktop.Windows.ToArray())
            {
                ApplyTitleBarTheme(window, isDark, accentColor, accentTextColor);
            }
        }
    }

    public static void ApplyTitleBarTheme(Window window, bool? isDarkTheme = null, Color? accentColor = null, Color? accentTextColor = null)
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

    public static void ApplyNativeTitleBar(IntPtr hwnd, bool isDark, Color captionColor, Color textColor)
    {
    }

    public static void ApplyAccentBrushes(
        Application app,
        Color primary,
        Color primaryForeground,
        Color? secondary = null,
        Color? secondaryForeground = null,
        bool? isDarkTheme = null)
    {
        Color sec = secondary ?? primary;
        Color secFore = secondaryForeground ?? primaryForeground;

        SetOrUpdateColor(app.Resources, "SkuaAccentColor", primary);
        SetOrUpdateColor(app.Resources, "SkuaAccentForegroundColor", primaryForeground);
        SetOrUpdateBrush(app.Resources, "SkuaAccentBrush", primary);
        SetOrUpdateBrush(app.Resources, "SkuaAccentForegroundBrush", primaryForeground);
        SetOrUpdateBrush(app.Resources, "SkuaAccentHoverBrush", Lighten(primary, 0.1));
        SetOrUpdateBrush(app.Resources, "SkuaAccentPressedBrush", Darken(primary, 0.12));

        // Fluent controls (CheckBox, RadioButton, ToggleSwitch, Slider, ProgressBar, checked
        // ToggleButton, TabItem pipe, TextBox focus) read the OS accent unless these are set.
        ApplyFluentAccent(app, primary, primaryForeground);

        Color selectionLight = Color.FromArgb(0x33, primary.R, primary.G, primary.B);
        Color selectionDark = Color.FromArgb(0x66, primary.R, primary.G, primary.B);
        bool useDarkSelection = isDarkTheme ?? false;
        SetOrUpdateBrush(app.Resources, "SkuaSelectionBrush", useDarkSelection ? selectionDark : selectionLight);

        // Selected/on fill for tabs, toggles, rows, cards and chips; the strong one is its hover.
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

        ApplyMaterialPaletteResources(app, primary, primaryForeground, sec, secFore);
        TrySetMaterialAccent(app, primary, sec);

        if (isDarkTheme is bool dark)
        {
            TrySetMaterialBaseTheme(app, dark);
        }
    }

    public static void ApplyAccentBrushes(
        Application app,
        string? accentHex = null,
        string? accentForegroundHex = null,
        string? secondaryHex = null,
        string? secondaryForegroundHex = null,
        string fallbackAccentHex = "#10B981",
        bool? isDarkTheme = null)
    {
        Color primary = ParseColorSafe(accentHex, GetResourceColor(app, "SkuaAccentColor", fallbackAccentHex));
        Color primaryForeground = ParseColorSafe(accentForegroundHex, GetResourceColor(app, "SkuaAccentForegroundColor", "#FF000000"));
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
        // Glyphs drawn on an accent fill use the theme's text-on-accent colour, not Fluent's fixed white.
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

    private static void EnsureMaterialChecked(Application app)
    {
        if (_materialChecked)
            return;

        foreach (object style in app.Styles)
        {
            string? fullName = style.GetType().FullName;
            if (!string.IsNullOrWhiteSpace(fullName) && fullName.StartsWith("Material.Styles.Themes.", StringComparison.Ordinal))
            {
                _hasMaterialStyles = true;
                break;
            }
        }
        _materialChecked = true;
    }

    private static void TrySetMaterialBaseTheme(Application app, bool isDark)
    {
        EnsureMaterialChecked(app);
        if (!_hasMaterialStyles)
            return;

        foreach (object style in app.Styles)
        {
            Type type = style.GetType();
            string? fullName = type.FullName;
            if (string.IsNullOrWhiteSpace(fullName) || !fullName.StartsWith("Material.Styles.Themes.", StringComparison.Ordinal))
                continue;

            try
            {
                PropertyInfo? property = type.GetProperty("BaseTheme", BindingFlags.Public | BindingFlags.Instance);
                if (property?.CanWrite == true)
                    property.SetValue(style, Enum.Parse(property.PropertyType, isDark ? "Dark" : "Light", ignoreCase: true));
            }
            catch
            {
                // Best effort: keep app theme application resilient across Material versions.
            }
        }
    }

    private static void TrySetMaterialAccent(Application app, Color primary, Color secondary)
    {
        EnsureMaterialChecked(app);
        if (!_hasMaterialStyles)
            return;

        foreach (object style in app.Styles)
        {
            Type type = style.GetType();
            string? fullName = type.FullName;
            if (string.IsNullOrWhiteSpace(fullName) || !fullName.StartsWith("Material.Styles.Themes.", StringComparison.Ordinal))
                continue;

            TrySetThemeColor(style, type, "PrimaryColor", primary);
            TrySetThemeColor(style, type, "SecondaryColor", secondary);
            TrySetThemeColor(style, type, "PrimaryMid", primary);
            TrySetThemeColor(style, type, "SecondaryMid", secondary);
            TryInvokeThemeMethod(style, type, "SetPrimaryColor", primary);
            TryInvokeThemeMethod(style, type, "SetSecondaryColor", secondary);
        }
    }

    private static void ApplyMaterialPaletteResources(Application app, Color primary, Color primaryForeground, Color secondary, Color secondaryForeground)
    {
        Color primaryLight = Lighten(primary, 0.1);
        Color primaryDark = Darken(primary, 0.12);
        Color secondaryLight = Lighten(secondary, 0.1);
        Color secondaryDark = Darken(secondary, 0.12);

        SetColorResource(app, "MaterialPrimaryLightColor", primaryLight);
        SetColorResource(app, "MaterialPrimaryMidColor", primary);
        SetColorResource(app, "MaterialPrimaryDarkColor", primaryDark);
        SetColorResource(app, "MaterialSecondaryLightColor", secondaryLight);
        SetColorResource(app, "MaterialSecondaryMidColor", secondary);
        SetColorResource(app, "MaterialSecondaryDarkColor", secondaryDark);

        SetColorResource(app, "MaterialPrimaryLightForegroundColor", primaryForeground);
        SetColorResource(app, "MaterialPrimaryMidForegroundColor", primaryForeground);
        SetColorResource(app, "MaterialPrimaryForegroundColor", primaryForeground);
        SetColorResource(app, "MaterialSecondaryLightForegroundColor", secondaryForeground);
        SetColorResource(app, "MaterialSecondaryMidForegroundColor", secondaryForeground);
        SetColorResource(app, "MaterialSecondaryDarkForegroundColor", secondaryForeground);

        SetColorResource(app, "MaterialPrimaryColor", primary);
        SetColorResource(app, "MaterialSecondaryColor", secondary);
        SetColorResource(app, "MaterialSelectionColor", Color.FromArgb(0x55, primary.R, primary.G, primary.B));
        SetColorResource(app, "MaterialFlatButtonClickColor", Color.FromArgb(0x66, primary.R, primary.G, primary.B));
        SetColorResource(app, "MaterialFlatButtonRippleColor", Color.FromArgb(0x33, primary.R, primary.G, primary.B));
    }

    private static void SetColorResource(Application app, string colorKey, Color color)
    {
        SetOrUpdateColor(app.Resources, colorKey, color);

        if (colorKey.EndsWith("Color", StringComparison.Ordinal))
        {
            string brushKey = colorKey[..^"Color".Length] + "Brush";
            SetOrUpdateBrush(app.Resources, brushKey, color);
        }
    }

    private static void TrySetThemeColor(object theme, Type themeType, string propertyName, Color color)
    {
        try
        {
            PropertyInfo? property = themeType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property?.CanWrite != true)
                return;

            object? value = CoerceValue(property.PropertyType, color);
            if (value is not null)
                property.SetValue(theme, value);
        }
        catch
        {
            // Best effort across Material versions.
        }
    }

    private static void TryInvokeThemeMethod(object theme, Type themeType, string methodName, Color color)
    {
        try
        {
            MethodInfo[] methods = themeType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            foreach (MethodInfo method in methods)
            {
                if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (!method.IsStatic && parameters.Length == 1)
                {
                    object? arg = CoerceValue(parameters[0].ParameterType, color);
                    if (arg is null)
                        continue;
                    method.Invoke(theme, [arg]);
                    return;
                }

                if (method.IsStatic && parameters.Length == 2 && parameters[0].ParameterType.IsInstanceOfType(theme))
                {
                    object? arg = CoerceValue(parameters[1].ParameterType, color);
                    if (arg is null)
                        continue;
                    method.Invoke(null, [theme, arg]);
                    return;
                }
            }
        }
        catch
        {
            // Best effort across Material versions.
        }
    }

    private static object? CoerceValue(Type targetType, Color color)
    {
        if (targetType == typeof(Color))
            return color;

        ConstructorInfo? colorCtor = targetType.GetConstructor([typeof(Color)]);
        if (colorCtor is not null)
            return colorCtor.Invoke([color]);

        if (targetType == typeof(string))
            return color.ToString();

        return null;
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
}
