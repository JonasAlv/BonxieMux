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
    private DispatcherTimer? _tabTitleSyncTimer;
    private DispatcherTimer? _throttleTimer;
    private DispatcherTimer? _optionCommandTimer;
    private string _lastTabState = "";
    private DateTime _lastScriptRunning = DateTime.MinValue;
    private bool _isFlashLoaded;
    private bool _isTitleBarPermanentlyHidden = false;
    private readonly CancellationTokenSource _ipcCancelCts = new();

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
            MaterialIconKind? iconKind = viewModel.Header switch
            {
                "Scripts" => MaterialIconKind.ScriptTextOutline,
                "Options" => MaterialIconKind.CogOutline,
                "Tools & Helpers" => MaterialIconKind.WrenchOutline,
                "Combat" => MaterialIconKind.SwordCross,
                "Bank" => MaterialIconKind.BankOutline,
                "Diagnostics" => MaterialIconKind.BugOutline,
                "Plugins" => MaterialIconKind.PuzzleOutline,
                _ => null
            };

            if (iconKind.HasValue)
            {
                var icon = new MaterialIcon
                {
                    Kind = iconKind.Value,
                    Width = 14,
                    Height = 14,
                    VerticalAlignment = VerticalAlignment.Center
                };
                icon[!MaterialIcon.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaTextBrush");

                var text = new TextBlock
                {
                    Text = viewModel.Header,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 12
                };
                menuItem.Header = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children = { icon, text }
                };
            }
            else
            {
                menuItem.Header = viewModel.Header;
            }
        }
        else
        {
            MaterialIconKind? subIconKind = viewModel.Header switch
            {
                "Script Loader" => MaterialIconKind.ScriptOutline,
                "Scheduler" => MaterialIconKind.CalendarClock,
                "Game" => MaterialIconKind.GamepadVariantOutline,
                "Watch Dog" => MaterialIconKind.ShieldCheckOutline,
                "Application" => MaterialIconKind.ApplicationCogOutline,
                "CoreBots" => MaterialIconKind.RobotOutline,
                "Application Themes" => MaterialIconKind.PaletteOutline,
                "HotKeys" => MaterialIconKind.KeyboardOutline,
                "Loadouts Manager" => MaterialIconKind.AccountSwitchOutline,
                "Fast Travel" => MaterialIconKind.CompassOutline,
                "Loader" => MaterialIconKind.DownloadOutline,
                "Grabber" => MaterialIconKind.HandBackRightOutline,
                "Current Drops" => MaterialIconKind.GiftOutline,
                "Junk Items" => MaterialIconKind.DeleteSweepOutline,
                "Calendar" => MaterialIconKind.CalendarMonthOutline,
                "Skills" => MaterialIconKind.LightningBoltOutline,
                "Stats" => MaterialIconKind.ChartLine,
                "Runtime" => MaterialIconKind.PlayNetworkOutline,
                "Logs" => MaterialIconKind.FileDocumentOutline,
                "Console" => MaterialIconKind.Console,
                "Spammer" => MaterialIconKind.SendClockOutline,
                "Logger" => MaterialIconKind.TextSearch,
                "Interceptor" => MaterialIconKind.ShieldBugOutline,
                _ => null
            };

            if (subIconKind.HasValue)
            {
                var subIcon = new MaterialIcon
                {
                    Kind = subIconKind.Value,
                    Width = 14,
                    Height = 14,
                    VerticalAlignment = VerticalAlignment.Center
                };
                subIcon[!MaterialIcon.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaMutedTextBrush");
                menuItem.Icon = subIcon;
            }
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
            ReleaseAllPressedKeys();
            Skua.App.Avalonia.Controls.GameHost.ForwardFocus(false);
        };
        Activated += (s, e) =>
        {
            ReleaseAllPressedKeys();
            Skua.App.Avalonia.Controls.GameHost.ForwardFocus(true);
        };

        if (_options is INotifyPropertyChanged notifyOptions)
        {
            notifyOptions.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName is nameof(IScriptOption.AutoHeadless)
                    or nameof(IScriptOption.AutoHeadlessBackgroundTabs)
                    or nameof(IScriptOption.AutoHeadlessGridView)
                    or nameof(IScriptOption.AutoHeadlessWhenMinimized)
                    or nameof(IScriptOption.AutoHeadlessLiftOnFocus))
                {
                    Dispatcher.UIThread.Post(EvaluateAutoHeadless);
                }
                else if (e.PropertyName is nameof(IScriptOption.HeadlessMode) or Skua.Core.Scripts.ScriptOption.AutoHeadlessActiveChanged)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        // Turning manual headless off also releases an automatic headless hold.
                        // Otherwise a tab can remain hidden after it becomes visible again.
                        if (e.PropertyName == nameof(IScriptOption.HeadlessMode) && !_options.HeadlessMode)
                        {
                            _autoHeadlessHeld = false;
                            EvaluateAutoHeadless();
                        }
                        bool isHeadless = _options.IsHeadlessNow();
                        if (HeadlessOverlay != null) HeadlessOverlay.IsVisible = isHeadless;
                        if (FlashHost != null)
                        {
                            FlashHost.IsVisible = !isHeadless && _isFlashLoaded;
                        }
                    });
                }
            };
        }
    }

    private void MainWindow_Opened(object? sender, EventArgs e)
    {
        _startup.Execute();
        HotKeys.OpenCommandPaletteRequested = () => Dispatcher.UIThread.Post(() =>
        {
            try
            {
                new CommandPaletteWindow(_mainMenuVm.MainMenuItems).ShowDialog(this);
            }
            catch { }
        });
        HotKeys.ToggleFullscreenRequested = () => Dispatcher.UIThread.Post(() =>
        {
            WindowState = WindowState == WindowState.FullScreen
                ? WindowState.Normal
                : WindowState.FullScreen;
        });
        _flash.FlashCall += OnFlashCall;

        try
        {
            var themeService = Ioc.Default.GetService<IThemeService>();
            bool isDark = themeService?.IsDarkTheme ?? true;
            Skua.Shared.Avalonia.Services.ThemeResourceApplicator.ApplyTitleBarTheme(this, isDark);
        }
        catch { }

        Focusable = true;
        InitializeLinuxInput();

        if (FlashHost != null)
        {
            bool isHeadless = _options.IsHeadlessNow();
            FlashHost.IsVisible = !isHeadless;
            Dispatcher.UIThread.RunJobs();
            if (!isHeadless)
                FlashHost.Focus();
        }
        if (_flash is Flash.FlashUtil player)
            player.ParentWindow = FlashHost?.WindowHandle ?? 0;
        _flash.InitializeFlash();
        TabIpc.StartListener(this, Environment.ProcessId, _ipcCancelCts.Token);

        _tabTitleSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        string lastWrittenUsername = "";
        _tabTitleSyncTimer.Tick += (s, ev) =>
        {
            try
            {
                var bot = Ioc.Default.GetRequiredService<IScriptInterface>();
                if (!bot.Player.LoggedIn || string.IsNullOrWhiteSpace(bot.Player.Username))
                    return;

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
                IScriptOption options = Ioc.Default.GetRequiredService<IScriptOption>();
                if (!map.Loaded || string.IsNullOrWhiteSpace(map.Name) || string.IsNullOrWhiteSpace(player.Cell))
                    return;

                // Report the server-assigned room number rather than the internal area ID or requested private-room number.
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
            catch { }
        };
        _tabTitleSyncTimer.Start();

        _throttleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _throttleTimer.Tick += (_, _) =>
        {
            WriteTabState();
            PublishSharedOptions();
        };
        _throttleTimer.Start();

        _optionCommandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _optionCommandTimer.Tick += (_, _) => ApplySharedOptionCommands();
        _optionCommandTimer.Start();
        PublishSharedOptions();
    }

    /// <summary>
    /// Tells the tab host which script this client has loaded and whether it is running, so it can start
    /// the client again with that script if the client crashes ({ClientFileSources.TabsDIR}/{pid}.state).
    /// </summary>
    private void WriteTabState()
    {
        try
        {
            Skua.Core.Services.ArmyOptionFeedback.Publish(Ioc.Default.GetRequiredService<IScriptOption>(), Environment.ProcessId);
            var manager = Ioc.Default.GetRequiredService<IScriptManager>();
            var player = Ioc.Default.GetRequiredService<IScriptPlayer>();
            if (manager.ScriptRunning)
                _lastScriptRunning = DateTime.UtcNow;
            // Still "running" while it is being brought back after a crash or disconnect.
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

    /// <summary>Tells the other clients' Game Options windows who this is and what every option is set to.</summary>
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

    /// <summary>Makes the changes another client's Game Options window asked for (set an option, save, reset).</summary>
    private void ApplySharedOptionCommands()
    {
        try
        {
            var commands = Skua.Core.Services.OptionSharing.Take(Environment.ProcessId);
            if (commands.Count == 0)
                return;
            foreach (var command in commands)
            {
                try { Skua.Core.Services.OptionSharing.Apply(_options, command); }
                catch { }
            }
            PublishSharedOptions();
        }
        catch { }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _throttleTimer?.Stop();
        _optionCommandTimer?.Stop();
        Skua.Core.Services.OptionSharing.Remove(Environment.ProcessId);
        App.DisposeTrayIcon();
        _tabTitleSyncTimer?.Stop();
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

        _ipcCancelCts.Cancel();
        _flash.FlashCall -= OnFlashCall;
        _startup.Dispose();
        _flash.Dispose();
        StrongReferenceMessenger.Default.UnregisterAll(this);
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }
}
