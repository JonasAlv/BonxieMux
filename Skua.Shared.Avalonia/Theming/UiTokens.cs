using Avalonia;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Skua.Shared.Avalonia.Theming;

/// <summary>
/// Centralized resource keys and runtime lookup/binding helpers for <c>Tokens.axaml</c> and <c>Colors.axaml</c>.
/// Keeps procedural C# UI builders 1:1 in sync with declarative AXAML tokens.
/// </summary>
public static class UiTokens
{
    // Heights & Shell Metrics
    public const string UiControlHeight = nameof(UiControlHeight);
    public const string UiSmallControlHeight = nameof(UiSmallControlHeight);
    public const string UiTitleBarHeight = nameof(UiTitleBarHeight);
    public const string UiTabBarHeight = nameof(UiTabBarHeight);
    public const string UiActivityBarWidth = nameof(UiActivityBarWidth);
    public const string UiSidebarWidth = nameof(UiSidebarWidth);
    public const string UiChipHeight = nameof(UiChipHeight);
    public const string UiMenuItemHeight = nameof(UiMenuItemHeight);
    public const string UiListItemMinHeight = nameof(UiListItemMinHeight);
    public const string UiScrollbarSize = nameof(UiScrollbarSize);

    // Corner Radii
    public const string UiRadiusNone = nameof(UiRadiusNone);
    public const string UiRadiusControl = nameof(UiRadiusControl);
    public const string UiRadiusCard = nameof(UiRadiusCard);

    // Typography
    public const string UiFontMicro = nameof(UiFontMicro);
    public const string UiFontSmall = nameof(UiFontSmall);
    public const string UiFontBody = nameof(UiFontBody);
    public const string UiFontHeading = nameof(UiFontHeading);
    public const string UiFontDisplay = nameof(UiFontDisplay);
    public const string UiFontMono = nameof(UiFontMono);

    // Icon Sizes
    public const string UiIconXs = nameof(UiIconXs);
    public const string UiIconSm = nameof(UiIconSm);
    public const string UiIconMd = nameof(UiIconMd);
    public const string UiIconLg = nameof(UiIconLg);

    // Spacing & Padding
    public const string UiGap2xs = nameof(UiGap2xs);
    public const string UiGapXs = nameof(UiGapXs);
    public const string UiGapSm = nameof(UiGapSm);
    public const string UiGapMd = nameof(UiGapMd);
    public const string UiGapBase = nameof(UiGapBase);
    public const string UiGapLg = nameof(UiGapLg);
    public const string UiGapXl = nameof(UiGapXl);
    public const string UiPaddingXs = nameof(UiPaddingXs);
    public const string UiPaddingSm = nameof(UiPaddingSm);
    public const string UiCardPadding = nameof(UiCardPadding);
    public const string UiCardCompactPadding = nameof(UiCardCompactPadding);
    public const string UiPaddingLg = nameof(UiPaddingLg);
    public const string UiControlPadding = nameof(UiControlPadding);
    public const string UiButtonPadding = nameof(UiButtonPadding);
    public const string UiMenuPadding = nameof(UiMenuPadding);

    // Borders & Bounds
    public const string UiBorderThin = nameof(UiBorderThin);
    public const string UiFocusBorderThickness = nameof(UiFocusBorderThickness);
    public const string UiActiveBarThickness = nameof(UiActiveBarThickness);
    public const string UiPopupMaxHeight = nameof(UiPopupMaxHeight);
    public const string UiMenuMaxHeight = nameof(UiMenuMaxHeight);
    public const string UiMenuMinWidth = nameof(UiMenuMinWidth);

    // Semantic Brushes
    public const string SkuaPageBrush = nameof(SkuaPageBrush);
    public const string SkuaSurfaceBrush = nameof(SkuaSurfaceBrush);
    public const string SkuaSurfaceAltBrush = nameof(SkuaSurfaceAltBrush);
    public const string SkuaSurfaceSubtleBrush = nameof(SkuaSurfaceSubtleBrush);
    public const string SkuaBorderBrush = nameof(SkuaBorderBrush);
    public const string SkuaBorderStrongBrush = nameof(SkuaBorderStrongBrush);
    public const string SkuaTextBrush = nameof(SkuaTextBrush);
    public const string SkuaMutedTextBrush = nameof(SkuaMutedTextBrush);
    public const string SkuaAccentBrush = nameof(SkuaAccentBrush);
    public const string SkuaAccentForegroundBrush = nameof(SkuaAccentForegroundBrush);
    public const string SkuaDangerBrush = nameof(SkuaDangerBrush);
    public const string SkuaDangerForegroundBrush = nameof(SkuaDangerForegroundBrush);
    public const string SkuaDangerTintBrush = nameof(SkuaDangerTintBrush);
    public const string SkuaDangerSoftBorderBrush = nameof(SkuaDangerSoftBorderBrush);
    public const string SkuaSuccessBrush = nameof(SkuaSuccessBrush);
    public const string SkuaSuccessTintBrush = nameof(SkuaSuccessTintBrush);
    public const string SkuaSuccessTintBorderBrush = nameof(SkuaSuccessTintBorderBrush);
    public const string SkuaWarningBrush = nameof(SkuaWarningBrush);
    public const string SkuaWarningTintBrush = nameof(SkuaWarningTintBrush);
    public const string SkuaWarningTintBorderBrush = nameof(SkuaWarningTintBorderBrush);
    public const string SkuaAccentTintBrush = nameof(SkuaAccentTintBrush);
    public const string SkuaTagBorderBrush = nameof(SkuaTagBorderBrush);
    public const string SkuaTagTextBrush = nameof(SkuaTagTextBrush);
    public const string SkuaSelectionBrush = nameof(SkuaSelectionBrush);
    public const string SkuaNavHoverBrush = nameof(SkuaNavHoverBrush);
    public const string SkuaOverlayBackdropBrush = nameof(SkuaOverlayBackdropBrush);

    public static DynamicResourceExtension Resource(string key) => new(key);

    public static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    public static double GetDouble(string key, double fallback)
        => TryGetResource(key, out object? val) && val is double d ? d : fallback;

    public static Thickness GetThickness(string key, Thickness fallback)
        => TryGetResource(key, out object? val) && val is Thickness t ? t : fallback;

    public static CornerRadius GetCornerRadius(string key, CornerRadius fallback)
        => TryGetResource(key, out object? val) && val is CornerRadius c ? c : fallback;

    public static IBrush GetBrush(string key, IBrush fallback)
        => TryGetResource(key, out object? val) && val is IBrush b ? b : fallback;

    public static FontFamily GetFontFamily(string key, FontFamily fallback)
        => TryGetResource(key, out object? val) && val is FontFamily ff ? ff : fallback;

    private static bool TryGetResource(string key, out object? value)
    {
        Application? app = Application.Current;
        if (app is not null && app.TryGetResource(key, app.ActualThemeVariant, out value))
            return true;

        value = null;
        return false;
    }
}
