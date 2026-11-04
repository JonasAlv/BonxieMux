using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Core.Utils;
using Skua.Shared.Avalonia.ViewModels;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Skua.Manager.Avalonia.ViewModels;

public partial class ClientUpdatesViewModel : BotControlViewModelBase
{
    public ClientUpdatesViewModel(IClientUpdateService updateService, IGetScriptsService scriptsService, IDialogService dialogService, ISettingsService settingsService)
        : base("Client Updates")
    {
        RegisterMessageHandlers();

        _updateService = updateService;
        _scriptsService = scriptsService;
        _dialogService = dialogService;
        _settingsService = settingsService;
        Current = ClientFileSources.AssemblyVersion;
        _progress = new Progress<string>(p => ProgressStatus = p);
    }

    private async void CheckUpdate(ClientUpdatesViewModel recipient, CheckClientUpdateMessage message)
    {
        await recipient.Refresh();

        if (recipient.UpdateVisible && recipient._dialogService.ShowMessageBox($"New update available: {recipient.Latest}\r\nDo you want to download it?", "Update Available", true) == true)
            await recipient.Update();
    }

    private async void ReceiveUpdateScriptsMessage(ClientUpdatesViewModel recipient, UpdateScriptsMessage message)
    {
        if (message.Reset)
        {
            await recipient.ResetScripts(default);
            return;
        }

        await recipient.UpdateScripts(default);
    }

    protected override void OnActivated()
    {
        RegisterMessageHandlers();
        if (Latest is null)
            Refresh();
    }

    protected override void OnDeactivated()
    {
        StrongReferenceMessenger.Default.UnregisterAll(this);
        _messageRegistered = false;
        base.OnDeactivated();
    }

    private bool _messageRegistered;

    private void RegisterMessageHandlers()
    {
        if (_messageRegistered)
            return;

        StrongReferenceMessenger.Default.Register<ClientUpdatesViewModel, UpdateScriptsMessage>(this, ReceiveUpdateScriptsMessage);
        StrongReferenceMessenger.Default.Register<ClientUpdatesViewModel, CheckClientUpdateMessage>(this, CheckUpdate);
        _messageRegistered = true;
    }

    private readonly IGetScriptsService _scriptsService;
    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;
    private readonly IClientUpdateService _updateService;
    private readonly IProgress<string> _progress;

    public string Current { get; }

    [ObservableProperty]
    private string _status = "Loading...";

    [ObservableProperty]
    private string? _progressStatus = null;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _updateVisible;

    [ObservableProperty]
    private string? _latest;

    [RelayCommand]
    public async Task Refresh()
    {
        IsBusy = true;
        Status = "Loading...";
        try
        {
            await _updateService.CheckForUpdateAsync();
            UpdateVisible = _updateService.UpdateAvailable;
            Latest = _updateService.LatestVersion;
            Status = UpdateVisible ? $"Update available: {Latest}" : "You have the latest version";

            if (UpdateVisible
                && _settingsService.Get("UseNightlyBuilds", false)
                && _settingsService.Get("AutoUpdateNightlyBuilds", false))
                await Update();
        }
        catch
        {
            Status = "Error while checking for updates";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task Update()
    {
        if (!UpdateVisible)
            return;

        IsBusy = true;
        Status = "Downloading update...";
        try
        {
            await _updateService.DownloadUpdateAsync(_progress);
            Status = "Update downloaded successfully.";
            await Task.Delay(1000);
            ProgressStatus = null;
        }
        catch (OperationCanceledException)
        {
            Status = "Update cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"Update failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ResetScripts(CancellationToken token)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        Status = "Resetting scripts...";
        try
        {
            string skuaPath = ClientFileSources.SkuaScriptsDIR;
            if (Directory.Exists(skuaPath))
                Directory.Delete(skuaPath, true);

            Directory.CreateDirectory(skuaPath);

            if (File.Exists(ClientFileSources.SkuaScriptsCommitFile))
                File.Delete(ClientFileSources.SkuaScriptsCommitFile);

            await UpdateScriptsCore(token);
        }
        catch (OperationCanceledException)
        {
            Status = "Reset cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"Error resetting scripts: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task UpdateScripts(CancellationToken token)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        Status = "Updating scripts...";
        try
        {
            await UpdateScriptsCore(token);
        }
        finally
        {
            IsBusy = false;
            ProgressStatus = null;
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
            Status = "Script update cancelled.";
        }
        catch (Exception ex)
        {
            ProgressStatus = "Update failed.";
            Status = $"Error updating scripts: {ex.Message}";
        }

        await Task.Delay(1000);
    }
}
