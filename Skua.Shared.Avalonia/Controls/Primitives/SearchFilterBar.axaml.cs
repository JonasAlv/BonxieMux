using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using System;
using System.Windows.Input;

namespace Skua.Shared.Avalonia.Controls.Primitives;

/// <summary>
/// Standard search input with magnify icon, clear button, and right-aligned filter/action slot.
/// </summary>
public partial class SearchFilterBar : UserControl
{
    public static readonly StyledProperty<string> SearchTextProperty =
        AvaloniaProperty.Register<SearchFilterBar, string>(nameof(SearchText), string.Empty, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> PlaceholderTextProperty =
        AvaloniaProperty.Register<SearchFilterBar, string>(nameof(PlaceholderText), "Search...");

    public static readonly StyledProperty<ICommand?> ClearCommandProperty =
        AvaloniaProperty.Register<SearchFilterBar, ICommand?>(nameof(ClearCommand));

    public static readonly StyledProperty<object?> RightContentProperty =
        AvaloniaProperty.Register<SearchFilterBar, object?>(nameof(RightContent));

    public string SearchText
    {
        get => GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    public string PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public ICommand? ClearCommand
    {
        get => GetValue(ClearCommandProperty);
        set => SetValue(ClearCommandProperty, value);
    }

    public object? RightContent
    {
        get => GetValue(RightContentProperty);
        set => SetValue(RightContentProperty, value);
    }

    public event EventHandler<string>? SearchTextChanged;

    public SearchFilterBar()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SearchTextProperty)
            SearchTextChanged?.Invoke(this, SearchText ?? string.Empty);
    }

    private void ClearBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (ClearCommand is { } cmd && cmd.CanExecute(null))
            cmd.Execute(null);
        else
            SearchText = string.Empty;
    }
}
