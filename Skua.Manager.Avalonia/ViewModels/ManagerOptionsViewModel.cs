using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.Services;
using Skua.Core.Models;
using Skua.Core.Utils;
using Skua.Shared.Avalonia.ViewModels;
using Skua.Shared.Avalonia.ViewModels.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Skua.Manager.Avalonia.ViewModels;

public class ManagerOptionsViewModel : ObservableObject
{
    public ManagerOptionsViewModel(
        List<DisplayOptionItemViewModelBase> options,
        ISettingsService settingsService,
        IFileDialogService fileService)
    {
        ManagerOptions = options;
        _settingsService = settingsService;
        _fileService = fileService;

        string initialDirectory = _settingsService.Get("ClientDownloadPath", string.Empty);
        if (string.IsNullOrEmpty(initialDirectory))
        {
            initialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (Directory.Exists(initialDirectory))
                _settingsService.Set("ClientDownloadPath", initialDirectory);
            else
                _settingsService.Set("ClientDownloadPath", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
        }

        _downloadPath = _settingsService.Get("ClientDownloadPath", string.Empty);
        ChangeDownloadPathCommand = new AsyncRelayCommand(ChangeDownloadPathAsync);
    }

    public List<DisplayOptionItemViewModelBase> ManagerOptions { get; }

    private string _downloadPath;
    private readonly ISettingsService _settingsService;
    private readonly IFileDialogService _fileService;

    public string DownloadPath
    {
        get => _downloadPath;
        set
        {
            if (SetProperty(ref _downloadPath, value))
                _settingsService.Set("ClientDownloadPath", value);
        }
    }

    public IAsyncRelayCommand ChangeDownloadPathCommand { get; }

    private async Task ChangeDownloadPathAsync()
    {
        string initialDirectory = _settingsService.Get("ClientDownloadPath", string.Empty);
        if (string.IsNullOrEmpty(initialDirectory))
        {
            initialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (Directory.Exists(initialDirectory))
                _settingsService.Set("ClientDownloadPath", initialDirectory);
            else
                _settingsService.Set("ClientDownloadPath", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
        }

        string? folderPath = await _fileService.OpenFolderAsync(initialDirectory);
        if (!string.IsNullOrEmpty(folderPath))
        {
            _settingsService.Set("ClientDownloadPath", folderPath);
            DownloadPath = folderPath;
        }
    }
}
