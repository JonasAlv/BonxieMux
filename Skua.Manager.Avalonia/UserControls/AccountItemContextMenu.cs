using System;
using Avalonia.Controls;
using Avalonia.Data;
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

        Items.Add(CreateBoundMenuItem("Start", "StartCommand", vm => vm.StartCommand.Execute(null)));
        Items.Add(CreateBoundMenuItem("Start With Script", "StartWithScriptCommand", vm => vm.StartWithScriptCommand.Execute(null)));
        Items.Add(new Separator());
        Items.Add(CreateBoundMenuItem("Edit Account", "EditCommand", vm => vm.EditCommand.Execute(null)));
        Items.Add(CreateBoundMenuItem("Edit Tags", "AddTagsCommand", vm => vm.AddTagsCommand.Execute(null)));
        Items.Add(CreateBoundMenuItem("Add to Group", "AddToGroupCommand", vm => vm.AddToGroupCommand.Execute(null)));
        Items.Add(new Separator());
        Items.Add(CreateBoundMenuItem("Remove", "RemoveCommand", vm => vm.RemoveCommand.Execute(null)));
    }

    private IAccountItemViewModel? GetAccount()
    {
        if (DataContext is IAccountItemViewModel vm)
            return vm;
        if (PlacementTarget is Control target && target.DataContext is IAccountItemViewModel targetVm)
            return targetVm;
        return null;
    }

    private MenuItem CreateBoundMenuItem(string header, string commandPath, Action<IAccountItemViewModel>? clickFallback = null)
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
        return item;
    }
}
