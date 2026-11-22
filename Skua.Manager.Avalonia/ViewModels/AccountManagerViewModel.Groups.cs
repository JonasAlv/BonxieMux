using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Core.Models.Servers;
using Skua.Core.Utils;
using Skua.Manager.Avalonia.Services;
using Skua.Shared.Avalonia.ViewModels;
using Skua.Shared.Avalonia.ViewModels.Dialogs;
using Skua.Shared.Avalonia.ViewModels.ScriptRepo;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Avalonia.Threading;

namespace Skua.Manager.Avalonia.ViewModels;


public sealed partial class AccountManagerViewModel
{
    public void AddGroup(string groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            return;

        if (Groups.Any(g => g.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase)))
        {
            _dialogService.ShowMessageBox($"A group named '{groupName}' already exists.", "Duplicate Group");
            return;
        }

        GroupItemViewModel newGroup = new(groupName);
        Groups.Add(newGroup);
        _SaveGroups();
    }

    public void AddAccountToGroup(IAccountItemViewModel account, IGroupItemViewModel group)
    {
        if (!group.Accounts.Contains(account))
        {
            group.Accounts.Add(account);
            _SaveGroups();
        }
    }

    private void _RemoveGroup(IGroupItemViewModel group)
    {
        if (Groups.Contains(group))
        {
            if (_dialogService.ShowMessageBox($"Are you sure you want to delete group '{group.Name}'?", "Confirm Delete Group", true) != true)
                return;

            Groups.Remove(group);
            _SaveGroups();
        }
    }

    private void _RenameGroup(IGroupItemViewModel group)
    {
        InputDialogViewModel inputDialogViewModel = new(
            "Rename Group", "Enter new group name", "Group Name", numericInputOnly: false)
        {
            DialogTextInput = group.Name
        };

        bool? result = _dialogService.ShowDialog(inputDialogViewModel, "Rename Group");

        if (result == true && !string.IsNullOrWhiteSpace(inputDialogViewModel.DialogTextInput))
        {
            string newName = inputDialogViewModel.DialogTextInput.Trim();

            if (newName.Equals(group.Name, StringComparison.OrdinalIgnoreCase))
                return;

            if (Groups.Any(g => g.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            {
                _dialogService.ShowMessageBox($"A group named '{newName}' already exists.", "Duplicate Group");
                return;
            }

            group.Name = newName;

            _SaveGroups();
        }
    }

    private void _RemoveAccountFromGroup(IGroupItemViewModel group, IAccountItemViewModel account)
    {
        if (group.Accounts.Contains(account))
        {
            group.Accounts.Remove(account);
            _SaveGroups();
        }
    }

    private void _ReplaceAccountInGroup(IGroupItemViewModel group, IAccountItemViewModel currentAccount, IAccountItemViewModel replacementAccount)
    {
        int index = group.Accounts.IndexOf(currentAccount);
        if (index < 0)
            return;

        bool duplicate = group.Accounts
            .Where((_, i) => i != index)
            .Any(a => string.Equals(a.Username, replacementAccount.Username, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
            return;

        group.Accounts[index] = replacementAccount;
        _SaveGroups();
    }

    private void _SaveGroups()
    {
        PerformInternalSettingsChange(() =>
        {
            List<GroupData> groupsData = Groups.Select(g => new GroupData
            {
                Name = g.Name,
                Accounts = g.Accounts.Select<IAccountItemViewModel, string>(a => a.Username).ToList()
            }).ToList();

            _settingsService.Set("AccountGroups", groupsData);
            _SaveAccountsInternal();
        });
    }

    private void _GetSavedGroups()
    {
        try
        {
            List<GroupData>? groupsData = _settingsService.Get<List<GroupData>>("AccountGroups");

            if (groupsData == null || groupsData.Count == 0)
            {
                Groups.Clear();
                return;
            }

            List<GroupItemViewModel> loadedGroups = new();

            foreach (GroupData groupData in groupsData)
            {
                try
                {
                    if (string.IsNullOrEmpty(groupData.Name))
                        continue;

                    GroupItemViewModel group = new(groupData.Name);

                    foreach (string username in groupData.Accounts)
                    {
                        if (!string.IsNullOrEmpty(username))
                        {
                            IAccountItemViewModel? account = Accounts.FirstOrDefault(a => a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                            if (account != null && !group.Accounts.Contains(account))
                            {
                                group.Accounts.Add(account);
                            }
                        }
                    }

                    loadedGroups.Add(group);
                }
                catch
                {
                    continue;
                }
            }

            Groups.ReplaceRange(loadedGroups);
        }
        catch
        {
            Groups.Clear();
        }
    }
}
