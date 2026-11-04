using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Skua.App.Avalonia.Converters;

// For a ProgressBar Maximum: a maximum of 0 (space not fetched yet) would draw the bar full, 1 draws it empty.
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
