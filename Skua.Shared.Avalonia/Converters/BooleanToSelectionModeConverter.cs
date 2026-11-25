using Avalonia.Controls;
using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Skua.Shared.Avalonia.Converters;

public class BooleanToSelectionModeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? (b ? SelectionMode.Multiple : SelectionMode.Single) : SelectionMode.Multiple;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SelectionMode mode && mode.HasFlag(SelectionMode.Multiple);
}
