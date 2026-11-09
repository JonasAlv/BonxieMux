using CommunityToolkit.Mvvm.Input;
using Skua.Core.Interfaces;
using Skua.Shared.Avalonia.Services;
using System.Collections.Generic;
using System;

namespace Skua.Shared.Avalonia.ViewModels;

public class ApplicationThemesViewModel : BotControlViewModelBase
{
    private string _selectedTab = "Palette & Accents";

    public ApplicationThemesViewModel(IThemeService themeService, ThemeSettingsViewModel themeSettings, ColorSchemeEditorViewModel colorSchemeEditor, BackgroundThemeViewModel backgroundTheme, ISettingsService settingsService, IFileDialogService fileDialogService)
        : base("Application Themes", 620, 680)
    {
        ThemeService = themeService;
        ThemeSettings = themeSettings;
        ColorSchemeEditor = colorSchemeEditor;
        BackgroundTheme = backgroundTheme;
        _settingsService = settingsService;
        _fileDialogService = fileDialogService;
        _managerTitleBarArtworkPath = _settingsService.Get<string>("ManagerTitleBarArtworkPath") ?? string.Empty;
        BrowseManagerTitleBarArtworkCommand = new RelayCommand(BrowseManagerTitleBarArtwork);
        ClearManagerTitleBarArtworkCommand = new RelayCommand(() => ManagerTitleBarArtworkPath = string.Empty);
        SetCurrentThemeCommand = new RelayCommand<object>(t => ThemeService.SetCurrentTheme(t));
        RemoveThemeCommand = new RelayCommand<object>(t => ThemeService.RemoveTheme(t));
    }

    private readonly ISettingsService _settingsService;
    private readonly IFileDialogService _fileDialogService;
    private string _managerTitleBarArtworkPath = string.Empty;

    public IThemeService ThemeService { get; }

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

    public IRelayCommand BrowseManagerTitleBarArtworkCommand { get; }
    public IRelayCommand ClearManagerTitleBarArtworkCommand { get; }

    private void BrowseManagerTitleBarArtwork()
    {
        string? path = _fileDialogService.OpenFile("Image Files (*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif");
        if (!string.IsNullOrWhiteSpace(path))
            ManagerTitleBarArtworkPath = path;
    }

    /// <summary>
    /// Controls whether the main BonxieMux title bar uses the active theme's accent color.
    /// </summary>
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

    /// <summary>
    /// Controls whether the Manager title bar uses the active theme's accent color.
    /// </summary>
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

    /// <summary>
    /// Controls whether changes to the Manager's theme automatically sync with BonxieMux.
    /// Maps to the persistent "syncTheme" setting.
    /// </summary>
    public bool SyncThemeWithApp
    {
        get => _settingsService.Get("syncTheme", false);
        set
        {
            if (_settingsService.Get("syncTheme", false) == value)
                return;

            _settingsService.Set("syncTheme", value);
            OnPropertyChanged();

            if (value && ThemeService is AvaloniaThemeService ats)
            {
                ManagerThemeBroadcaster.Broadcast(ats);
            }
        }
    }

    public ThemeSettingsViewModel ThemeSettings { get; }
    public ColorSchemeEditorViewModel ColorSchemeEditor { get; }
    public BackgroundThemeViewModel BackgroundTheme { get; }
    public IRelayCommand SetCurrentThemeCommand { get; }
    public IRelayCommand RemoveThemeCommand { get; }

    public List<string> Tabs { get; } = new() { "Palette & Accents", "Theme Library", "Custom Backgrounds" };

    public string SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }
}