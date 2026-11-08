using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.Core.Scripts;
using Skua.Shared.Avalonia.ViewModels;
using System.Collections.ObjectModel;
using System.Linq;

namespace Skua.App.Avalonia.ViewModels.Options;

public sealed partial class WatchDogViewModel : BotControlViewModelBase
{
    public WatchDogViewModel(ScriptOption options) : base("Watch Dog", 680, 560)
    {
        Options = options;
    }

    public ScriptOption Options { get; }

    /// <summary>The accounts on this PC that run a Watch Dog, refreshed while the window is open.</summary>
    public ObservableCollection<RescueAccountRow> Accounts { get; } = new();

    [ObservableProperty]
    private string _accountsNote = "Watch Dog must be running on each account (VibeSkuffle has it built in). Only this one window needs to be open.";

    [ObservableProperty]
    private bool _hasNoAccounts = true;

    public void RefreshAccounts()
    {
        var list = Options.RescueAccounts();
        // Keep the rows that are still there, so a checkbox being clicked is not rebuilt under the mouse.
        foreach (RescueAccountRow gone in Accounts.Where(r => list.All(a => !a.Account.Equals(r.Account, System.StringComparison.OrdinalIgnoreCase))).ToList())
            Accounts.Remove(gone);
        foreach (ScriptOption.RescueAccountInfo info in list)
        {
            RescueAccountRow? row = Accounts.FirstOrDefault(r => r.Account.Equals(info.Account, System.StringComparison.OrdinalIgnoreCase));
            if (row is null)
                Accounts.Add(row = new RescueAccountRow(this, info.Account));
            row.Update(info);
        }
        HasNoAccounts = Accounts.Count == 0;
    }

    internal void SetNote(string text) => AccountsNote = text;
}

/// <summary>One account of the Accounts tab: where it is, what it is doing, whether it may help, and a button to ask for help for it.</summary>
public sealed partial class RescueAccountRow : ObservableObject
{
    private readonly WatchDogViewModel _owner;
    private bool _updating;

    public RescueAccountRow(WatchDogViewModel owner, string account)
    {
        _owner = owner;
        Account = account;
    }

    public string Account { get; }

    [ObservableProperty]
    private string _where = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _canRequest;

    [ObservableProperty]
    private bool _canHelp = true;

    internal void Update(ScriptOption.RescueAccountInfo info)
    {
        _updating = true;
        Where = string.IsNullOrWhiteSpace(info.Map) ? info.Server : $"{info.Server} · {info.Map}";
        Status = info.Status;
        CanRequest = info.CanRequest;
        CanHelp = info.CanHelp;
        _updating = false;
    }

    partial void OnCanHelpChanged(bool value)
    {
        if (!_updating)
            _owner.Options.SetAccountCanHelp(Account, value);
    }

    [RelayCommand]
    private void RequestHelp() => _owner.SetNote(_owner.Options.RequestRescueHelpFor(Account));
}
