using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace Skua.App.Avalonia.Converters;

public class InterceptedPacketBackgroundConverter : IValueConverter
{
    private static readonly IBrush OutboundBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xD5, 0x4F)); // Subtle Yellow
    private static readonly IBrush InboundBrush = new SolidColorBrush(Color.FromArgb(0x28, 0x29, 0xB6, 0xF6));  // Subtle Blue
    private static readonly IBrush BlockedBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xEF, 0x53, 0x50));  // Subtle Red
    private static readonly IBrush DefaultBrush = Brushes.Transparent;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool outbound)
        {
            return outbound ? OutboundBrush : InboundBrush;
        }

        if (value is null)
        {
            return BlockedBrush;
        }

        return DefaultBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class InterceptedPacketBorderBrushConverter : IValueConverter
{
    private static readonly IBrush OutboundBorder = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xD5, 0x4F));
    private static readonly IBrush InboundBorder = new SolidColorBrush(Color.FromArgb(0x66, 0x29, 0xB6, 0xF6));
    private static readonly IBrush BlockedBorder = new SolidColorBrush(Color.FromArgb(0x66, 0xEF, 0x53, 0x50));
    private static readonly IBrush DefaultBorder = Brushes.Transparent;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool outbound)
        {
            return outbound ? OutboundBorder : InboundBorder;
        }

        if (value is null)
        {
            return BlockedBorder;
        }

        return DefaultBorder;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class InterceptedPacketTagConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool outbound)
        {
            return outbound ? "OUT" : "IN";
        }

        return "BLOCK";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class InterceptedPacketTagForegroundConverter : IValueConverter
{
    private static readonly IBrush DefaultFg = Brushes.Gray;

    // Theme tokens have a darker light-mode variant, so the tag stays readable on light surfaces.
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool outbound)
        {
            return outbound ? ThemeBrush("SkuaWarningBrush") : ThemeBrush("SkuaInfoBrush");
        }

        if (value is null)
        {
            return ThemeBrush("SkuaDangerBrush");
        }

        return DefaultFg;
    }

    private static IBrush ThemeBrush(string key)
    {
        Application? app = Application.Current;
        if (app is not null && app.TryGetResource(key, app.ActualThemeVariant, out object? resource) && resource is IBrush brush)
            return brush;

        return DefaultFg;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
