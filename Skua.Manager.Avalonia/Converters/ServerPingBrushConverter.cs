using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace Skua.Manager.Avalonia.Converters;

/// <summary>Colour of a server's ping text from its level: green for quick, amber for middling, red for slow or no answer, muted when unknown.</summary>
public sealed class ServerPingBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is int level
            ? level switch { 1 => "SkuaSuccessBrush", 2 => "SkuaWarningBrush", 3 => "SkuaDangerBrush", _ => "SkuaMutedTextBrush" }
            : "SkuaMutedTextBrush";
        if (Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out object? found) && found is IBrush brush)
            return brush;
        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
