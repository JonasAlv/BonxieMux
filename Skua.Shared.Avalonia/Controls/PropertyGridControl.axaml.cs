using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.ComponentModel;
using System.Reflection;

namespace Skua.Shared.Avalonia.Controls;

public class PropertyItemRow
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public bool IsCollection { get; set; }
    public bool IsNotCollection => !IsCollection;
    public IReadOnlyList<object> CollectionItems { get; set; } = Array.Empty<object>();
}

public partial class PropertyGridControl : UserControl
{
    public static readonly StyledProperty<object?> SelectedObjectProperty =
        AvaloniaProperty.Register<PropertyGridControl, object?>(nameof(SelectedObject));

    public object? SelectedObject
    {
        get => GetValue(SelectedObjectProperty);
        set => SetValue(SelectedObjectProperty, value);
    }

    private readonly ObservableCollection<PropertyItemRow> _properties = new();

    public PropertyGridControl()
    {
        InitializeComponent();
        PropertiesItemsControl.ItemsSource = _properties;
        UpdateVisibility();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SelectedObjectProperty)
        {
            RebuildProperties();
        }
    }

    private void RebuildProperties()
    {
        _properties.Clear();
        object? obj = SelectedObject;

        if (obj == null)
        {
            UpdateVisibility();
            return;
        }

        Type type = obj.GetType();
        PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (PropertyInfo pi in properties)
        {
            if (!pi.CanRead || pi.GetIndexParameters().Length > 0)
                continue;

            try
            {
                object? val = pi.GetValue(obj);
                bool isCollection = val is IEnumerable && val is not string;
                IReadOnlyList<object> collectionItems = Array.Empty<object>();

                if (isCollection && val is IEnumerable enumerable)
                {
                    collectionItems = enumerable.Cast<object?>()
                        .Where(item => item != null)
                        .Cast<object>()
                        .ToArray();
                }

                string strVal = val switch
                {
                    null => "null",
                    bool b => b ? "True" : "False",
                    _ when isCollection => $"{collectionItems.Count} items",
                    _ => val.ToString() ?? string.Empty
                };

                string desc = pi.GetCustomAttribute<DescriptionAttribute>()?.Description ?? pi.Name;
                string category = pi.GetCustomAttribute<CategoryAttribute>()?.Category ?? "General";

                _properties.Add(new PropertyItemRow
                {
                    Name = pi.Name,
                    Value = strVal,
                    Description = desc,
                    Category = category,
                    IsCollection = isCollection,
                    CollectionItems = collectionItems
                });
            }
            catch
            {
                // Ignore properties that throw on read
            }
        }

        UpdateVisibility();
    }

    private void OpenCollection_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PropertyItemRow row } || row.CollectionItems.Count == 0)
            return;

        ListBox entriesList = new()
        {
            ItemsSource = row.CollectionItems,
            SelectedIndex = 0,
            ItemTemplate = new FuncDataTemplate<object>((item, _) =>
                new TextBlock
                {
                    Text = FormatCollectionEntry(item),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 3)
                })
        };

        PropertyGridControl selectedItemProperties = new();
        selectedItemProperties.SelectedObject = entriesList.SelectedItem;
        entriesList.SelectionChanged += (_, _) =>
            selectedItemProperties.SelectedObject = entriesList.SelectedItem;

        Grid content = new()
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            ColumnDefinitions = new ColumnDefinitions("*,5,*"),
            Margin = new Thickness(10)
        };

        Grid.SetRow(entriesList, 0);
        Grid.SetColumn(entriesList, 0);
        Grid.SetRow(selectedItemProperties, 0);
        Grid.SetColumn(selectedItemProperties, 2);

        GridSplitter splitter = new()
        {
            Width = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRow(splitter, 0);
        Grid.SetColumn(splitter, 1);

        Button closeButton = new()
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        Grid.SetRow(closeButton, 1);
        Grid.SetColumn(closeButton, 0);
        Grid.SetColumnSpan(closeButton, 3);

        Window editor = new()
        {
            Title = $"{row.Name} - Collection Viewer",
            Width = 700,
            Height = 500,
            Content = content,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        content.Children.Add(entriesList);
        content.Children.Add(splitter);
        content.Children.Add(selectedItemProperties);
        content.Children.Add(closeButton);

        closeButton.Click += (_, _) => editor.Close();
        editor.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
                editor.Close();
        };

        Window? owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null)
            editor.Show(owner);
        else
            editor.Show();
    }

    private static string FormatCollectionEntry(object? item)
    {
        if (item == null)
            return "(null)";

        try
        {
            Type itemType = item.GetType();
            PropertyInfo? nameProperty = itemType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo? idProperty = itemType.GetProperty("ID", BindingFlags.Public | BindingFlags.Instance);

            string name = nameProperty?.GetValue(item)?.ToString()
                ?? item.ToString()
                ?? itemType.Name;
            string? id = idProperty?.GetValue(item)?.ToString();
            PropertyInfo? quantityProperty = itemType.GetProperty("Quantity", BindingFlags.Public | BindingFlags.Instance);
            string? quantity = quantityProperty?.GetValue(item)?.ToString();

            string displayName = !string.IsNullOrWhiteSpace(quantity) ? $"{name} x{quantity}" : name;
            return !string.IsNullOrWhiteSpace(id) ? $"[{id}] {displayName}" : displayName;
        }
        catch
        {
            return item.ToString() ?? item.GetType().Name;
        }
    }

    private void UpdateVisibility()
    {
        bool hasProps = _properties.Count > 0;
        EmptyTextBlock.IsVisible = !hasProps;
        ContentScrollViewer.IsVisible = hasProps;
    }
}
