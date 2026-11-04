using Avalonia.Controls;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Messaging;
using Skua.Core.Interfaces;
using Skua.Shared.Avalonia.Services;
using Avalonia.Threading;
using Skua.Manager.Avalonia.ViewModels;
using Skua.Shared.Avalonia.ViewModels;
using System.ComponentModel;
using System;
using System.Runtime.InteropServices;

namespace Skua.Manager.Avalonia.Views;

public partial class MainWindow : Window
{
    private readonly ManagerMainViewModel _viewModel;
    private readonly AvaloniaThemeService? _themeService;
    private readonly ApplicationThemesViewModel _themesViewModel;
    private bool _allowClose;



    public MainWindow()
    {
        InitializeComponent();
        _viewModel = Ioc.Default.GetRequiredService<ManagerMainViewModel>();
        DataContext = _viewModel;
        _themesViewModel = Ioc.Default.GetRequiredService<ApplicationThemesViewModel>();
        TitleBar.SetInstanceArtworkPath(_themesViewModel.ManagerTitleBarArtworkPath);
        _themesViewModel.PropertyChanged += OnThemesViewModelPropertyChanged;

        _themeService = Ioc.Default.GetService<IThemeService>() as AvaloniaThemeService;
        if (_themeService != null)
        {
            TitleBar.UseAccentBackground = _themeService.UseAccentOnManagerTitleBar;
            _themeService.ThemeChanged += OnThemeChanged;
        }

        StrongReferenceMessenger.Default.Register<MainWindow, ShowMainWindowMessage>(this, ShowManager);
        StrongReferenceMessenger.Default.Register<MainWindow, ExitManagerMessage>(this, AllowClose);

        Skua.Shared.Avalonia.Services.X11WindowPositionTracker.Track(this);

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
        if (_themeService != null)
            _themeService.ThemeChanged -= OnThemeChanged;
        _themesViewModel.PropertyChanged -= OnThemesViewModelPropertyChanged;
        StrongReferenceMessenger.Default.UnregisterAll(this);
        base.OnClosing(e);
    }

    private void OnThemesViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ApplicationThemesViewModel.ManagerTitleBarArtworkPath))
            Dispatcher.UIThread.Post(() => TitleBar.SetInstanceArtworkPath(_themesViewModel.ManagerTitleBarArtworkPath));
    }

    private void OnThemeChanged(object? theme)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_themeService != null)
                TitleBar.UseAccentBackground = _themeService.UseAccentOnManagerTitleBar;
        });
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
