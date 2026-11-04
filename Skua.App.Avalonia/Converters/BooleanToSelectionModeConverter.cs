using Avalonia.Controls;
using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Skua.App.Avalonia.Converters;

public class BooleanToSelectionModeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? SelectionMode.Multiple : SelectionMode.Single;
        return SelectionMode.Multiple;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is SelectionMode mode)
            return mode.HasFlag(SelectionMode.Multiple);
        return false;
    }
}
