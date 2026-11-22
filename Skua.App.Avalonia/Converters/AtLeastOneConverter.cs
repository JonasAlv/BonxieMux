using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Skua.App.Avalonia.Converters;

public class AtLeastOneConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is int i && i > 0 ? (double)i : 1d;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
