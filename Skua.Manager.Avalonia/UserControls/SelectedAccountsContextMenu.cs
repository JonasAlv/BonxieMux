using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using System;

namespace Skua.Manager.Avalonia.UserControls;

public class SelectedAccountsContextMenu : ContextMenu
{
    public SelectedAccountsContextMenu()
    {
        MenuItem startSelected = CreateBoundMenuItem("Start Selected", "StartAccountsCommand");
        MenuItem startWithScript = CreateClickMenuItem("Start Selected With Script", (_, _) =>
        {
            _ = FindOwner()?.StartSelectedWithScriptAsync();
        });
        MenuItem addTags = CreateClickMenuItem("Add Tags to Selected", (_, _) =>
        {
            FindOwner()?.AddTagsToSelectedAction();
        });
        MenuItem addToGroup = CreateClickMenuItem("Add Selected to Group", (_, _) =>
        {
            FindOwner()?.AddSelectedToGroupAction();
        });
        MenuItem selectAll = CreateBoundMenuItem("Select All", "SelectAllCommand");
        MenuItem deselectAll = CreateBoundMenuItem("Deselect All", "DeselectAllCommand");

        Items.Add(startSelected);
        Items.Add(startWithScript);
        Items.Add(new Separator());
        Items.Add(selectAll);
        Items.Add(deselectAll);
        Items.Add(new Separator());
        Items.Add(addTags);
        Items.Add(addToGroup);
        Items.Add(new Separator());
        Items.Add(CreateBoundMenuItem("Remove Selected", "RemoveAccountsCommand"));
    }

    private AccountManagerUserControl? FindOwner()
    {
        if (PlacementTarget is not Control current)
            return null;

        while (current is not null)
        {
            if (current is AccountManagerUserControl owner)
                return owner;

            current = current.Parent as Control;
        }

        return null;
    }

    private static MenuItem CreateBoundMenuItem(string header, string commandPath)
    {
        MenuItem item = new() { Header = header };
        item.Bind(MenuItem.CommandProperty, new Binding(commandPath));
        return item;
    }

    private static MenuItem CreateClickMenuItem(string header, EventHandler<RoutedEventArgs> clickHandler)
    {
        MenuItem item = new() { Header = header };
        item.Click += clickHandler;
        return item;
    }
}
