using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.Shared.Avalonia.ViewModels;

public class BackgroundThemeViewModel : ObservableObject
{
    private readonly IFileDialogService _fileDialogService;
    private readonly ISettingsService _settingsService;

    public BackgroundThemeViewModel(IFileDialogService fileDialogService, ISettingsService settingsService)
    {
        _fileDialogService = fileDialogService;
        _settingsService = settingsService;
        BrowseTitleBarArtworkCommand = new RelayCommand(BrowseTitleBarArtwork);
        ClearTitleBarArtworkCommand = new RelayCommand(ClearTitleBarArtwork);
    }

    public string? TitleBarArtworkPath => _settingsService.GetShared().TitleBarArtworkPath;

    public IRelayCommand BrowseTitleBarArtworkCommand { get; }
    public IRelayCommand ClearTitleBarArtworkCommand { get; }

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
}