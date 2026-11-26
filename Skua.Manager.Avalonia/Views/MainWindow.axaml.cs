using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Messaging;
using Skua.Core.Interfaces;
using Skua.Shared.Avalonia.Services;
using Skua.Shared.Avalonia.Theming;
using Avalonia.Threading;
using Skua.Manager.Avalonia.ViewModels;
using Skua.Shared.Avalonia.ViewModels;
using System.Collections.Specialized;
using System.ComponentModel;
using System;
using System.Linq;

namespace Skua.Manager.Avalonia.Views;

public partial class MainWindow : Window
{
    private readonly ManagerMainViewModel _viewModel;
    private readonly AvaloniaThemeService? _themeService;
    private readonly ApplicationThemesViewModel _themesViewModel;
    private readonly ArmyQueueViewModel? _armyQueueViewModel;
    private readonly AppUpdaterViewModel? _appUpdaterViewModel;
    private readonly ScriptUpdaterViewModel? _scriptUpdaterViewModel;
    private readonly IGetScriptsService? _getScriptsService;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        try { ThemeResourceApplicator.ApplyTitleBarTheme(this); } catch { }
        _viewModel = Ioc.Default.GetRequiredService<ManagerMainViewModel>();
        DataContext = _viewModel;
        _themesViewModel = Ioc.Default.GetRequiredService<ApplicationThemesViewModel>();
        TitleBar.SetInstanceArtworkPath(_themesViewModel.ManagerTitleBarArtworkPath);
        _themesViewModel.PropertyChanged += OnThemesViewModelPropertyChanged;

        _themeService = Ioc.Default.GetService<IThemeService>() as AvaloniaThemeService;

        _armyQueueViewModel = Ioc.Default.GetService<ArmyQueueViewModel>();
        if (_armyQueueViewModel != null)
            _armyQueueViewModel.PropertyChanged += OnArmyQueuePropertyChanged;

        _appUpdaterViewModel = Ioc.Default.GetService<AppUpdaterViewModel>();
        if (_appUpdaterViewModel != null)
            _appUpdaterViewModel.PropertyChanged += OnAppUpdaterPropertyChanged;

        _scriptUpdaterViewModel = Ioc.Default.GetService<ScriptUpdaterViewModel>();
        if (_scriptUpdaterViewModel != null)
            _scriptUpdaterViewModel.PropertyChanged += OnScriptUpdaterPropertyChanged;

        _getScriptsService = Ioc.Default.GetService<IGetScriptsService>();
        if (_getScriptsService != null)
        {
            _getScriptsService.PropertyChanged += OnGetScriptsPropertyChanged;
            _getScriptsService.Scripts.CollectionChanged += OnScriptsCollectionChanged;
        }

        UpdateManagerStatusBar();
        AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        StrongReferenceMessenger.Default.Register<MainWindow, ShowMainWindowMessage>(this, ShowManager);
        StrongReferenceMessenger.Default.Register<MainWindow, ExitManagerMessage>(this, AllowClose);

        Skua.Shared.Avalonia.Services.WindowPlacementHelper.CenterOnActiveScreen(this);

        Opened += (s, e) =>
        {
            try
            {
                Skua.Shared.Avalonia.Services.ThemeResourceApplicator.ApplyTitleBarTheme(this);
            }
            catch { }
        };

        Activated += (s, e) =>
        {
            try
            {
                var accVm = Ioc.Default.GetService<AccountManagerViewModel>();
                accVm?.ScheduleReload();
            }
            catch { }
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            _viewModel.IsActive = false;
            return;
        }

        _viewModel.IsActive = false;
        if (_armyQueueViewModel != null)
            _armyQueueViewModel.PropertyChanged -= OnArmyQueuePropertyChanged;
        if (_appUpdaterViewModel != null)
            _appUpdaterViewModel.PropertyChanged -= OnAppUpdaterPropertyChanged;
        if (_scriptUpdaterViewModel != null)
            _scriptUpdaterViewModel.PropertyChanged -= OnScriptUpdaterPropertyChanged;
        if (_getScriptsService != null)
        {
            _getScriptsService.PropertyChanged -= OnGetScriptsPropertyChanged;
            _getScriptsService.Scripts.CollectionChanged -= OnScriptsCollectionChanged;
        }
        _themesViewModel.PropertyChanged -= OnThemesViewModelPropertyChanged;
        StrongReferenceMessenger.Default.UnregisterAll(this);
        base.OnClosing(e);
    }

    private void OnThemesViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ApplicationThemesViewModel.ManagerTitleBarArtworkPath))
            Dispatcher.UIThread.Post(() => TitleBar.SetInstanceArtworkPath(_themesViewModel.ManagerTitleBarArtworkPath));
    }

    private void OnArmyQueuePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ArmyQueueViewModel.IsRunning) or nameof(ArmyQueueViewModel.IsPaused) or nameof(ArmyQueueViewModel.StatusText))
            Dispatcher.UIThread.Post(UpdateManagerStatusBar);
    }

    private void OnAppUpdaterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppUpdaterViewModel.UpdateAvailable) or nameof(AppUpdaterViewModel.IsChecking) or nameof(AppUpdaterViewModel.UpdateStatus))
            Dispatcher.UIThread.Post(UpdateManagerStatusBar);
    }

    private void OnScriptUpdaterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScriptUpdaterViewModel.IsBusy) or nameof(ScriptUpdaterViewModel.Status) or nameof(ScriptUpdaterViewModel.ProgressStatus))
            Dispatcher.UIThread.Post(UpdateManagerStatusBar);
    }

    private void OnGetScriptsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => Dispatcher.UIThread.Post(UpdateManagerStatusBar);

    private void OnScriptsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(UpdateManagerStatusBar);

    private void UpdateManagerStatusBar()
    {
        if (ManagerStatusBarBorder == null)
            return;

        if (_armyQueueViewModel is { IsRunning: true } q)
        {
            if (q.IsPaused)
            {
                UiTokens.Bind(StatusQueueIcon, Material.Icons.Avalonia.MaterialIcon.ForegroundProperty, UiTokens.SkuaWarningBrush);
                UiTokens.Bind(StatusQueueText, TextBlock.ForegroundProperty, UiTokens.SkuaTextBrush);
                StatusQueueText.Text = "Queue: Paused";
            }
            else
            {
                UiTokens.Bind(StatusQueueIcon, Material.Icons.Avalonia.MaterialIcon.ForegroundProperty, UiTokens.SkuaSuccessBrush);
                UiTokens.Bind(StatusQueueText, TextBlock.ForegroundProperty, UiTokens.SkuaTextBrush);
                StatusQueueText.Text = "Queue: Running";
            }
        }
        else
        {
            UiTokens.Bind(StatusQueueIcon, Material.Icons.Avalonia.MaterialIcon.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
            UiTokens.Bind(StatusQueueText, TextBlock.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
            StatusQueueText.Text = "Queue";
        }

        if (_appUpdaterViewModel != null)
        {
            bool updateAvailable = _appUpdaterViewModel.UpdateAvailable;
            string status = _appUpdaterViewModel.UpdateStatus ?? string.Empty;
            bool checkFailed = status.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || status.Contains("error", StringComparison.OrdinalIgnoreCase);
            bool notCheckedYet = status.StartsWith("Ready", StringComparison.OrdinalIgnoreCase);

            UiTokens.Bind(
                StatusUpdatesIcon,
                Material.Icons.Avalonia.MaterialIcon.ForegroundProperty,
                updateAvailable || checkFailed ? UiTokens.SkuaWarningBrush : UiTokens.SkuaMutedTextBrush);
            UiTokens.Bind(
                StatusUpdatesText,
                TextBlock.ForegroundProperty,
                updateAvailable ? UiTokens.SkuaTextBrush : UiTokens.SkuaMutedTextBrush);
            StatusUpdatesText.Text = _appUpdaterViewModel.IsChecking
                ? "Checking updates..."
                : updateAvailable
                    ? "Update Available"
                    : checkFailed
                        ? "Update check failed"
                        : notCheckedYet
                            ? "Updates"
                            : "Up to date";
            ToolTip.SetTip(StatusUpdatesButton, status);
        }

        if (_scriptUpdaterViewModel is { IsBusy: true })
        {
            UiTokens.Bind(StatusScriptsIcon, Material.Icons.Avalonia.MaterialIcon.ForegroundProperty, UiTokens.SkuaAccentBrush);
            UiTokens.Bind(StatusScriptsText, TextBlock.ForegroundProperty, UiTokens.SkuaTextBrush);
            StatusScriptsText.Text = "Updating scripts...";
            ToolTip.SetTip(StatusScriptsButton, _scriptUpdaterViewModel.ProgressStatus ?? _scriptUpdaterViewModel.Status);
        }
        else if (_getScriptsService != null && _getScriptsService.Total > 0)
        {
            int pending = _getScriptsService.Outdated + _getScriptsService.Missing;
            UiTokens.Bind(
                StatusScriptsIcon,
                Material.Icons.Avalonia.MaterialIcon.ForegroundProperty,
                pending > 0 ? UiTokens.SkuaWarningBrush : UiTokens.SkuaMutedTextBrush);
            UiTokens.Bind(
                StatusScriptsText,
                TextBlock.ForegroundProperty,
                pending > 0 ? UiTokens.SkuaTextBrush : UiTokens.SkuaMutedTextBrush);
            StatusScriptsText.Text = pending > 0
                ? $"{pending} Script Update{(pending == 1 ? string.Empty : "s")}"
                : "Scripts";
            ToolTip.SetTip(StatusScriptsButton, pending > 0
                ? $"Scripts: {_getScriptsService.Downloaded}/{_getScriptsService.Total} downloaded, {_getScriptsService.Outdated} outdated, {_getScriptsService.Missing} missing — click to open Scripts"
                : $"Scripts: All {_getScriptsService.Total} scripts up to date — click to open Scripts");
        }
        else
        {
            UiTokens.Bind(StatusScriptsIcon, Material.Icons.Avalonia.MaterialIcon.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
            UiTokens.Bind(StatusScriptsText, TextBlock.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
            StatusScriptsText.Text = "Scripts";
            ToolTip.SetTip(StatusScriptsButton, "Scripts — click to open");
        }
    }

    private void SelectTabOfType<T>() where T : class
    {
        TabItemViewModel? tab = _viewModel.Tabs.FirstOrDefault(t => t.Content is T);
        if (tab != null)
            _viewModel.SelectedTab = tab;
    }

    private void StatusQueueButton_Click(object? sender, RoutedEventArgs e) => SelectTabOfType<ArmyQueueViewModel>();
    private void StatusUpdatesButton_Click(object? sender, RoutedEventArgs e) => SelectTabOfType<AppUpdaterViewModel>();
    private void StatusScriptsButton_Click(object? sender, RoutedEventArgs e) => SelectTabOfType<ScriptUpdaterViewModel>();

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && FocusManager?.GetFocusedElement() is not TextBox)
        {
            int tabIndex = e.Key switch
            {
                >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
                >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1,
                _ => -1
            };
            if (tabIndex >= 0 && tabIndex < _viewModel.Tabs.Count)
            {
                _viewModel.SelectedTab = _viewModel.Tabs[tabIndex];
                e.Handled = true;
            }
        }
    }

    private void ShowManager(MainWindow recipient, ShowMainWindowMessage message)
    {
        if (!recipient.IsVisible)
        {
            recipient.Show();
        }

        if (recipient.WindowState == WindowState.Minimized)
            recipient.WindowState = WindowState.Normal;

        recipient.Activate();
        recipient.Topmost = true;
        recipient.Topmost = false;
        recipient.Focus();

    }

    private void AllowClose(MainWindow recipient, ExitManagerMessage message)
    {
        recipient._allowClose = true;
    }
}
