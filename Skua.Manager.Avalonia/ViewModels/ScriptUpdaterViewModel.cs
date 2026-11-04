using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Shared.Avalonia.ViewModels;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Skua.Manager.Avalonia.ViewModels;

public partial class ScriptUpdaterViewModel : BotControlViewModelBase
{
    public ScriptUpdaterViewModel(IGetScriptsService scriptsService)
        : base("Scripts")
    {
        _scriptsService = scriptsService;
        _progress = new Progress<string>(p => ProgressStatus = p);
        RegisterMessageHandler();
    }

    private readonly IGetScriptsService _scriptsService;
    private readonly IProgress<string> _progress;
    private bool _messageRegistered;

    [ObservableProperty]
    private string _status = "Ready to update scripts.";

    [ObservableProperty]
    private string? _progressStatus = null;

    [ObservableProperty]
    private bool _isBusy;

    protected override void OnDeactivated()
    {
        StrongReferenceMessenger.Default.UnregisterAll(this);
        _messageRegistered = false;
        base.OnDeactivated();
    }

    protected override void OnActivated()
    {
        RegisterMessageHandler();
        base.OnActivated();
    }

    private void RegisterMessageHandler()
    {
        if (_messageRegistered)
            return;

        StrongReferenceMessenger.Default.Register<ScriptUpdaterViewModel, UpdateScriptsMessage>(this, ReceiveUpdateScriptsMessage);
        _messageRegistered = true;
    }

    private async void ReceiveUpdateScriptsMessage(ScriptUpdaterViewModel recipient, UpdateScriptsMessage message)
    {
        if (message.Reset)
        {
            await recipient.ResetScripts(default);
            return;
        }

        await recipient.UpdateScripts(default);
    }

    [RelayCommand]
    public async Task ResetScripts(CancellationToken token)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Resetting scripts...";
        try
        {
            string skuaPath = ClientFileSources.SkuaScriptsDIR;
            
            // Defensive move: retry to overcome transient OS file locks or dangling file handles
            int retries = 5;
            while (retries > 0)
            {
                try
                {
                    // The old folder is renamed, not deleted: scripts of your own, JunkItems.json and the like are still there
                    // (Scripts-old-<date>) if anything in it was needed.
                    if (Directory.Exists(skuaPath))
                        Directory.Move(skuaPath, $"{skuaPath}-old-{DateTime.Now:yyyyMMdd-HHmmss}");
                    break; // Success
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    retries--;
                    if (retries == 0) throw;
                    
                    // Force GC to clean up any dangling file streams from the script parser
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    await Task.Delay(300, token);
                }
            }

            if (!Directory.Exists(skuaPath))
                Directory.CreateDirectory(skuaPath);

            if (File.Exists(ClientFileSources.SkuaScriptsCommitFile))
                File.Delete(ClientFileSources.SkuaScriptsCommitFile);

            await UpdateScriptsCore(token);
        }
        catch (Exception ex)
        {
            Status = $"Error resetting scripts: {ex.Message}";
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task UpdateScripts(CancellationToken token)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Updating scripts...";
        try
        {
            await UpdateScriptsCore(token);
        }
        catch (Exception ex)
        {
            Status = $"Error updating scripts: {ex.Message}";
            IsBusy = false;
        }
    }

    private async Task UpdateScriptsCore(CancellationToken token)
    {
        try
        {
            await _scriptsService.RefreshScriptsAsync(_progress, token);

            int count = await _scriptsService.DownloadAllWhereAsync(s => !s.Downloaded || s.Outdated, token: token);
            ProgressStatus = $"Downloaded {count} scripts.";
            Status = "Scripts are up to date.";
        }
        catch (OperationCanceledException)
        {
            ProgressStatus = "Task cancelled.";
            Status = "Update cancelled.";
        }
        catch (Exception ex)
        {
            ProgressStatus = "Error occurred.";
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            await Task.Delay(1000);
            IsBusy = false;
            ProgressStatus = null;
        }
    }
}
