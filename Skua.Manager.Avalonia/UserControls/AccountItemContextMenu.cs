using System;
using Avalonia.Controls;
using Avalonia.Data;
using Material.Icons;
using Material.Icons.Avalonia;
using Skua.Core.Interfaces.ViewModels;

namespace Skua.Manager.Avalonia.UserControls;

public class AccountItemContextMenu : ContextMenu
{
    public AccountItemContextMenu()
    {
        Opened += (s, e) =>
        {
            if (PlacementTarget is Control target && target.DataContext is IAccountItemViewModel vm)
            {
                DataContext = vm;
            }
        };

        Items.Add(CreateBoundMenuItem("Start", "StartCommand", MaterialIconKind.PlayCircleOutline, vm => vm.StartCommand.Execute(null)));
        Items.Add(CreateBoundMenuItem("Start With Script", "StartWithScriptCommand", MaterialIconKind.ScriptTextPlayOutline, vm => vm.StartWithScriptCommand.Execute(null)));
        Items.Add(new Separator());
        Items.Add(CreateBoundMenuItem("Edit Account", "EditCommand", MaterialIconKind.AccountEditOutline, vm => vm.EditCommand.Execute(null)));
        Items.Add(CreateBoundMenuItem("Edit Tags", "AddTagsCommand", MaterialIconKind.Tag, vm => vm.AddTagsCommand.Execute(null)));
        Items.Add(CreateBoundMenuItem("Add to Group", "AddToGroupCommand", MaterialIconKind.AccountMultiplePlus, vm => vm.AddToGroupCommand.Execute(null)));
        Items.Add(new Separator());
        Items.Add(CreateBoundMenuItem("Remove", "RemoveCommand", MaterialIconKind.DeleteOutline, vm => vm.RemoveCommand.Execute(null)));
    }

    private IAccountItemViewModel? GetAccount()
    {
        if (DataContext is IAccountItemViewModel vm)
            return vm;
        if (PlacementTarget is Control target && target.DataContext is IAccountItemViewModel targetVm)
            return targetVm;
        return null;
    }

    private MenuItem CreateBoundMenuItem(string header, string commandPath, MaterialIconKind iconKind, Action<IAccountItemViewModel>? clickFallback = null)
    {
        MenuItem item = new() { Header = header };
        item.Bind(MenuItem.CommandProperty, new Binding(commandPath));
        if (clickFallback != null)
        {
            item.Click += (s, e) =>
            {
                if (item.Command == null)
                {
                    var account = GetAccount();
                    if (account != null)
                        clickFallback(account);
                }
            };
        }
        item.Icon = new MaterialIcon { Kind = iconKind, Width = 14, Height = 14 };
        return item;
    }
}
