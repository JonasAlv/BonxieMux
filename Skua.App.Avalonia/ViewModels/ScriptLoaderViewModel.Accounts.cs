using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Avalonia.ViewModels.Options;
using Skua.Core.Services;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Skua.App.Avalonia.ViewModels;

/// <summary>
/// The Account drop-down of the Script Loader: the card, Load Script and Start/Stop Script work on this client or on any other open
/// account. Another account is a separate process, so it is driven with the same messages Army Control uses (load a script, start or
/// stop it) and its loaded script and running state are read from the file every client writes in the tabs folder: {pid}.state.
/// </summary>
public partial class ScriptLoaderViewModel
{
    private const int WM_SKUA_SET_OPTION = 0x0400 + 450;
    private const int WM_SKUA_LOAD_SCRIPT = 0x0400 + 453;

    private DispatcherTimer? _accountsTimer;
    private bool _syncingAccounts;
    private AccountChoice _selectedAccount = new(Environment.ProcessId, "This client", true);
    private string _remoteScriptPath = "";
    private bool _remoteRunning;

    public ObservableCollection<AccountChoice> Accounts { get; } = new();

    public AccountChoice SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (value is null || _syncingAccounts || value == _selectedAccount)
                return;
            _selectedAccount = value;
            RefreshRemote();
            RaiseShown();
            OnPropertyChanged(nameof(SelectedAccount));
        }
    }

    public bool IsOtherAccount => !_selectedAccount.IsSelf;

    // What the card and the buttons show: this client's script, or the picked account's.
    public string ShownScript => IsOtherAccount ? (string.IsNullOrWhiteSpace(_remoteScriptPath) ? "No script loaded" : Path.GetFileName(_remoteScriptPath)) : LoadedScript;
    public string ShownScriptPath => IsOtherAccount ? _remoteScriptPath : LoadedScriptPath;
    public bool ShownRunning => IsOtherAccount ? _remoteRunning : ScriptManager.ScriptRunning;
    public string ShownToggleText => ShownRunning ? "Stop Script" : "Start Script";
    public string ShownStatus => IsOtherAccount ? (_remoteRunning ? "[Running]" : string.IsNullOrWhiteSpace(_remoteScriptPath) ? "[No Script Loaded]" : "[Loaded]") : ScriptStatus;
    public bool CanPauseShown => !IsOtherAccount && ScriptManager.ScriptRunning;
    public bool CanEditOptionsShown => !IsOtherAccount && ToggleScriptEnabled;

    // This client's own state moved: the card follows it while this client is the shown account.
    partial void OnLoadedScriptChanged(string value) => RaiseShownIfLocal();
    partial void OnScriptStatusChanged(string value) => RaiseShownIfLocal();
    partial void OnToggleScriptEnabledChanged(bool value) => RaiseShownIfLocal();

    private void RaiseShownIfLocal()
    {
        if (!IsOtherAccount)
            RaiseShown();
    }

    private void RaiseShown()
    {
        OnPropertyChanged(nameof(IsOtherAccount));
        OnPropertyChanged(nameof(ShownScript));
        OnPropertyChanged(nameof(ShownScriptPath));
        OnPropertyChanged(nameof(ShownRunning));
        OnPropertyChanged(nameof(ShownToggleText));
        OnPropertyChanged(nameof(ShownStatus));
        OnPropertyChanged(nameof(CanPauseShown));
        OnPropertyChanged(nameof(CanEditOptionsShown));
    }

    /// <summary>The view calls this when it appears and goes: the open accounts are only polled while the Script Loader is on screen.</summary>
    public void SetAccountPolling(bool on)
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

    private void AccountsTick(object? sender, EventArgs e)
    {
        RefreshAccounts();
        if (IsOtherAccount)
        {
            RefreshRemote();
            RaiseShown();
        }
    }

    private static string SelfAccountName()
    {
        try
        {
            string? name = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<Skua.Core.Interfaces.IScriptPlayer>() is { LoggedIn: true } player ? player.Username : null;
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch { }
        return "This client";
    }

    private void RefreshAccounts()
    {
        var wanted = new System.Collections.Generic.List<AccountChoice> { new(Environment.ProcessId, SelfAccountName(), true) };
        wanted.AddRange(OptionSharing.OtherClients(Environment.ProcessId).Select(c => new AccountChoice(c.ProcessId, c.Account, false)));
        if (Accounts.SequenceEqual(wanted))
            return;
        _syncingAccounts = true;
        try
        {
            Accounts.Clear();
            foreach (AccountChoice account in wanted)
                Accounts.Add(account);
            // An account that closed: back to this client.
            _selectedAccount = wanted.FirstOrDefault(a => a.ProcessId == _selectedAccount.ProcessId) ?? wanted[0];
            OnPropertyChanged(nameof(SelectedAccount));
            RaiseShown();
        }
        finally { _syncingAccounts = false; }
    }

    private void RefreshRemote()
    {
        _remoteScriptPath = "";
        _remoteRunning = false;
        if (!IsOtherAccount)
            return;
        try
        {
            string file = Path.Combine(Skua.Core.Models.ClientFileSources.TabsDIR, $"{_selectedAccount.ProcessId}.state");
            if (File.Exists(file))
            {
                string[] lines = File.ReadAllLines(file);
                _remoteScriptPath = lines.Length > 0 ? lines[0].Trim() : "";
                _remoteRunning = lines.Length > 1 && lines[1].Trim() == "1";
            }
        }
        catch { }
    }

    /// <summary>Load Script button: opens the file picker in the local scripts directory and loads the script for the selected account.</summary>
    [RelayCommand]
    private void LoadScriptForAccount()
    {
        string? path = _fileDialog.OpenFile(_scriptPath, "Skua Scripts (*.cs)|*.cs");
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!IsOtherAccount)
        {
            LoadScript(path);
            return;
        }

        SendLoadToAccount(path);
    }

    /// <summary>Start / Stop Script button: on this client as before, or on the picked account.</summary>
    [RelayCommand]
    private async Task ToggleScriptForAccount()
    {
        if (!IsOtherAccount)
        {
            await ToggleScript();
            return;
        }
        if (!_remoteRunning && string.IsNullOrWhiteSpace(_remoteScriptPath))
        {
            _dialogService.ShowMessageBox($"No script is loaded on {_selectedAccount.Name}. Load one first.", "Scripts");
            return;
        }
        Skua.App.Avalonia.Services.TabIpc.SendMessage(_selectedAccount.ProcessId, WM_SKUA_SET_OPTION, new IntPtr(99), new IntPtr(_remoteRunning ? 0 : 1));
        // The account reports its new state within a moment; show the intent right away.
        _remoteRunning = !_remoteRunning;
        RaiseShown();
    }

    private void SendLoadToAccount(string path)
    {
        // The target reads the path from this shared file when the message reaches it.
        File.WriteAllText(Path.Combine(Skua.Core.Models.ClientFileSources.TabsDIR, "skua_global_script.txt"), path);
        Skua.App.Avalonia.Services.TabIpc.SendMessage(_selectedAccount.ProcessId, WM_SKUA_LOAD_SCRIPT, IntPtr.Zero, IntPtr.Zero);
        _remoteScriptPath = path;
        RaiseShown();
    }
}
