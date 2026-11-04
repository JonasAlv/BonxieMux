using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Services;
using Skua.Core.Utils;

namespace Skua.Shared.Avalonia.ViewModels;

public class BackgroundThemeViewModel : ObservableObject
{
    private readonly BackgroundThemeService _backgroundService;
    private readonly IFileDialogService _fileDialogService;
    private readonly ISettingsService _settingsService;

    public BackgroundThemeViewModel(BackgroundThemeService backgroundService, IFileDialogService fileDialogService, ISettingsService settingsService)
    {
        _backgroundService = backgroundService;
        _fileDialogService = fileDialogService;
        _settingsService = settingsService;
        BrowseTitleBarArtworkCommand = new RelayCommand(BrowseTitleBarArtwork);
        ClearTitleBarArtworkCommand = new RelayCommand(ClearTitleBarArtwork);
        BrowseBackgroundCommand = new AsyncRelayCommand(BrowseBackgroundAsync);
        OpenThemesFolderCommand = new RelayCommand(OpenThemesFolder);
        RefreshBackgroundsCommand = new RelayCommand(() => OnPropertyChanged(nameof(AvailableBackgrounds)));
        GetBackgroundsCommand = new RelayCommand(GetBackgrounds);
    }

    public List<string> AvailableBackgrounds => _backgroundService.GetAvailableBackgrounds();

    public string SelectedBackground
    {
        get => _backgroundService.CurrentBackground;
        set
        {
            if (_backgroundService.CurrentBackground != value)
            {
                _backgroundService.CurrentBackground = value;
                OnPropertyChanged();
            }
        }
    }

    public string? TitleBarArtworkPath => _settingsService.GetShared().TitleBarArtworkPath;

    public IRelayCommand BrowseTitleBarArtworkCommand { get; }
    public IRelayCommand ClearTitleBarArtworkCommand { get; }
    public IAsyncRelayCommand BrowseBackgroundCommand { get; }
    public IRelayCommand OpenThemesFolderCommand { get; }
    public IRelayCommand RefreshBackgroundsCommand { get; }
    public IRelayCommand GetBackgroundsCommand { get; }

    private void BrowseTitleBarArtwork()
    {
        string? selectedFilePath = _fileDialogService.OpenFile(
            ClientFileSources.SkuaDIR,
            "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*");

        if (string.IsNullOrWhiteSpace(selectedFilePath))
            return;

        _settingsService.Set("TitleBarArtworkPath", selectedFilePath);
        Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar.SetArtworkPath(selectedFilePath);
        OnPropertyChanged(nameof(TitleBarArtworkPath));
    }

    private void ClearTitleBarArtwork()
    {
        _settingsService.Set<string?>("TitleBarArtworkPath", null);
        Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar.SetArtworkPath(null);
        OnPropertyChanged(nameof(TitleBarArtworkPath));
    }

    private async Task BrowseBackgroundAsync()
    {
        string? selectedFilePath = _fileDialogService.OpenFile(
            ClientFileSources.SkuaThemesDIR,
            "SWF files (*.swf)|*.swf|All files (*.*)|*.*");

        if (!string.IsNullOrEmpty(selectedFilePath))
        {
            string fileName = Path.GetFileName(selectedFilePath);
            string destinationPath = Path.Combine(ClientFileSources.SkuaThemesDIR, fileName);

            if (selectedFilePath != destinationPath)
            {
                try
                {
                    File.Copy(selectedFilePath, destinationPath, true);
                }
                catch (Exception)
                {
                    return;
                }
            }

            OnPropertyChanged(nameof(AvailableBackgrounds));
            SelectedBackground = fileName;
        }
    }

    private void OpenThemesFolder()
    {
        try
        {
            System.Diagnostics.Process.Start("xdg-open", ClientFileSources.SkuaThemesDIR);
        }
        catch (Exception)
        {
        }
    }

    private void GetBackgrounds()
    {
        try
        {
            Link.OpenBrowser("https://github.com/auqw/SkuaBackgrounds");
        }
        catch (Exception)
        {
        }
    }
}