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
    [RelayCommand]
    public async Task StartAccounts()
    {
        await StartAccountBatch(Accounts.Where(acc => acc.UseCheck).ToList(), "selected accounts", StartWithScript);
    }

    [RelayCommand]
    public async Task StartAccountsInNewWindow()
    {
        await StartAccountBatch(Accounts.Where(acc => acc.UseCheck).ToList(), "selected accounts", StartWithScript, true);
    }

    [RelayCommand]
    public async Task StartAllAccountsInNewWindow()
    {
        await StartAccountBatch(Accounts.ToList(), "all accounts", StartWithScript, true);
    }

    [RelayCommand]
    public async Task StartAllAccounts()
    {
        await StartAccountBatch(Accounts.ToList(), "all accounts", StartWithScript);
    }

    private async Task StartAccountBatch(List<IAccountItemViewModel> accounts, string batchName, bool runScript, bool newWindow = false)
    {
        if (_isLaunching || accounts.Count == 0)
            return;

        try
        {
            _isLaunching = true;
            _launchCancellation?.Dispose();
            _launchCancellation = new CancellationTokenSource();
            string? hostInstanceId = newWindow ? Guid.NewGuid().ToString("N") : null;
            string batchId = Guid.NewGuid().ToString("N");

            foreach (IAccountItemViewModel account in accounts)
            {
                _launchCancellation.Token.ThrowIfCancellationRequested();
                _LaunchAcc(account.Username, account.Password, account.DisplayName, runScript, hostInstanceId, (accounts.Count >= 2 && _startInGridView) ? batchId : null);
                await Task.Delay(TimeSpan.FromSeconds(LaunchDelaySeconds), _launchCancellation.Token);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error starting {batchName}{(newWindow ? " in a new window" : string.Empty)}: {ex.Message}");
        }
        finally
        {
            _isLaunching = false;
            _launchCancellation?.Dispose();
            _launchCancellation = null;
            if (_pendingReload)
            {
                _pendingReload = false;
                ScheduleReload();
            }
        }
    }

    private void _StartAccount(IAccountItemViewModel account, bool withScript)
    {
        if (withScript && string.IsNullOrEmpty(ScriptPath))
        {
            _dialogService.ShowMessageBox("No script selected. Please select a script first.", "No Script");
            return;
        }

        bool runScript = withScript || StartWithScript;
        _LaunchAcc(account.Username, account.Password, account.DisplayName, runScript);
    }

    private async void _StartGroup(IGroupItemViewModel group, bool withScript, bool newWindow = false)
    {
        if (_isLaunching)
            return;

        if (withScript && string.IsNullOrEmpty(ScriptPath))
        {
            _dialogService.ShowMessageBox("No script selected. Please select a script first.", "No Script");
            return;
        }

        bool runScript = withScript || StartWithScript;
        await StartAccountBatch(group.Accounts.ToList(), $"group '{group.Name}'", runScript, newWindow);
    }

    private void _LaunchAcc(string username, string password, string? displayName = null, bool? withScript = null, string? hostInstanceId = null, string? gridBatchId = null)
    {
        try
        {
            ProcessStartInfo psi = SkuaExecutableLocator.CreateProcessStartInfo(new[]
            {
                "-s",
                SelectedServer?.Name ?? "Twilly"
            });

            if (!string.IsNullOrWhiteSpace(username))
            {
                psi.EnvironmentVariables["BONXIEMUX_USERNAME"] = username;
            }

            if (!string.IsNullOrWhiteSpace(password))
            {
                psi.EnvironmentVariables["BONXIEMUX_PASSWORD"] = password;
            }

            if (!string.IsNullOrWhiteSpace(hostInstanceId))
            {
                psi.ArgumentList.Add("--host-instance");
                psi.ArgumentList.Add(hostInstanceId);
            }
            if (!string.IsNullOrWhiteSpace(gridBatchId))
            {
                psi.ArgumentList.Add("--grid-view");
                psi.ArgumentList.Add(gridBatchId);
            }

            bool shouldUseScript = withScript ?? StartWithScript;
            if (shouldUseScript && !string.IsNullOrEmpty(ScriptPath))
            {
                psi.ArgumentList.Add("--run-script");
                psi.ArgumentList.Add(ScriptPath);
            }
            else if (!string.IsNullOrEmpty(ScriptPath))
            {
                psi.ArgumentList.Add("--load-script");
                psi.ArgumentList.Add(ScriptPath);
            }

            Process? process = Process.Start(psi);
            if (process != null)
            {
                string accountName = !string.IsNullOrWhiteSpace(displayName) ? displayName : username;
                StrongReferenceMessenger.Default.Send(new AddProcessMessage(process, accountName));
            }
        }
        catch (Exception ex)
        {
            _dialogService.ShowMessageBox($"Error while starting process: {ex.Message}", "Launch Error");
        }
    }

}
