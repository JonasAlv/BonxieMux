using CommunityToolkit.Mvvm.Input;
using Skua.Core.Interfaces;
using Skua.Shared.Avalonia.Services;
using System;
using System.Collections.ObjectModel;

namespace Skua.Shared.Avalonia.ViewModels;

public sealed record InstalledThemeItemViewModel(
    string Name,
    string Subtitle,
    string DirectoryPath,
    bool IsBuiltIn);

public class ApplicationThemesViewModel : BotControlViewModelBase
{
    private readonly ISettingsService _settingsService;
    private string _managerTitleBarArtworkPath;
    private string _selectedThemeSection = "Themes";

    public ApplicationThemesViewModel(IThemeService themeService, ISettingsService settingsService)
        : base("Application Themes", 700, 620)
    {
        ThemeService = themeService;
        _settingsService = settingsService;
        _managerTitleBarArtworkPath = _settingsService.Get<string>("ManagerTitleBarArtworkPath") ?? string.Empty;

        if (themeService is AvaloniaThemeService ats)
        {
            ats.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AvaloniaThemeService.IsComfortableDensity))
                {
                    OnPropertyChanged(nameof(IsCompactDensity));
                    OnPropertyChanged(nameof(IsComfortableDensity));
                }
            };
        }

        UninstallInstalledThemeCommand = new RelayCommand<InstalledThemeItemViewModel?>(UninstallInstalledTheme);
        RefreshInstalledThemes();
    }

    public string[] ThemeSections { get; } = ["Themes", "Installed Themes"];

    public string SelectedThemeSection
    {
        get => _selectedThemeSection;
        set
        {
            if (!SetProperty(ref _selectedThemeSection, value))
                return;

            OnPropertyChanged(nameof(IsThemesSection));
            OnPropertyChanged(nameof(IsInstalledSection));

            if (IsInstalledSection)
            {
                if (ThemeService is AvaloniaThemeService avaloniaTheme)
                    avaloniaTheme.RefreshAvailableThemes();
                RefreshInstalledThemes();
            }
        }
    }

    public bool IsThemesSection => string.Equals(_selectedThemeSection, "Themes", StringComparison.Ordinal);
    public bool IsInstalledSection => string.Equals(_selectedThemeSection, "Installed Themes", StringComparison.Ordinal);

    public IThemeService ThemeService { get; }
    public ObservableCollection<InstalledThemeItemViewModel> InstalledThemes { get; } = [];
    public IRelayCommand<InstalledThemeItemViewModel?> UninstallInstalledThemeCommand { get; }

    private void UninstallInstalledTheme(InstalledThemeItemViewModel? item)
    {
        if (item is null || item.IsBuiltIn || string.IsNullOrWhiteSpace(item.DirectoryPath))
            return;

        OpenVsxThemeService.UninstallByDirectory(item.DirectoryPath);
        if (ThemeService is AvaloniaThemeService avaloniaTheme)
            avaloniaTheme.RefreshAvailableThemes();
        RefreshInstalledThemes();
    }

    private void RefreshInstalledThemes()
    {
        InstalledThemes.Clear();
        foreach (InstalledThemePackage pkg in OpenVsxThemeService.GetInstalledPackages())
            InstalledThemes.Add(new InstalledThemeItemViewModel(pkg.Name, pkg.Subtitle, pkg.DirectoryPath, pkg.IsBuiltIn));
    }

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

    public bool IsCompactDensity
    {
        get => ThemeService is not AvaloniaThemeService avaloniaTheme || !avaloniaTheme.IsComfortableDensity;
        set
        {
            if (value)
                IsComfortableDensity = false;
        }
    }

    public bool IsComfortableDensity
    {
        get => ThemeService is AvaloniaThemeService avaloniaTheme && avaloniaTheme.IsComfortableDensity;
        set
        {
            if (ThemeService is not AvaloniaThemeService avaloniaTheme || avaloniaTheme.IsComfortableDensity == value)
                return;

            avaloniaTheme.IsComfortableDensity = value;
            OnPropertyChanged(nameof(IsCompactDensity));
            OnPropertyChanged(nameof(IsComfortableDensity));
        }
    }
}
