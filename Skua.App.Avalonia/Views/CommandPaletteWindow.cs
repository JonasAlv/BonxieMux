using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Skua.Core.Interfaces.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Skua.App.Avalonia.Views;

/// <summary>Searches the client's main menu and executes the selected command.</summary>
public sealed class CommandPaletteWindow : Window
{
    private sealed record Entry(string Path, string Title, IMainMenuItemViewModel Item);

    private readonly List<Entry> _all;
    private readonly TextBox _search = new() { Watermark = "Type to find a tool or helper…", Margin = new Thickness(10, 10, 10, 6) };
    private readonly ListBox _list = new() { Margin = new Thickness(6, 0, 6, 8), MaxHeight = 360 };

    public CommandPaletteWindow(IEnumerable<IMainMenuItemViewModel> menu)
    {
        _all = new List<Entry>();
        foreach (IMainMenuItemViewModel top in menu)
            Collect(top, "", _all);

        Title = "Command palette";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.BorderOnly;
        this[!BackgroundProperty] = new DynamicResourceExtension("SkuaPageBrush");

        var panel = new StackPanel { Orientation = Orientation.Vertical };
        panel.Children.Add(_search);
        panel.Children.Add(_list);
        Content = panel;

        _list.DoubleTapped += (_, _) => RunSelected();
        _search.TextChanged += (_, _) => Refresh();
        _search.KeyDown += SearchKeyDown;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
        Deactivated += (_, _) => Close();
        Opened += (_, _) =>
        {
            Refresh();
            _search.Focus();
        };
    }

    private static void Collect(IMainMenuItemViewModel item, string path, List<Entry> into)
    {
        string header = item.Header ?? string.Empty;
        if (item.SubItems is { Count: > 0 } children)
        {
            foreach (IMainMenuItemViewModel child in children)
                Collect(child, path.Length == 0 ? header : $"{path} › {header}", into);
            return;
        }
        if (header.Length > 0 && item.Command is not null)
            into.Add(new Entry(path, header, item));
    }

    private void Refresh()
    {
        string query = (_search.Text ?? string.Empty).Trim();
        IEnumerable<Entry> matches = _all;
        if (query.Length > 0)
        {
            string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            matches = _all.Where(e => words.All(w => e.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || e.Path.Contains(w, StringComparison.OrdinalIgnoreCase)))
                          .OrderBy(e => e.Title.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                          .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase);
        }
        _list.ItemsSource = matches.Select(e => new ListBoxItem { Content = e.Path.Length == 0 ? e.Title : $"{e.Title}    ({e.Path})", Tag = e }).ToList();
        if (_list.ItemCount > 0)
            _list.SelectedIndex = 0;
    }

    private void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when _list.ItemCount > 0:
                _list.SelectedIndex = Math.Min(_list.SelectedIndex + 1, _list.ItemCount - 1);
                e.Handled = true;
                break;
            case Key.Up when _list.ItemCount > 0:
                _list.SelectedIndex = Math.Max(_list.SelectedIndex - 1, 0);
                e.Handled = true;
                break;
            case Key.Enter:
                RunSelected();
                e.Handled = true;
                break;
        }
    }

    private void RunSelected()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: Entry entry })
            return;
        Close();
        try { entry.Item.Command?.Execute(null); } catch { }
    }
}
