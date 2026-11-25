using Avalonia.Data.Converters;
using Avalonia.Media;
using Skua.Shared.Avalonia.Theming;
using System;
using System.Globalization;

namespace Skua.App.Avalonia.Converters;

public class InterceptedPacketBackgroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool outbound)
            return UiTokens.GetBrush(outbound ? UiTokens.SkuaWarningTintBrush : UiTokens.SkuaAccentTintBrush, Brushes.Transparent);

        if (value is null)
            return UiTokens.GetBrush(UiTokens.SkuaDangerTintBrush, Brushes.Transparent);

        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class InterceptedPacketBorderBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool outbound)
            return UiTokens.GetBrush(outbound ? UiTokens.SkuaWarningTintBorderBrush : UiTokens.SkuaTagBorderBrush, Brushes.Transparent);

        if (value is null)
            return UiTokens.GetBrush(UiTokens.SkuaDangerSoftBorderBrush, Brushes.Transparent);

        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class InterceptedPacketTagConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool outbound ? (outbound ? "OUT" : "IN") : "BLOCK";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class InterceptedPacketTagForegroundConverter : IValueConverter
{
    private static IBrush DefaultFg => UiTokens.GetBrush(UiTokens.SkuaMutedTextBrush, Brushes.Gray);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool outbound)
            return UiTokens.GetBrush(outbound ? UiTokens.SkuaWarningBrush : UiTokens.SkuaTagTextBrush, Brushes.Gray);

        if (value is null)
            return UiTokens.GetBrush(UiTokens.SkuaDangerBrush, Brushes.Gray);

        return DefaultFg;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
