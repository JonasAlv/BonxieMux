using CommunityToolkit.Mvvm.Input;
using Skua.Core.Interfaces;
using Skua.Shared.Avalonia.Services;

namespace Skua.Shared.Avalonia.ViewModels;

public class ApplicationThemesViewModel : BotControlViewModelBase
{
    private readonly ISettingsService _settingsService;
    private string _managerTitleBarArtworkPath;

    public ApplicationThemesViewModel(IThemeService themeService, ColorSchemeEditorViewModel colorSchemeEditor, ISettingsService settingsService)
        : base("Application Themes", 620, 560)
    {
        ThemeService = themeService;
        ColorSchemeEditor = colorSchemeEditor;
        _settingsService = settingsService;
        _managerTitleBarArtworkPath = _settingsService.Get<string>("ManagerTitleBarArtworkPath") ?? string.Empty;
    }

    public IThemeService ThemeService { get; }
    public ColorSchemeEditorViewModel ColorSchemeEditor { get; }

    public string ManagerTitleBarArtworkPath
    {
        get => _managerTitleBarArtworkPath;
        set
        {
            string normalizedPath = value ?? string.Empty;
            if (!SetProperty(ref _managerTitleBarArtworkPath, normalizedPath))
                return;

            _settingsService.Set("ManagerTitleBarArtworkPath", normalizedPath);
        }
    }

    public bool UseAccentOnMainTitleBar
    {
        get => ThemeService is AvaloniaThemeService avaloniaTheme && avaloniaTheme.UseAccentOnMainTitleBar;
        set
        {
            if (ThemeService is not AvaloniaThemeService avaloniaTheme || avaloniaTheme.UseAccentOnMainTitleBar == value)
                return;

            avaloniaTheme.UseAccentOnMainTitleBar = value;
            OnPropertyChanged();
        }
    }

    public bool UseAccentOnManagerTitleBar
    {
        get => ThemeService is AvaloniaThemeService avaloniaTheme && avaloniaTheme.UseAccentOnManagerTitleBar;
        set
        {
            if (ThemeService is not AvaloniaThemeService avaloniaTheme || avaloniaTheme.UseAccentOnManagerTitleBar == value)
                return;

            avaloniaTheme.UseAccentOnManagerTitleBar = value;
            OnPropertyChanged();
        }
    }
}
