using System;
using System.Globalization;
using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Models.Items;

namespace Skua.App.Avalonia.Converters;

public sealed class GrabberItemDisplayConverter : IValueConverter
{
    public static readonly GrabberItemDisplayConverter Instance = new();

    // ConverterParameter "row": the Grabber row text, the summary without its leading "[ID] " (the ID has its own
    // column). "junk": true when the item is marked as junk (shows the Junk chip). No parameter: the old one-line text.
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is "junk")
            return value is ItemBase junkItem && (Ioc.Default.GetService<IJunkService>()?.IsJunk(junkItem.ID) ?? false);

        if (parameter is "row" && value is not null)
        {
            string summary = Skua.App.Avalonia.ViewModels.Grabber.GrabberListViewModel.FormatGrabbedItem(value);
            int idEnd = summary.StartsWith('[') ? summary.IndexOf("] ", StringComparison.Ordinal) : -1;
            return idEnd > 0 ? summary[(idEnd + 2)..] : summary;
        }

        if (value is ItemBase item)
        {
            string baseText = item.ToString();

            IJunkService? junkService = Ioc.Default.GetService<IJunkService>();
            if (junkService != null && junkService.IsJunk(item.ID))
            {
                return baseText + " [Junk]";
            }

            return baseText;
        }

        return value?.ToString() ?? string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return null;
    }
}
