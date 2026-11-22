using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Manager.Avalonia.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.Shared.Avalonia.ViewModels.ScriptRepo;

namespace Skua.Manager.Avalonia.ViewModels;


public partial class ArmyQueueViewModel
{
    [RelayCommand]
    public async Task StartQueue()
    {
        if (IsRunning)
            return;

        if (Steps.Count == 0 || Steps.All(s => s.Members.Count == 0))
        {
            _dialogService.ShowMessageBox("Please add at least one squad with accounts to the queue.", "Empty Queue");
            return;
        }

        IsRunning = true;
        _queueCts = new CancellationTokenSource();
        CancellationToken token = _queueCts.Token;

        try
        {
            await RunQueueAsync(token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Army Queue was stopped.";
        }
        catch (Exception ex)
        {
            StatusText = $"Queue Error: {ex.Message}";
            _dialogService.ShowMessageBox($"Queue encountered an error: {ex.Message}", "Queue Execution Error");
        }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            _queueCts?.Dispose();
            _queueCts = null;
            _pauseTcs = null;
        }
    }

    [RelayCommand]
    public void StopQueue()
    {
        if (!IsRunning || _queueCts == null)
            return;

        if (_dialogService.ShowMessageBox("Are you sure you want to stop the running Army Queue?", "Stop Queue", true) == true)
        {
            _pauseTcs?.TrySetCanceled();
            _queueCts?.Cancel();
            IsPaused = false;
            StatusText = "Stopping Army Queue...";
        }
    }

    private async Task RunQueueAsync(CancellationToken token)
    {
        int totalSteps = Steps.Count;
        ActiveStepIndex = 0;
        ProgressValue = 0;

        foreach (var s in Steps)
        {
            s.StepStatus = "Pending";
            foreach (var m in s.Members)
            {
                m.RuntimeStatus = "Waiting";
                m.ProcessId = null;
            }
        }

        for (int i = 0; i < Steps.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            ActiveStepIndex = i + 1;
            ArmyQueueStepViewModel currentStep = Steps[i];
            currentStep.StepStatus = "Running";
            StatusText = $"Running Squad {ActiveStepIndex}/{totalSteps}: '{currentStep.Name}' ({currentStep.Members.Count} accounts)...";

            List<Process> activeProcesses = new();
            Dictionary<int, ArmyQueueMemberViewModel> pidToMember = new();

            foreach (ArmyQueueMemberViewModel member in currentStep.Members)
            {
                token.ThrowIfCancellationRequested();

                IAccountItemViewModel? acc = _accountManager.Accounts.FirstOrDefault(
                    a => a.Username.Equals(member.Username, StringComparison.OrdinalIgnoreCase));

                if (acc is null)
                {
                    member.RuntimeStatus = "Account Missing";
                    continue;
                }

                member.RuntimeStatus = "Launching...";
                string targetScript = !string.IsNullOrWhiteSpace(member.ScriptPath)
                    ? member.ScriptPath
                    : (currentStep.StepScriptPath ?? string.Empty);

                string? optionsStorage = null;
                if (!string.IsNullOrWhiteSpace(targetScript))
                {
                    optionsStorage = !string.IsNullOrWhiteSpace(member.ScriptPath)
                        ? GetMemberStorageName(currentStep, member)
                        : GetStepStorageName(currentStep);
                }

                Process? proc = LaunchMemberProcess(acc, targetScript, optionsStorage);
                if (proc != null)
                {
                    member.ProcessId = proc.Id;
                    member.RuntimeStatus = "Running";
                    activeProcesses.Add(proc);
                    pidToMember[proc.Id] = member;
                }
                else
                {
                    member.RuntimeStatus = "Launch Failed";
                }

                if (DelayBetweenLaunchesSeconds > 0 && member != currentStep.Members.Last())
                {
                    await Task.Delay(DelayBetweenLaunchesSeconds * 1000, token);
                }
            }

            DateTime startTime = DateTime.UtcNow;

            while (activeProcesses.Any(p => !p.HasExited))
            {
                token.ThrowIfCancellationRequested();

                int completedCount = activeProcesses.Count(p => p.HasExited);
                int totalCount = activeProcesses.Count;
                TimeSpan elapsed = DateTime.UtcNow - startTime;

                foreach (Process p in activeProcesses)
                {
                    if (p.HasExited && pidToMember.TryGetValue(p.Id, out ArmyQueueMemberViewModel? m))
                    {
                        if (m.RuntimeStatus == "Running")
                            m.RuntimeStatus = "Finished";
                    }
                }

                string elapsedStr = elapsed.TotalHours >= 1
                    ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes:D2}m"
                    : $"{elapsed.Minutes}m {elapsed.Seconds:D2}s";

                StatusText = $"Squad {ActiveStepIndex}/{totalSteps} '{currentStep.Name}': {completedCount}/{totalCount} accounts finished (Runtime: {elapsedStr})";
                ProgressValue = ((i * 100.0) / totalSteps) + ((completedCount * 100.0) / (totalSteps * Math.Max(1, totalCount)));

                await Task.Delay(1000, token);
            }

            currentStep.StepStatus = "Completed";
            foreach (var m in currentStep.Members)
            {
                if (m.RuntimeStatus == "Running")
                    m.RuntimeStatus = "Finished";
            }

            ProgressValue = ((i + 1) * 100.0) / totalSteps;

            if (i < Steps.Count - 1)
            {
                if (IsPaused && _pauseTcs != null)
                {
                    string nextSquad = Steps[i + 1].Name;
                    StatusText = $"Squad '{currentStep.Name}' completed. Queue is PAUSED. Click 'Resume Queue' to start {nextSquad}.";
                    using var reg = token.Register(() => _pauseTcs.TrySetCanceled());
                    try
                    {
                        await _pauseTcs.Task;
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }

                int cooldown = Math.Max(1, DelayBetweenStepsSeconds);
                for (int c = cooldown; c > 0; c--)
                {
                    token.ThrowIfCancellationRequested();

                    if (IsPaused && _pauseTcs != null)
                    {
                        string nextSquad = Steps[i + 1].Name;
                        StatusText = $"Squad '{currentStep.Name}' completed. Queue is PAUSED. Click 'Resume Queue' to start {nextSquad}.";
                        using var reg = token.Register(() => _pauseTcs.TrySetCanceled());
                        try
                        {
                            await _pauseTcs.Task;
                        }
                        catch (TaskCanceledException)
                        {
                            break;
                        }
                    }

                    StatusText = $"Squad '{currentStep.Name}' completed! Waiting {c}s ghost session cooldown before next squad...";
                    await Task.Delay(1000, token);
                }
            }
        }

        StatusText = "All Army Queue squads completed successfully!";
        ProgressValue = 100;
        _dialogService.ShowMessageBox("All scheduled Army Queue squads have completed their runs!", "Army Queue Finished");
    }

    private Process? LaunchMemberProcess(IAccountItemViewModel acc, string scriptPath, string? optionsStorage = null)
    {
        try
        {
            string server = _settingsService.Get("LastServer", "Twilly");

            ProcessStartInfo psi = SkuaExecutableLocator.CreateProcessStartInfo(new[]
            {
                "-s",
                server
            });

            if (!string.IsNullOrWhiteSpace(acc.Username))
            {
                psi.EnvironmentVariables["SKUA_USERNAME"] = acc.Username;
            }

            if (!string.IsNullOrWhiteSpace(acc.Password))
            {
                psi.EnvironmentVariables["SKUA_PASSWORD"] = acc.Password;
            }

            if (!string.IsNullOrWhiteSpace(scriptPath))
            {
                psi.ArgumentList.Add("--run-script");
                psi.ArgumentList.Add(scriptPath);

                if (!string.IsNullOrWhiteSpace(optionsStorage))
                {
                    psi.ArgumentList.Add("--script-options-storage");
                    psi.ArgumentList.Add(optionsStorage);
                }
            }

            psi.ArgumentList.Add("--exit-on-script-stop");

            Process? process = Process.Start(psi);
            if (process != null)
            {
                StrongReferenceMessenger.Default.Send(new AddProcessMessage(process, acc.DisplayOrUsername));
            }

            return process;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error launching queue account {acc.Username}: {ex.Message}");
            return null;
        }
    }
}
