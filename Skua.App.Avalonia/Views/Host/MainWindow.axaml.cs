using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Material.Icons;
using Material.Icons.Avalonia;
using Skua.App.Avalonia.Services;
using Skua.App.Avalonia.ViewModels;
using Skua.App.Avalonia.ViewModels.MainMenu;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Skua.Core.Interfaces.ViewModels;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Skua.Core.AppStartup;

namespace Skua.App.Avalonia.Views;

public partial class MainWindow : Window
{
    private static readonly TimeSpan MetricRefreshInterval = TimeSpan.FromSeconds(1);

    private readonly IFlashUtil _flash;
    private readonly IScriptOption _options;
    private readonly SkuaStartupHandler _startup;
    private readonly MainMenuViewModel _mainMenuVm;
    private readonly MainViewModel _mainViewModel;
    private MenuItem? _pluginsMenuItem;
    private readonly CancellationTokenSource _ipcCancelCts = new();
    private string _lastTabState = "";
    private DateTime _lastScriptRunning = DateTime.MinValue;
    private bool _isFlashLoaded;
    private bool _isTitleBarPermanentlyHidden = false;
    private bool _openedInitialized;

    public void HideTitleBar()
    {
        _isTitleBarPermanentlyHidden = true;
        var titleBar = this.FindControl<global::Avalonia.Controls.Control>("TitleBar");
        if (titleBar != null)
        {
            titleBar.IsVisible = false;
        }
        
        this.ExtendClientAreaToDecorationsHint = false;
    }

    public MainWindow()
    {
        InitializeComponent();
        Skua.Shared.Avalonia.Services.WindowResizeHelper.Attach(this);
        _mainViewModel = Ioc.Default.GetRequiredService<MainViewModel>();
        _mainMenuVm = Ioc.Default.GetRequiredService<MainMenuViewModel>();
        _flash = Ioc.Default.GetRequiredService<IFlashUtil>();
        _options = Ioc.Default.GetRequiredService<IScriptOption>();
        _startup = Ioc.Default.GetRequiredService<SkuaStartupHandler>();
        
        DataContext = _mainViewModel;
        Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar.SetArtworkPath(Ioc.Default.GetRequiredService<ISettingsService>().GetShared().TitleBarArtworkPath);
        MainMenuBar.DataContext = _mainMenuVm;
        var titleBar = this.FindControl<global::Avalonia.Controls.Control>("TitleBar");
        if (titleBar is Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar wtb)
        {
            wtb.DockClicked += (s, e) => RequestDockToHost();
        }
        ConfigureMainMenu();
        RegisterLifecycleHandlers();
    }

    public static void RequestDockToHost()
    {
        Task.Run(async () =>
        {
            try
            {
                using var client = new System.IO.Pipes.NamedPipeClientStream(".", App.HostPipeName, System.IO.Pipes.PipeDirection.Out);
                await client.ConnectAsync(500);
                using var writer = new System.IO.StreamWriter(client) { AutoFlush = true };
                await writer.WriteLineAsync($"DOCK|{Environment.ProcessId}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to send dock request: {ex.Message}");
            }
        });
    }

    private bool _gameOnly;
    private bool _gameOnlySetFullScreen;

    public void ToggleDetachedGameOnly()
    {
        if (_gameOnly)
        {
            ApplyGameOnly(false);
            if (_gameOnlySetFullScreen && WindowState == WindowState.FullScreen)
                WindowState = WindowState.Normal;
            _gameOnlySetFullScreen = false;
            return;
        }

        _gameOnlySetFullScreen = WindowState != WindowState.FullScreen;
        if (_gameOnlySetFullScreen)
            WindowState = WindowState.FullScreen;
        ApplyGameOnly(true);
    }

    public void ApplyGameOnly(bool on)
    {
        _gameOnly = on;
        var topNav = this.FindControl<global::Avalonia.Controls.Control>("TopNavBorder");
        if (topNav != null)
            topNav.IsVisible = !on;
        var titleBar = this.FindControl<global::Avalonia.Controls.Control>("TitleBar");
        if (titleBar != null && !_isTitleBarPermanentlyHidden)
            titleBar.IsVisible = !on;
    }

    private void ConfigureMainMenu()
    {
        BuildMainMenu();
        _mainMenuVm.MainMenuItems.CollectionChanged += MainMenuItemsChanged;
        if (_mainMenuVm.Plugins is INotifyCollectionChanged notifyPlugins)
        {
            notifyPlugins.CollectionChanged += (s, e) => Dispatcher.UIThread.Post(BuildMainMenu);
        }
    }

    private void MainMenuItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(BuildMainMenu);
    }

    private void BuildMainMenu()
    {
        MainMenuControl.Items.Clear();
        foreach (MainMenuItemViewModel item in _mainMenuVm.MainMenuItems)
            MainMenuControl.Items.Add(CreateMainMenuItem(item, isTopLevel: true));
    }

    private global::Avalonia.Controls.MenuItem CreateMainMenuItem(IMainMenuItemViewModel viewModel, bool isTopLevel = false)
    {
        global::Avalonia.Controls.MenuItem menuItem = new()
        {
            Command = viewModel.Command
        };

        if (isTopLevel)
        {
            menuItem.Classes.Add("top-nav-item");
            menuItem.Header = viewModel.Header;
        }
        else
        {
            menuItem.Header = viewModel.Header;
        }

        if (viewModel.Header == "Plugins")
            _pluginsMenuItem = menuItem;

        if (viewModel is MainMenuItemViewModel item && item.SubItems != null && item.SubItems.Any())
        {
            foreach (IMainMenuItemViewModel subItem in item.SubItems)
                menuItem.Items.Add(CreateMainMenuItem(subItem, isTopLevel: false));
        }
        else if (viewModel.SubItems != null && viewModel.SubItems.Any())
        {
            foreach (IMainMenuItemViewModel subItem in viewModel.SubItems)
                menuItem.Items.Add(CreateMainMenuItem(subItem, isTopLevel: false));
        }

        return menuItem;
    }

    private void RegisterLifecycleHandlers()
    {
        Opened += MainWindow_Opened;
        Closing += (s, e) => App.DisposeTrayIcon();
        Closed += MainWindow_Closed;

        Deactivated += (s, e) =>
        {
            CloseDropdowns();
            if (string.IsNullOrEmpty(App.HostPipeName) || HotKeys.IsDetachedMode)
            {
                _isWindowFocused = false;
                ReleaseAllPressedKeys();
                Skua.App.Avalonia.Controls.GameHost.ForwardFocus(false);
                Dispatcher.UIThread.Post(UpdateQualityFocusState);
            }
        };
        Activated += (s, e) =>
        {
            if (string.IsNullOrEmpty(App.HostPipeName) || HotKeys.IsDetachedMode)
            {
                _isWindowFocused = true;
                ReleaseAllPressedKeys();
                Skua.App.Avalonia.Controls.GameHost.ForwardFocus(true);
                Dispatcher.UIThread.Post(UpdateQualityFocusState);
            }
        };

        PropertyChanged += (s, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                Dispatcher.UIThread.Post(UpdateQualityFocusState);
            }
        };

        if (_options is INotifyPropertyChanged notifyOptions)
        {
            notifyOptions.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName is nameof(IScriptOption.AutoHeadless)
                    or nameof(IScriptOption.AutoHeadlessExcludeFocused)
                    or nameof(IScriptOption.AutoHeadlessExcludeGridView))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        EvaluateAutoHeadless();
                        UpdateQualityFocusState();
                    });
                }
                else if (e.PropertyName is nameof(IScriptOption.HeadlessMode) or Skua.Core.Scripts.ScriptOption.AutoHeadlessActiveChanged)
                {
                    if (Dispatcher.UIThread.CheckAccess())
                        UpdateHeadlessHostVisibility();
                    else
                        Dispatcher.UIThread.Post(UpdateHeadlessHostVisibility);
                }

                if (!string.IsNullOrEmpty(App.HostPipeName) && Skua.Core.Scripts.ScriptOption.IsGlobalSyncedOption(e.PropertyName))
                {
                    try
                    {
                        var prop = _options.GetType().GetProperty(e.PropertyName!);
                        if (prop is not null)
                        {
                            string valStr = Convert.ToString(prop.GetValue(_options), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                            SendPipeToHost($"GLOBAL_OPT|{e.PropertyName}|{valStr}");
                        }
                    }
                    catch { }
                }
            };
        }

        if (Ioc.Default.GetService<IScriptManager>() is INotifyPropertyChanged notifyScriptManager)
        {
            notifyScriptManager.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(IScriptManager.LoadedScript)
                    or nameof(IScriptManager.ScriptRunning)
                    or nameof(IScriptManager.ScriptPaused))
                {
                    SendScriptStateToHost();
                }
            };
        }
    }

    private void UpdateHeadlessHostVisibility()
    {
        bool isHeadless = _options.IsHeadlessNow();
        if (HeadlessOverlay != null) HeadlessOverlay.IsVisible = isHeadless;
        if (FlashHost != null)
        {
            bool shouldShowFlash = !isHeadless && _isFlashLoaded;
            bool wasVisible = FlashHost.IsVisible;
            FlashHost.IsVisible = shouldShowFlash;
            if (!wasVisible && shouldShowFlash)
            {
                TopNavBorder?.InvalidateMeasure();
                TopNavBorder?.InvalidateVisual();
                _options.EnforceRenderState(flushGamePref: false);
            }
        }
        SyncHostedVisibility();
        SendHeadlessStateToHost();
    }

    private void MainWindow_Opened(object? sender, EventArgs e)
    {
        if (_openedInitialized)
            return;
        _openedInitialized = true;

        _startup.Execute();
        HotKeys.OpenCommandPaletteRequested = () => Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (Skua.Shared.Avalonia.Controls.Shell.QuickPickWindow.DismissActive())
                    return;
                new CommandPaletteWindow(_mainMenuVm.MainMenuItems, _mainMenuVm.Plugins).ShowModeless(IsVisible ? this : null);
            }
            catch { }
        });
        HotKeys.ToggleDetachedGameFullScreenRequested = () => Dispatcher.UIThread.Post(ToggleDetachedGameOnly);
        if (HotKeys.IsHostProcess)
        {
            HotKeys.ToggleFullscreenRequested = () => Dispatcher.UIThread.Post(() =>
            {
                if (_gameOnly)
                {
                    _gameOnlySetFullScreen = false;
                    ApplyGameOnly(false);
                }
                WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
            });
            HotKeys.ToggleGameFullScreenRequested = () => Dispatcher.UIThread.Post(() =>
            {
                if (!_gameOnly)
                {
                    _gameOnlySetFullScreen = WindowState != WindowState.FullScreen;
                    if (_gameOnlySetFullScreen)
                        WindowState = WindowState.FullScreen;
                    ApplyGameOnly(true);
                }
                else
                {
                    ApplyGameOnly(false);
                    if (_gameOnlySetFullScreen && WindowState == WindowState.FullScreen)
                        WindowState = WindowState.Normal;
                    _gameOnlySetFullScreen = false;
                }
            });
        }
        _flash.FlashCall += OnFlashCall;

        try
        {
            var themeService = Ioc.Default.GetService<IThemeService>();
            bool isDark = themeService?.IsDarkTheme ?? true;
            Skua.Shared.Avalonia.Services.ThemeResourceApplicator.ApplyTitleBarTheme(this, isDark);
        }
        catch { }

        Focusable = true;
        InitializeInput();

        if (FlashHost != null)
        {
            bool isHeadless = _options.IsHeadlessNow();
            FlashHost.IsVisible = !isHeadless;
            Dispatcher.UIThread.RunJobs();
            if (!isHeadless)
                FlashHost.Focus();
        }
        if (_flash is Flash.FlashUtil player)
        {
            FlashHost?.AttachFlash(player);
            if (!player.IsPlayerRunning)
                _flash.InitializeFlash();
        }
        else
        {
            _flash.InitializeFlash();
        }
        TabIpc.StartListener(this, Environment.ProcessId, _ipcCancelCts.Token);

        _ = Task.Run(() => RunBackgroundTabSyncLoopAsync(_ipcCancelCts.Token));
        _ = Task.Run(() => RunOptionCommandLoopAsync(_ipcCancelCts.Token));
    }

    private async Task RunBackgroundTabSyncLoopAsync(CancellationToken ct)
    {
        string lastWrittenUsername = "";
        int tick = 0;

        try
        {
            PublishSharedOptions();
            SendScriptStateToHost();

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);

                try
                {
                    var bot = Ioc.Default.GetRequiredService<IScriptInterface>();
                    if (bot.Player.LoggedIn && !string.IsNullOrWhiteSpace(bot.Player.Username))
                    {
                        string username = bot.Player.Username;
                        string dir = ClientFileSources.TabsDIR;
                        System.IO.Directory.CreateDirectory(dir);

                        if (username != lastWrittenUsername)
                        {
                            lastWrittenUsername = username;
                            string usernameFile = System.IO.Path.Combine(dir, $"{Environment.ProcessId}.txt");
                            System.IO.File.WriteAllText(usernameFile, username);
                        }

                        IScriptMap map = Ioc.Default.GetRequiredService<IScriptMap>();
                        IScriptPlayer player = Ioc.Default.GetRequiredService<IScriptPlayer>();
                        if (map.Loaded && !string.IsNullOrWhiteSpace(map.Name) && !string.IsNullOrWhiteSpace(player.Cell))
                        {
                            string room = map.RoomNumber > 0 ? map.RoomNumber.ToString() : string.Empty;
                            string locationFile = System.IO.Path.Combine(dir, $"{Environment.ProcessId}.location.json");
                            string temporaryFile = locationFile + ".tmp";
                            string locationJson = JsonSerializer.Serialize(new
                            {
                                Map = map.Name,
                                Room = room,
                                Cell = player.Cell,
                                Pad = string.IsNullOrWhiteSpace(player.Pad) ? "Spawn" : player.Pad,
                                Username = username,
                                UpdatedUtc = DateTime.UtcNow
                            });
                            System.IO.File.WriteAllText(temporaryFile, locationJson);
                            System.IO.File.Move(temporaryFile, locationFile, true);
                        }
                    }
                }
                catch { }

                if ((++tick & 1) == 0)
                {
                    WriteTabState();
                    PublishSharedOptions();
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunOptionCommandLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
                ApplySharedOptionCommands();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void WriteTabState()
    {
        try
        {
            Skua.Core.Services.ArmyOptionFeedback.Publish(Ioc.Default.GetRequiredService<IScriptOption>(), Environment.ProcessId);
            var manager = Ioc.Default.GetRequiredService<IScriptManager>();
            var player = Ioc.Default.GetRequiredService<IScriptPlayer>();
            if (manager.ScriptRunning)
                _lastScriptRunning = DateTime.UtcNow;
            bool running = manager.ScriptRunning || (DateTime.UtcNow - _lastScriptRunning < TimeSpan.FromMinutes(5) && !player.Playing);
            string state = $"{manager.LoadedScript}\n{(running ? 1 : 0)}";
            if (state == _lastTabState)
                return;
            _lastTabState = state;
            string dir = ClientFileSources.TabsDIR;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"{Environment.ProcessId}.state"), state);
        }
        catch { }
    }

    private void PublishSharedOptions()
    {
        try
        {
            string account = $"Not logged in ({Environment.ProcessId})";
            var player = Ioc.Default.GetRequiredService<IScriptPlayer>();
            if (player.LoggedIn && !string.IsNullOrWhiteSpace(player.Username))
                account = player.Username;
            Skua.Core.Services.OptionSharing.Publish(_options, Environment.ProcessId, account);
        }
        catch { }
    }

    private void ApplySharedOptionCommands()
    {
        try
        {
            var commands = Skua.Core.Services.OptionSharing.Take(Environment.ProcessId);
            if (commands.Count == 0)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var command in commands)
                {
                    try { Skua.Core.Services.OptionSharing.Apply(_options, command); }
                    catch { }
                }
                PublishSharedOptions();
            });
        }
        catch { }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        Skua.Core.Services.OptionSharing.Remove(Environment.ProcessId);
        App.DisposeTrayIcon();
        _ipcCancelCts.Cancel();
        try
        {
            string dir = ClientFileSources.TabsDIR;
            string file = System.IO.Path.Combine(dir, $"{Environment.ProcessId}.txt");
            string stateFile = System.IO.Path.Combine(dir, $"{Environment.ProcessId}.state");
            string locationFile = System.IO.Path.Combine(dir, $"{Environment.ProcessId}.location.json");
            string temporaryFile = locationFile + ".tmp";
            if (System.IO.File.Exists(file))
                System.IO.File.Delete(file);
            if (System.IO.File.Exists(stateFile))
                System.IO.File.Delete(stateFile);
            if (System.IO.File.Exists(locationFile))
                System.IO.File.Delete(locationFile);
            if (System.IO.File.Exists(temporaryFile))
                System.IO.File.Delete(temporaryFile);
        }
        catch { }

        _flash.FlashCall -= OnFlashCall;
        _startup.Dispose();
        _flash.Dispose();
        StrongReferenceMessenger.Default.UnregisterAll(this);
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }
}
