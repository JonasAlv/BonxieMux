using Avalonia.Threading;
using Skua.App.Avalonia.ViewModels.Options;
using Skua.Core.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Skua.App.Avalonia.ViewModels.CoreBotsOptions;

/// <summary>
/// The Account drop-down of the CoreBots window: Load and Save (and the save when the window closes) use the picked account's
/// CBO_Storage(name).txt, this client's or another open account's. Switching saves the account being left, as closing the window
/// does, then loads the new one. An account with no file yet starts from the values shown.
/// </summary>
public partial class CoreBotsViewModel
{
    private DispatcherTimer? _accountsTimer;
    private bool _syncingAccounts;
    private AccountChoice _selectedAccount = new(Environment.ProcessId, "This client", true);

    public ObservableCollection<AccountChoice> Accounts { get; } = new();

    public AccountChoice SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (value is null || _syncingAccounts || value == _selectedAccount)
                return;
            if (!string.IsNullOrEmpty(TargetName))
                SaveInternal(showDialog: false);
            _selectedAccount = value;
            OnPropertyChanged(nameof(SelectedAccount));
            OnPropertyChanged(nameof(IsSelfAccount));
            LoadInternal(showDialog: false);
        }
    }

    public bool IsSelfAccount => _selectedAccount.IsSelf;

    /// <summary>The account whose file Load and Save use: this client's character or the picked account.</summary>
    private string TargetName => _selectedAccount.IsSelf ? _player.Username ?? "" : _selectedAccount.Name;

    private void SetAccountPolling(bool on)
    {
        if (on)
        {
            RefreshAccounts();
            _accountsTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _accountsTimer.Tick -= AccountsTick;
            _accountsTimer.Tick += AccountsTick;
            _accountsTimer.Start();
        }
        else
        {
            _accountsTimer?.Stop();
        }
    }

    private void AccountsTick(object? sender, EventArgs e) => RefreshAccounts();

    private void RefreshAccounts()
    {
        string self = string.IsNullOrWhiteSpace(_player.Username) ? "This client" : _player.Username;
        var wanted = new List<AccountChoice> { new(Environment.ProcessId, self, true) };
        wanted.AddRange(OptionSharing.OtherClients(Environment.ProcessId).Select(c => new AccountChoice(c.ProcessId, c.Account, false)));
        if (Accounts.SequenceEqual(wanted))
            return;
        _syncingAccounts = true;
        try
        {
            Accounts.Clear();
            foreach (AccountChoice account in wanted)
                Accounts.Add(account);
            // An account that closed (or this client logging in): keep the pick when it is still there, else this client.
            AccountChoice? same = wanted.FirstOrDefault(a => a.ProcessId == _selectedAccount.ProcessId);
            bool lost = same is null;
            _selectedAccount = same ?? wanted[0];
            OnPropertyChanged(nameof(SelectedAccount));
            OnPropertyChanged(nameof(IsSelfAccount));
            if (lost || _selectedAccount.IsSelf)
                CurrentPlayer = TargetName;
        }
        finally { _syncingAccounts = false; }
    }
}
