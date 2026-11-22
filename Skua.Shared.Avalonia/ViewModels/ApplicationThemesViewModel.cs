using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.Core.Interfaces;
using Skua.Shared.Avalonia.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Skua.Shared.Avalonia.ViewModels;

public sealed partial class OpenVsxThemeItemViewModel : ObservableObject
{
    public OpenVsxThemeItemViewModel(OpenVsxThemeEntry entry)
    {
        Entry = entry;
        _isInstalled = entry.IsInstalled;
    }

    public OpenVsxThemeEntry Entry { get; }
    public string DisplayName => Entry.DisplayName;
    public string Metadata => $"{Entry.Namespace} · v{Entry.Version} · {Entry.DownloadCount:N0} downloads";
    public string Description => Entry.Description;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isBusy;
}

public sealed record InstalledThemeItemViewModel(
    string Name,
    string Subtitle,
    string DirectoryPath,
    bool IsBuiltIn);

public class ApplicationThemesViewModel : BotControlViewModelBase
{
    private readonly ISettingsService _settingsService;
    private string _managerTitleBarArtworkPath;
    private string _themeSearchQuery = string.Empty;
    private bool _isSearchingThemes;
    private bool _hasLoadedInitialDiscovery;
    private string _selectedThemeSection = "Themes";
    private string _themeDiscoveryStatus = "Browse or search Open VSX for VS Code color themes.";

    public ApplicationThemesViewModel(IThemeService themeService, ISettingsService settingsService)
        : base("Application Themes", 700, 620)
    {
        ThemeService = themeService;
        _settingsService = settingsService;
        _managerTitleBarArtworkPath = _settingsService.Get<string>("ManagerTitleBarArtworkPath") ?? string.Empty;

        SearchThemesCommand = new AsyncRelayCommand(SearchThemesAsync);
        InstallThemeCommand = new AsyncRelayCommand<OpenVsxThemeItemViewModel?>(InstallThemeAsync);
        UninstallThemeCommand = new RelayCommand<OpenVsxThemeItemViewModel?>(UninstallTheme);
        UninstallInstalledThemeCommand = new RelayCommand<InstalledThemeItemViewModel?>(UninstallInstalledTheme);
        RefreshInstalledThemes();
    }

    public string[] ThemeSections { get; } = ["Themes", "Discover Themes", "Installed Themes"];

    public string SelectedThemeSection
    {
        get => _selectedThemeSection;
        set
        {
            if (!SetProperty(ref _selectedThemeSection, value))
                return;

            OnPropertyChanged(nameof(IsThemesSection));
            OnPropertyChanged(nameof(IsDiscoverSection));
            OnPropertyChanged(nameof(IsInstalledSection));

            if (IsDiscoverSection && !_hasLoadedInitialDiscovery)
            {
                _hasLoadedInitialDiscovery = true;
                _ = SearchThemesAsync();
            }
        }
    }

    public bool IsThemesSection => string.Equals(_selectedThemeSection, "Themes", StringComparison.Ordinal);
    public bool IsDiscoverSection => string.Equals(_selectedThemeSection, "Discover Themes", StringComparison.Ordinal);
    public bool IsInstalledSection => string.Equals(_selectedThemeSection, "Installed Themes", StringComparison.Ordinal);

    public IThemeService ThemeService { get; }
    public ObservableCollection<OpenVsxThemeItemViewModel> DiscoveredThemes { get; } = [];
    public ObservableCollection<InstalledThemeItemViewModel> InstalledThemes { get; } = [];
    public IAsyncRelayCommand SearchThemesCommand { get; }
    public IAsyncRelayCommand<OpenVsxThemeItemViewModel?> InstallThemeCommand { get; }
    public IRelayCommand<OpenVsxThemeItemViewModel?> UninstallThemeCommand { get; }
    public IRelayCommand<InstalledThemeItemViewModel?> UninstallInstalledThemeCommand { get; }

    public string ThemeSearchQuery
    {
        get => _themeSearchQuery;
        set => SetProperty(ref _themeSearchQuery, value);
    }

    public bool IsSearchingThemes
    {
        get => _isSearchingThemes;
        private set => SetProperty(ref _isSearchingThemes, value);
    }

    public string ThemeDiscoveryStatus
    {
        get => _themeDiscoveryStatus;
        private set => SetProperty(ref _themeDiscoveryStatus, value);
    }

    private async Task SearchThemesAsync()
    {
        if (IsSearchingThemes)
            return;

        IsSearchingThemes = true;
        ThemeDiscoveryStatus = "Searching Open VSX...";
        try
        {
            var entries = await OpenVsxThemeService.SearchAsync(ThemeSearchQuery);
            DiscoveredThemes.Clear();
            foreach (OpenVsxThemeEntry entry in entries)
                DiscoveredThemes.Add(new OpenVsxThemeItemViewModel(entry));

            ThemeDiscoveryStatus = entries.Count == 0
                ? "No matching themes found on Open VSX."
                : $"Found {entries.Count} theme extensions.";
        }
        catch (Exception ex)
        {
            ThemeDiscoveryStatus = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearchingThemes = false;
        }
    }

    private async Task InstallThemeAsync(OpenVsxThemeItemViewModel? item)
    {
        if (item is null || item.IsBusy)
            return;

        item.IsBusy = true;
        ThemeDiscoveryStatus = $"Downloading {item.DisplayName}...";
        try
        {
            int count = await OpenVsxThemeService.InstallAsync(item.Entry);
            item.IsInstalled = true;
            if (ThemeService is AvaloniaThemeService avaloniaTheme)
                avaloniaTheme.RefreshAvailableThemes();
            RefreshInstalledThemes();
            ThemeDiscoveryStatus = $"Installed {item.DisplayName} ({count} color theme{(count == 1 ? "" : "s")}). Select it in Preferred Dark/Light Theme above.";
        }
        catch (Exception ex)
        {
            ThemeDiscoveryStatus = $"Install failed: {ex.Message}";
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private void UninstallTheme(OpenVsxThemeItemViewModel? item)
    {
        if (item is null || item.IsBusy)
            return;

        OpenVsxThemeService.Uninstall(item.Entry);
        item.IsInstalled = false;
        if (ThemeService is AvaloniaThemeService avaloniaTheme)
            avaloniaTheme.RefreshAvailableThemes();
        RefreshInstalledThemes();
        ThemeDiscoveryStatus = $"Removed {item.DisplayName}.";
    }

    private void UninstallInstalledTheme(InstalledThemeItemViewModel? item)
    {
        if (item is null || item.IsBuiltIn || string.IsNullOrWhiteSpace(item.DirectoryPath))
            return;

        OpenVsxThemeService.UninstallByDirectory(item.DirectoryPath);
        foreach (OpenVsxThemeItemViewModel d in DiscoveredThemes)
        {
            if (string.Equals(d.Entry.InstallDirectory, item.DirectoryPath, StringComparison.OrdinalIgnoreCase))
                d.IsInstalled = false;
        }
        if (ThemeService is AvaloniaThemeService avaloniaTheme)
            avaloniaTheme.RefreshAvailableThemes();
        RefreshInstalledThemes();
        ThemeDiscoveryStatus = $"Removed {item.Name}.";
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
