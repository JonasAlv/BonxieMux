using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Material.Icons;
using Material.Icons.Avalonia;
using Skua.App.Avalonia.Services;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Core.AppStartup;
using Skua.Shared.Avalonia.ViewModels;
using Skua.Shared.Avalonia.ViewModels.ScriptRepo;
using Skua.Shared.Avalonia.ViewModels.Dialogs;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Skua.App.Avalonia.Views;

public partial class TabbedHostWindow : Window
{
    private IntPtr SendChildMessage(TabInfo info, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == (uint)WM_SKUA_CHECK_LOGIN)
        {
            int pid = info.Process?.Id ?? 0;
            if (pid != 0)
            {
                string file = Path.Combine(ClientFileSources.TabsDIR, $"{pid}.txt");
                if (File.Exists(file) && !string.IsNullOrWhiteSpace(File.ReadAllText(file)))
                    return new IntPtr(1);
            }
        }
        return IntPtr.Zero;
    }

    #region Tab State
    private class TabInfo
    {
        public Process Process { get; set; } = null!;
        public IntPtr ChildHwnd { get; set; } = IntPtr.Zero;
        public bool IsThrottled { get; set; } = false;
        public bool IsCustomNamed { get; set; } = false;
        public bool IsLoaded { get; set; } = false;
        public string AccountOrderKey { get; set; } = "";
        // The tab header, for the tooltip of a follow group (see FollowGroups.cs).
        public StackPanel? HeaderPanel { get; set; }
        // The crown over the icon of the account the others follow.
        public MaterialIcon? Crown { get; set; }
    }

    private readonly string[] _initialArgs;
    private bool _initialInstanceStarted = false;
    private readonly Dictionary<TabItem, TabInfo> _tabs = new();
    private bool _needsReposition = false;
    private bool _repositionScheduled = false;
    private bool _isRepositioning = false;
    private bool _isTogglingGridView = false;
    private DateTime _lastGridViewToggleTime = DateTime.MinValue;
    private readonly TimeSpan _gridViewToggleCooldown = TimeSpan.FromMilliseconds(300);
    private bool _isClosing = false;
    private readonly CancellationTokenSource _pipeCts = new();
    private bool _isGridViewEnabled = false;
    private TabItem? _lastSelectedTab;
    private IntPtr _hostHwnd = IntPtr.Zero;
    private int _lastX = -99999, _lastY = -99999, _lastW = -99999, _lastH = -99999;
    private IntPtr _lastActiveHwnd = IntPtr.Zero;
    private bool _lastGridViewState = false;
    private int _lastTabCount = 0;
    private int _childOffsetX = 0;
    private int _childOffsetY = 0;
    private int _childWidth = 0;
    private int _childHeight = 0;
    private int _lastFastX = -99999;
    private int _lastFastY = -99999;
    private bool _isDraggingWindow = false;

    private struct GridTile
    {
        public IntPtr Hwnd;
        public int OffsetX;
        public int OffsetY;
        public int Width;
        public int Height;
    }
    private readonly List<GridTile> _gridTiles = new();

    private Queue<Action> _spawnQueue = new Queue<Action>();
    private bool _isSpawning = false;

    private void EnqueueSpawn(Action spawnAction)
    {
        _spawnQueue.Enqueue(spawnAction);
        ProcessNextSpawn();
    }

    private void ProcessNextSpawn()
    {
        if (_isSpawning || _spawnQueue.Count == 0 || _isClosing) return;
        
        _isSpawning = true;
        Action next = _spawnQueue.Dequeue();
        next.Invoke();
    }
    #endregion

    public TabbedHostWindow() : this(Array.Empty<string>()) { }

    internal static string AccountKey(string[] args)
    {
        int i = Array.IndexOf(args, "-u");
        if (i < 0)
            i = Array.IndexOf(args, "--user");
        return i >= 0 && i + 1 < args.Length ? args[i + 1].ToLowerInvariant() : "";
    }

    private void RecordClientLocation(int pid, ArmyClientLocation location)
    {
        _latestClientLocations[pid] = location;
        string signature = $"{location.Map}|{location.Room}";
        if (_lastRecordedLocationByPid.TryGetValue(pid, out string? previousSignature)
            && previousSignature.Equals(signature, StringComparison.OrdinalIgnoreCase))
            return;

        _lastRecordedLocationByPid[pid] = signature;
        _jumpMapHistory.RemoveAll(item =>
            item.Map.Equals(location.Map, StringComparison.OrdinalIgnoreCase)
            && item.Room.Equals(location.Room, StringComparison.OrdinalIgnoreCase));

        _jumpMapHistory.Insert(0, new ArmyJumpDestination
        {
            Name = location.Username,
            Map = location.Map,
            Room = location.Room,
            Cell = string.IsNullOrWhiteSpace(location.Cell) ? "Enter" : location.Cell,
            Pad = string.IsNullOrWhiteSpace(location.Pad) ? "Spawn" : location.Pad
        });

        if (_jumpMapHistory.Count > 15)
            _jumpMapHistory.RemoveRange(15, _jumpMapHistory.Count - 15);

        SaveArmyJumpSettings();
    }


    public TabbedHostWindow(string rawInitialArgs) : this(ParseRawCommandLine(rawInitialArgs)) { }

    public TabbedHostWindow(string[]? initialArgs)
    {
        _initialArgs = initialArgs ?? Array.Empty<string>();
        InitializeComponent();
        Skua.Shared.Avalonia.Services.WindowResizeHelper.Attach(this);
        Title = $"BonxieMux V{Skua.Core.Models.ClientFileSources.DisplayVersion}";
        LoadArmyJumpSettings();
        MainTitleBar.TitleText = Title;

        try
        {
            IThemeService? themeService = Ioc.Default.GetService<IThemeService>();
            Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar.SetArtworkPath(
                Ioc.Default.GetRequiredService<ISettingsService>().GetShared().TitleBarArtworkPath);

            if (themeService is Skua.Shared.Avalonia.Services.AvaloniaThemeService avaloniaTheme)
            {
                MainTitleBar.UseAccentBackground = avaloniaTheme.UseAccentOnMainTitleBar;
                Skua.Shared.Avalonia.Services.ThemeResourceApplicator.ApplyTitleBarTheme(this, themeService.IsDarkTheme,
                    avaloniaTheme.UseAccentOnMainTitleBar ? avaloniaTheme.PrimaryColor : (Color?)null,
                    avaloniaTheme.UseAccentOnMainTitleBar ? avaloniaTheme.PrimaryForegroundColor : (Color?)null);
            }
            else
            {
                MainTitleBar.UseAccentBackground = false;
                Skua.Shared.Avalonia.Services.ThemeResourceApplicator.ApplyTitleBarTheme(this, themeService?.IsDarkTheme);
            }
        }
        catch { }

        var options = Ioc.Default.GetRequiredService<IScriptOption>();
        MiscOptionsMenu.DataContext = options;
        options.PropertyChanged += Options_PropertyChanged;

        Opened += TabbedHostWindow_Opened;
        PositionChanged += (s, e) => OnWindowPositionChanged(e.Point);
        SizeChanged += (s, e) => { ScheduleReposition(); UpdateTabScrollButtons(); };
        ScalingChanged += (s, e) => Dispatcher.UIThread.Post(RepositionWithFreshCache, DispatcherPriority.Loaded);
        
        PropertyChanged += (s, e) =>
        {
            if (e.Property != WindowStateProperty && e.Property != IsVisibleProperty)
                return;

            if ((e.Property == WindowStateProperty && WindowState == WindowState.Minimized) ||
                (e.Property == IsVisibleProperty && !IsVisible))
                CloseHostDropdowns();

            // Minimize/hidden handling unmaps X11 children. Invalidate the cache on restore so
            // DoReposition calls XMapWindow again instead of returning on unchanged geometry.
            if (WindowState != WindowState.Minimized && IsVisible)
            {
                _lastX = int.MinValue;
                _lastY = int.MinValue;
                _lastW = int.MinValue;
                _lastH = int.MinValue;
                _lastActiveHwnd = IntPtr.Zero;
            }

            ScheduleReposition();
        };
        Activated += (s, e) =>
        {
            ScheduleReposition();
            UpdateTabScrollButtons();
            if (_isGridViewEnabled)
                foreach (var info in _tabs.Values)
                    if (info.ChildHwnd != IntPtr.Zero)
                        PostChildMessage(info.ChildHwnd, (uint)HotKeys.WM_SKUA_FOCUS, new IntPtr(1), IntPtr.Zero);
            else if (HotKeys.ActiveChildHwnd != IntPtr.Zero)
                PostChildMessage(HotKeys.ActiveChildHwnd, (uint)HotKeys.WM_SKUA_FOCUS, new IntPtr(1), IntPtr.Zero);
        };
        StartGridFocusTracking();
        Deactivated += (s, e) =>
        {
            CloseHostDropdowns();
            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero)
                    PostChildMessage(info.ChildHwnd, (uint)HotKeys.WM_SKUA_FOCUS, IntPtr.Zero, IntPtr.Zero);
            }
        };
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;

        StrongReferenceMessenger.Default.Register<TabbedHostWindow, LoadScriptMessage, int>(this, (int)MessageChannels.ScriptStatus, (r, m) => r.BroadcastScriptAction(m.Path, WM_SKUA_LOAD_SCRIPT));
        StrongReferenceMessenger.Default.Register<TabbedHostWindow, ScriptSchedulerViewModel.ArmySchedulerMessage>(this, (r, m) => r.OnArmySchedulerStart(m));
        StrongReferenceMessenger.Default.Register<TabbedHostWindow, ScriptSchedulerViewModel.ArmySchedulerStopMessage>(this, (r, m) => r.OnArmySchedulerStop(m));

        StartPipeServer();

        TabScrollViewer.PointerWheelChanged += (s, e) =>
        {
            if (e.Delta.Y != 0)
            {
                var newOffset = TabScrollViewer.Offset.X - (e.Delta.Y * 80);
                TabScrollViewer.Offset = new Vector(Math.Max(0, newOffset), 0);
                UpdateTabScrollButtons();
                e.Handled = true;
            }
        };

        TabScrollViewer.PropertyChanged += (s, e) =>
        {
            if (e.Property == ScrollViewer.OffsetProperty ||
                e.Property == ScrollViewer.ExtentProperty ||
                e.Property == ScrollViewer.ViewportProperty)
            {
                UpdateTabScrollButtons();
            }
        };

        var titleSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        titleSyncTimer.Tick += (s, e) => SyncTabTitles();
        titleSyncTimer.Start();

        _gridLayout = NormalGridLayout(Ioc.Default.GetService<ISettingsService>()?.Get("BonxieMuxGridLayout", "auto"));
        UpdateGridLayoutButton();
        _gameSize = ParseGameSize(Ioc.Default.GetService<ISettingsService>()?.Get("BonxieMuxGameSize", "auto"));
        UpdateGameSizeButton();
        GridViewBorder.PointerEntered += GridViewBorder_PointerEntered;
        GridViewBorder.PointerExited += GridViewBorder_PointerExited;
        GlobeBorder.PointerEntered += GlobeBorder_PointerEntered;
        GlobeBorder.PointerExited += GlobeBorder_PointerExited;
    }

    private void SyncTabTitles()
    {
        if (_isClosing || _tabs.Count == 0) return;

        try { UpdateFollowGroups(); } catch { }

        var entries = _tabs.Select(kvp => (Tab: kvp.Key, Pid: kvp.Value?.Process?.Id ?? 0)).ToList();
        
        Task.Run(() =>
        {
            string dir = ClientFileSources.TabsDIR;
            if (!Directory.Exists(dir)) return;

            var updates = new List<(TabItem Tab, string Username)>();
            var locations = new List<(int Pid, ArmyClientLocation Location)>();

            foreach (var (tab, pid) in entries)
            {
                if (pid == 0) continue;
                try
                {
                    string file = Path.Combine(dir, $"{pid}.txt");
                    if (File.Exists(file))
                    {
                        string username = File.ReadAllText(file).Trim();
                        if (!string.IsNullOrWhiteSpace(username))
                            updates.Add((tab, username));
                    }

                    string locationFile = Path.Combine(dir, $"{pid}.location.json");
                    if (File.Exists(locationFile))
                    {
                        ArmyClientLocation? location = JsonSerializer.Deserialize<ArmyClientLocation>(File.ReadAllText(locationFile));
                        if (location != null
                            && !string.IsNullOrWhiteSpace(location.Map)
                            && location.UpdatedUtc >= DateTime.UtcNow.AddSeconds(-5))
                            locations.Add((pid, location));
                    }
                }
                catch { }
            }

            if ((updates.Count > 0 || locations.Count > 0) && !_isClosing)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_isClosing) return;

                    foreach (var (pid, location) in locations)
                        RecordClientLocation(pid, location);

                    foreach (var (tab, username) in updates)
                    {
                        if (_tabs.TryGetValue(tab, out TabInfo? tInfo) && tInfo.IsCustomNamed)
                            continue;

                        if (tab.Header is StackPanel headerPanel)
                        {
                            var titleBlock = headerPanel.Children.OfType<TextBlock>().FirstOrDefault(tb => tb.Text != "✕");
                            var editBox = headerPanel.Children.OfType<TextBox>().FirstOrDefault();
                            if (titleBlock != null && (titleBlock.Text.StartsWith("User ") || titleBlock.Text.StartsWith("Skua ") || titleBlock.Text != username))
                            {
                                titleBlock.Text = username;
                                if (editBox != null) editBox.Text = username;
                            }
                        }
                    }
                }, DispatcherPriority.Background);
            }
        });
    }

    #region Lifecycle
    private void TabbedHostWindow_Opened(object? sender, EventArgs e)
    {
        _hostHwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

        // F11 is grabbed by the host while an embedded Mugetsu child owns focus.
        // Toggle the host window here; the child process must not be fullscreened.
        HotKeys.ToggleFullscreenRequested = () => Dispatcher.UIThread.Post(() =>
        {
            WindowState = WindowState == WindowState.FullScreen
                ? WindowState.Normal
                : WindowState.FullScreen;
        });

        HotKeys.RegisterSkuaPid((uint)Environment.ProcessId);
        HotKeys.GetChildHwndForPid = (pid) =>
        {
            var tab = _tabs.Values.FirstOrDefault(t => t.Process != null && (uint)t.Process.Id == pid);
            return tab?.ChildHwnd ?? IntPtr.Zero;
        };
        HotKeys.ForwardHotKeyAction = (actionName) =>
        {
            IntPtr childHwnd = HotKeys.ActiveChildHwnd;
            if (childHwnd != IntPtr.Zero)
            {
                if (actionName == "OpenBank")
                {
                    PostChildMessage(childHwnd, HotKeys.WM_SKUA_OPEN_BANK, IntPtr.Zero, IntPtr.Zero);
                    return;
                }

                int actionId = HotKeys.GetActionId(actionName);
                if (actionId >= 0)
                {
                    PostChildMessage(childHwnd, HotKeys.WM_SKUA_EXECUTE_HOTKEY, new IntPtr(actionId), IntPtr.Zero);
                }
            }
        };
        HotKeys.BroadcastArmyMessageAction = (msg, wParam, lParam) =>
        {
            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero)
                {
                    PostChildMessage(info.ChildHwnd, msg, new IntPtr(wParam), new IntPtr(lParam));
                }
            }
        };
        HotKeys.BroadcastReloadHotkeysAction = () =>
        {
            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero)
                {
                    PostChildMessage(info.ChildHwnd, HotKeys.WM_SKUA_RELOAD_HOTKEYS, IntPtr.Zero, IntPtr.Zero);
                }
            }
        };

        try
        {
            var themeService = Ioc.Default.GetService<IThemeService>();
            if (themeService != null)
            {
                void RefreshNativeTitleBar()
                {
                    if (themeService is Skua.Shared.Avalonia.Services.AvaloniaThemeService currentTheme)
                    {
                        MainTitleBar.UseAccentBackground = currentTheme.UseAccentOnMainTitleBar;
                        Skua.Shared.Avalonia.Services.ThemeResourceApplicator.ApplyTitleBarTheme(this, themeService.IsDarkTheme,
                            currentTheme.UseAccentOnMainTitleBar ? currentTheme.PrimaryColor : (Color?)null,
                            currentTheme.UseAccentOnMainTitleBar ? currentTheme.PrimaryForegroundColor : (Color?)null);
                    }
                    else
                    {
                        MainTitleBar.UseAccentBackground = false;
                        Skua.Shared.Avalonia.Services.ThemeResourceApplicator.ApplyTitleBarTheme(this, themeService.IsDarkTheme);
                    }
                }

                // Preset changes and single-scheme edits use separate events. Listen to both so
                // changing a custom primary/foreground color cannot leave the native caption stale.
                themeService.ThemeChanged += _ => Dispatcher.UIThread.Post(RefreshNativeTitleBar, DispatcherPriority.Background);
                themeService.SchemeChanged += (_, _) => Dispatcher.UIThread.Post(RefreshNativeTitleBar, DispatcherPriority.Background);
                RefreshNativeTitleBar();
            }
        }
        catch { }

        try
        {
            Ioc.Default.GetRequiredService<IHotKeyService>().Reload();
        }
        catch { }

        if (!_initialInstanceStarted)
        {
            _initialInstanceStarted = true;
            AddNewInstance(_initialArgs);
        }
    }



    private void AttachChildWindow(TabInfo info, IntPtr childHwnd)
    {
        if (info.ChildHwnd != IntPtr.Zero) return;
        
        info.ChildHwnd = childHwnd;
        if (info.Process != null)
            HotKeys.RegisterSkuaPid((uint)info.Process.Id);

        PlatformReparentChild(childHwnd);

        TabItem? selectedTab = InstancesTabControl.SelectedItem as TabItem;
        bool isSelected = (selectedTab != null && _tabs.TryGetValue(selectedTab, out var selInfo) && selInfo == info);

        if (_isGridViewEnabled)
        {
            info.IsThrottled = false;
            PostChildMessage(childHwnd, WM_SKUA_GRIDVIEW, new IntPtr(1), IntPtr.Zero);
            PostChildMessage(childHwnd, WM_SKUA_THROTTLE, IntPtr.Zero, IntPtr.Zero);
            DoReposition();
        }
        else if (isSelected)
        {
            HotKeys.ActiveChildHwnd = childHwnd;
            _lastActiveHwnd = IntPtr.Zero; // Invalidate cache so DoReposition maps and positions child
            DoReposition();
        }
        else
        {
            // Inactive tab: ensure it stays unmapped, offscreen and throttled so it never shows over active tab
            info.IsThrottled = true;
            PlatformHideChild(childHwnd);
            PostChildMessage(childHwnd, WM_SKUA_THROTTLE, new IntPtr(1), IntPtr.Zero);
            PostChildMessage(childHwnd, (uint)HotKeys.WM_SKUA_FOCUS, IntPtr.Zero, IntPtr.Zero);
        }

        if (info.Process != null && !info.IsLoaded)
        {
            int childPid = info.Process.Id;
            string lFile = Path.Combine(ClientFileSources.TabsDIR, $"loaded_{childPid}.txt");
            Task.Run(async () =>
            {
                for (int i = 0; i < 600; i++)
                {
                    await Task.Delay(50);
                    if (info.IsLoaded || info.ChildHwnd == IntPtr.Zero || _isClosing) break;
                    if (File.Exists(lFile))
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            info.IsLoaded = true;
                            _lastActiveHwnd = IntPtr.Zero;
                            DoReposition();
                        });
                        break;
                    }
                }
            });
        }

        _isSpawning = false;
        ProcessNextSpawn();
    }

    private static void KillProcessTree(Process? process)
    {
        if (process == null) return;
        try
        {
            try
            {
                using var pkill = Process.Start(new ProcessStartInfo
                {
                    FileName = "pkill",
                    Arguments = $"-9 -P {process.Id}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            catch { }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch { }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        _isClosing = true;
        App.DisposeTrayIcon();
        try { _pipeCts.Cancel(); } catch { }

        var tabsToClose = _tabs.Values.ToList();
        _tabs.Clear();

        foreach (var info in tabsToClose)
        {
            try
            {
                KillProcessTree(info.Process);
                if (info.ChildHwnd != IntPtr.Zero)
                {
                    PlatformUnmapChild(info.ChildHwnd);
                }
                if (info.Process != null)
                {
                    TabIpc.CleanUp(info.Process.Id);
                    try { File.Delete(Path.Combine(ClientFileSources.TabsDIR, $"{info.Process.Id}.txt")); } catch { }
                    try { File.Delete(Path.Combine(ClientFileSources.TabsDIR, $"ready_{info.Process.Id}.txt")); } catch { }
                    try { File.Delete(Path.Combine(ClientFileSources.TabsDIR, $"loaded_{info.Process.Id}.txt")); } catch { }
                }
                if (info.Process != null)
                {
                    try { File.Delete(Path.Combine(ClientFileSources.TabsDIR, $"{info.Process.Id}.location.json")); } catch { }
                    _latestClientLocations.Remove(info.Process.Id);
                    _lastRecordedLocationByPid.Remove(info.Process.Id);
                }
            }
            catch { }
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        App.DisposeTrayIcon();
        try
        {
            App.HostLockStream?.Dispose();
            App.HostLockStream = null;
            if (System.IO.Path.IsPathRooted(App.HostPipeName) && System.IO.File.Exists(App.HostPipeName))
                System.IO.File.Delete(App.HostPipeName);
        }
        catch { }

        lock (_xLock)
        {
            if (_xDisplay != IntPtr.Zero)
            {
                try { XCloseDisplay(_xDisplay); } catch { }
                _xDisplay = IntPtr.Zero;
            }
        }

        Task.Run(async () =>
        {
            await Task.Delay(300);
            Environment.Exit(0);
        });
    }
    #endregion

    #region Pipe Server
    private void StartPipeServer()
    {
        Task.Run(async () =>
        {
            while (!_isClosing && !_pipeCts.Token.IsCancellationRequested)
            {
                NamedPipeServerStream? pipeServer = null;
                try
                {
                    pipeServer = new NamedPipeServerStream(
                        App.HostPipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    using var reg = _pipeCts.Token.Register(() =>
                    {
                        try { pipeServer?.Dispose(); } catch { }
                    });

                    await pipeServer.WaitForConnectionAsync(_pipeCts.Token);

                    using (pipeServer)
                    using (StreamReader sr = new(pipeServer))
                    {
                        string? line = await sr.ReadLineAsync();
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            if (line.StartsWith("ARTWORK|", StringComparison.Ordinal))
                            {
                                string encodedPath = line.Substring("ARTWORK|".Length);
                                Dispatcher.UIThread.Post(() =>
                                {
                                    try
                                    {
                                        string artworkPath = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedPath));
                                        Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar.SetArtworkPath(
                                            string.IsNullOrWhiteSpace(artworkPath) ? null : artworkPath);
                                    }
                                    catch (Exception ex)
                                    {
                                        Trace.WriteLine($"BonxieMux artwork sync failed: {ex.GetType().Name}: {ex.Message}");
                                    }
                                });
                            }
                            else if (line.StartsWith("THEME|", StringComparison.Ordinal))
                            {
                                string themePayload = line.Substring("THEME|".Length);
                                bool useAccentOnMainTitleBar = true;
                                string themeCsv = themePayload;
                                int separatorIndex = themePayload.IndexOf('|');
                                if (separatorIndex >= 0 &&
                                    bool.TryParse(themePayload[..separatorIndex], out bool parsedAccentSetting))
                                {
                                    useAccentOnMainTitleBar = parsedAccentSetting;
                                    themeCsv = themePayload[(separatorIndex + 1)..];
                                }

                                Dispatcher.UIThread.Post(() =>
                                {
                                    try
                                    {
                                        Skua.Shared.Avalonia.Services.ThemeItem? themeItem =
                                            Skua.Shared.Avalonia.Services.ThemeItem.FromString(themeCsv);
                                        if (themeItem is not null &&
                                            Ioc.Default.GetService<IThemeService>() is Skua.Shared.Avalonia.Services.AvaloniaThemeService themeService)
                                        {
                                            themeService.ApplyExternalThemeSnapshot(themeItem, useAccentOnMainTitleBar);
                                            MainTitleBar.UseAccentBackground = useAccentOnMainTitleBar;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Trace.WriteLine($"BonxieMux theme sync failed: {ex.GetType().Name}: {ex.Message}");
                                    }
                                });
                            }
                            else
                            {
                                string[]? parsedArgs = null;
                                try
                                {
                                    if (line.TrimStart().StartsWith("["))
                                    {
                                        parsedArgs = System.Text.Json.JsonSerializer.Deserialize<string[]>(line);
                                    }
                                }
                                catch { }

                                parsedArgs ??= ParseRawCommandLine(line);
                                Dispatcher.UIThread.Post(() => AddNewInstance(parsedArgs));
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    try { pipeServer?.Dispose(); } catch { }
                    break;
                }
                catch
                {
                    try { pipeServer?.Dispose(); } catch { }
                    if (_isClosing || _pipeCts.Token.IsCancellationRequested) break;
                    await Task.Delay(100);
                }
            }

            if (System.IO.Path.IsPathRooted(App.HostPipeName))
            {
                try
                {
                    if (System.IO.File.Exists(App.HostPipeName))
                        System.IO.File.Delete(App.HostPipeName);
                }
                catch { }
            }
        });
    }

    private static string[] ParseRawCommandLine(string? cmdLine)
    {
        if (string.IsNullOrWhiteSpace(cmdLine))
            return Array.Empty<string>();

        List<string> args = new();
        bool inQuotes = false;
        var current = new System.Text.StringBuilder();

        for (int i = 0; i < cmdLine.Length; i++)
        {
            char c = cmdLine[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            args.Add(current.ToString());

        return args.ToArray();
    }
    #endregion

    #region Tab Switching & Positioning
    private const int WM_SKUA_GRIDVIEW = 0x0400 + 444;
    private const int WM_SKUA_START_SCRIPT = 0x0400 + 445;
    private const int WM_SKUA_STOP_SCRIPT = 0x0400 + 446;
    private const int WM_SKUA_LOGIN = 0x0400 + 447;
    private const int WM_SKUA_LOGOUT = 0x0400 + 448;
    private const int WM_SKUA_JUMP_MAP = 0x0400 + 449;
    private const int WM_SKUA_SET_OPTION = 0x0400 + 450;
    private const int WM_SKUA_THROTTLE = 0x0400 + 452;
    private const int WM_SKUA_LOAD_SCRIPT = 0x0400 + 453;
    private const int WM_SKUA_ARMY_SCHEDULER = 0x0400 + 454;
    private const int WM_SKUA_ARMY_SCHEDULER_STOP = 0x0400 + 455;
    private const int WM_SKUA_CHECK_LOGIN = 0x0400 + 456;
    private const int WM_SKUA_JUMP_PLAYER = 0x0400 + 457;
    private const int WM_SKUA_AUTO_ATTACK = 0x0400 + 459;

    private void OnArmySchedulerStart(ScriptSchedulerViewModel.ArmySchedulerMessage m)
    {
        m.Handled = true;
        try
        {
            var dialogService = Ioc.Default.GetRequiredService<IDialogService>();

            int notLoggedInCount = 0;
            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero)
                {
                    IntPtr res = SendChildMessage(info, (uint)WM_SKUA_CHECK_LOGIN, IntPtr.Zero, IntPtr.Zero);
                    if (res.ToInt32() == 0)
                        notLoggedInCount++;
                }
            }

            if (notLoggedInCount > 0)
            {
                dialogService.ShowMessageBox($"Cannot start Army Scheduler! {notLoggedInCount} account(s) are not logged in. Please log them in first.", "Army Scheduler Error");
                return;
            }

            string tempFile = System.IO.Path.Combine(ClientFileSources.TabsDIR, "skua_global_playlist.json");
            var data = m.Queue.Select(x => new ScriptSchedulerViewModel.SavedScriptItem { Path = x.Path, Id = x.Id, Name = x.Name }).ToList();
            System.IO.File.WriteAllText(tempFile, System.Text.Json.JsonSerializer.Serialize(data));
            
            foreach (var info in _tabs.Values)
                if (info.ChildHwnd != IntPtr.Zero)
                    PostChildMessage(info.ChildHwnd, WM_SKUA_ARMY_SCHEDULER, IntPtr.Zero, IntPtr.Zero);
                    
            dialogService.ShowMessageBox("Army Scheduler payload has been broadcasted. All active accounts will now seamlessly rebuild their queues and begin executing the playlist.", "Army Scheduler Started");
        }
        catch { }
    }

    private void OnArmySchedulerStop(ScriptSchedulerViewModel.ArmySchedulerStopMessage m)
    {
        m.Handled = true;
        try
        {
            foreach (var info in _tabs.Values)
                if (info.ChildHwnd != IntPtr.Zero)
                    PostChildMessage(info.ChildHwnd, WM_SKUA_ARMY_SCHEDULER_STOP, IntPtr.Zero, IntPtr.Zero);
                    
            var dialogService = Ioc.Default.GetRequiredService<IDialogService>();
            dialogService.ShowMessageBox("Stop signal has been successfully broadcasted. All accounts are now halting their schedulers.", "Army Scheduler Stopped");
        }
        catch { }
    }

    private void MenuItem_ArmyScheduler_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var windowService = Ioc.Default.GetRequiredService<IWindowService>();
            windowService.RegisterManagedWindow("Scheduler", Ioc.Default.GetRequiredService<ScriptSchedulerViewModel>());
            windowService.RegisterManagedWindow("Script Repo", Ioc.Default.GetRequiredService<ScriptRepoViewModel>());
            windowService.ShowManagedWindow("Scheduler");
        }
        catch (Exception ex)
        {
            Ioc.Default.GetRequiredService<IDialogService>().ShowMessageBox(ex.ToString(), "Error Opening Scheduler");
        }
    }
    
    private bool _ignoreSelectionChange = false;

    public void EnterGridView()
    {
        if (_tabs.Count <= 1 || _isTogglingGridView) return;

        _isTogglingGridView = true;
        try
        {
            _lastGridViewToggleTime = DateTime.UtcNow;
            _isGridViewEnabled = true;
            _gridFocusedChild = IntPtr.Zero;
            UpdateGridViewBorderColor();

            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero)
                {
                    info.IsThrottled = false;
                    PostChildMessage(info.ChildHwnd, WM_SKUA_GRIDVIEW, new IntPtr(1), IntPtr.Zero);
                    PostChildMessage(info.ChildHwnd, WM_SKUA_THROTTLE, IntPtr.Zero, IntPtr.Zero);
                }
            }

            if (InstancesTabControl.SelectedItem is TabItem currentTab)
                _lastSelectedTab = currentTab;
            _lastSelectedTab ??= InstancesTabControl.Items.OfType<TabItem>().FirstOrDefault();

            _ignoreSelectionChange = true;
            InstancesTabControl.SelectedItem = null;
            foreach (var item in InstancesTabControl.Items.OfType<TabItem>())
                item.IsSelected = false;
            _ignoreSelectionChange = false;

            DoReposition();
        }
        finally
        {
            _isTogglingGridView = false;
        }
    }

    public void ExitGridView()
    {
        if (_isTogglingGridView) return;

        _isTogglingGridView = true;
        try
        {
            _lastGridViewToggleTime = DateTime.UtcNow;
            _isGridViewEnabled = false;
            _gridFocusedChild = IntPtr.Zero;
            UpdateGridViewBorderColor();

            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero)
                {
                    PostChildMessage(info.ChildHwnd, WM_SKUA_GRIDVIEW, IntPtr.Zero, IntPtr.Zero);
                }
            }

            _ignoreSelectionChange = true;
            if (_lastSelectedTab != null && InstancesTabControl.Items.Contains(_lastSelectedTab))
            {
                InstancesTabControl.SelectedItem = _lastSelectedTab;
            }
            else
            {
                var firstTab = InstancesTabControl.Items.OfType<TabItem>().FirstOrDefault();
                if (firstTab != null)
                    InstancesTabControl.SelectedItem = firstTab;
            }
            _ignoreSelectionChange = false;

            DoReposition();
        }
        finally
        {
            _isTogglingGridView = false;
        }
    }

    // How Grid View arranges the accounts: "auto" (a near-square grid), "vertical" (one column: two accounts stack top and
    // bottom) or "horizontal" (one row: side by side). Kept in the settings.
    private string _gridLayout = "auto";

    private static string NormalGridLayout(string? value) => value is "vertical" or "horizontal" ? value : "auto";

    private static (int Cols, int Rows) GridShape(string layout, int n)
    {
        switch (layout)
        {
            case "vertical": return (1, Math.Max(n, 1));
            case "horizontal": return (Math.Max(n, 1), 1);
            default:
                int cols = (int)Math.Ceiling(Math.Sqrt(n));
                return (cols, (int)Math.Ceiling((double)n / cols));
        }
    }

    // The size (pixels) the game is played at while one account is shown: null fills the window. The Size button steps through
    // these; the game is centred and shrunk (same shape) when the window is smaller. Kept in the settings.
    private static readonly (int W, int H)[] GameSizes = { (640, 367), (960, 550), (1280, 720), (1600, 900), (1920, 1080) };
    private (int W, int H)? _gameSize;

    private static (int W, int H)? ParseGameSize(string? value)
    {
        string[] parts = (value ?? "").Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h)
            && w is >= 320 and <= 7680 && h is >= 180 and <= 4320)
            return (w, h);
        return null;
    }

    private static (int X, int Y, int W, int H) FitGameSize(int x, int y, int w, int h, (int W, int H)? size)
    {
        if (size is not { } s || w <= 0 || h <= 0)
            return (x, y, w, h);
        double shrink = Math.Min(1.0, Math.Min((double)w / s.W, (double)h / s.H));
        int gw = Math.Max((int)(s.W * shrink), 1), gh = Math.Max((int)(s.H * shrink), 1);
        return (x + (w - gw) / 2, y + (h - gh) / 2, gw, gh);
    }

    private void UpdateGameSizeButton()
    {
        GameSizeText.Text = _gameSize is { } s ? $"Size: {s.W}x{s.H}" : "Size: Auto";
        ToolTip.SetTip(GameSizeBorder, _gameSize is { } t
            ? $"Game size: {t.W} x {t.H}. Click for the next size."
            : "Game size: fills the window. Click to play at a fixed size (centred, never larger than the window).");
    }

    private void GameSizeBorder_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        int next = _gameSize is { } s ? Array.IndexOf(GameSizes, s) + 1 : 0;
        _gameSize = next < GameSizes.Length ? GameSizes[next] : null;
        Ioc.Default.GetService<ISettingsService>()?.Set("BonxieMuxGameSize", _gameSize is { } g ? $"{g.W}x{g.H}" : "auto");
        UpdateGameSizeButton();
        _lastX = int.MinValue; // forces the game to be placed again
        DoReposition();
    }

    private void UpdateGridLayoutButton()
    {
        (string text, MaterialIconKind icon, string tip) = _gridLayout switch
        {
            "vertical" => ("Stacked", MaterialIconKind.ViewAgendaOutline, "Grid View layout: stacked top to bottom. Click for side by side."),
            "horizontal" => ("Side by side", MaterialIconKind.ViewColumnOutline, "Grid View layout: side by side. Click for automatic."),
            _ => ("Auto", MaterialIconKind.ViewDashboardOutline, "Grid View layout: automatic grid. Click to stack the accounts top to bottom."),
        };
        GridLayoutText.Text = text;
        GridLayoutIcon.Kind = icon;
        ToolTip.SetTip(GridLayoutBorder, tip);
    }

    private void GridLayoutBorder_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _gridLayout = _gridLayout switch { "auto" => "vertical", "vertical" => "horizontal", _ => "auto" };
        Ioc.Default.GetService<ISettingsService>()?.Set("BonxieMuxGridLayout", _gridLayout);
        UpdateGridLayoutButton();
        if (_isGridViewEnabled)
            DoReposition();
    }

    private void GridViewBorder_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;

        if (DateTime.UtcNow - _lastGridViewToggleTime < _gridViewToggleCooldown)
            return;

        if (_isGridViewEnabled)
        {
            ExitGridView();
        }
        else
        {
            if (_tabs.Count <= 1)
            {
                // If only 1 account, Grid View has nothing to tile; guarantee normal tab view
                ExitGridView();
                return;
            }
            EnterGridView();
        }
    }

    private void GridViewBorder_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (!_isGridViewEnabled)
            GridViewBorder[!Border.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaNavHoverBrush");
    }

    private void GridViewBorder_PointerExited(object? sender, PointerEventArgs e)
    {
        if (!_isGridViewEnabled)
            GridViewBorder[!Border.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaSurfaceAltBrush");
    }

    private void AddTabButton_Click(object? sender, RoutedEventArgs e)
    {
        AddNewInstance();
    }

    private void ScrollTabsLeftButton_Click(object? sender, RoutedEventArgs e)
    {
        if (TabScrollViewer == null) return;
        var newOffset = Math.Max(0, TabScrollViewer.Offset.X - 160);
        TabScrollViewer.Offset = new Vector(newOffset, 0);
        UpdateTabScrollButtons();
    }

    private void ScrollTabsRightButton_Click(object? sender, RoutedEventArgs e)
    {
        if (TabScrollViewer == null) return;
        double maxScroll = Math.Max(0, TabScrollViewer.Extent.Width - TabScrollViewer.Viewport.Width);
        var newOffset = Math.Min(maxScroll, TabScrollViewer.Offset.X + 160);
        TabScrollViewer.Offset = new Vector(newOffset, 0);
        UpdateTabScrollButtons();
    }

    private void UpdateTabScrollButtons()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (TabScrollViewer == null || ScrollTabsLeftButton == null || ScrollTabsRightButton == null)
                return;

            double maxScroll = Math.Max(0, TabScrollViewer.Extent.Width - TabScrollViewer.Viewport.Width);
            if (maxScroll > 2)
            {
                ScrollTabsLeftButton.IsVisible = TabScrollViewer.Offset.X > 1;
                ScrollTabsRightButton.IsVisible = TabScrollViewer.Offset.X < maxScroll - 1;
            }
            else
            {
                ScrollTabsLeftButton.IsVisible = false;
                ScrollTabsRightButton.IsVisible = false;
            }
        }, DispatcherPriority.Normal);
    }

    private void ScrollTabIntoView(TabItem? tab)
    {
        if (tab == null || TabScrollViewer == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var tabBounds = tab.Bounds;
                var currentOffset = TabScrollViewer.Offset.X;
                var viewportWidth = TabScrollViewer.Viewport.Width;

                if (viewportWidth <= 0 || tabBounds.Width <= 0) return;

                if (tabBounds.Left < currentOffset)
                {
                    TabScrollViewer.Offset = new Vector(Math.Max(0, tabBounds.Left), 0);
                }
                else if (tabBounds.Right > currentOffset + viewportWidth)
                {
                    var target = tabBounds.Right - viewportWidth;
                    TabScrollViewer.Offset = new Vector(Math.Max(0, target), 0);
                }
            }
            catch { }
        }, DispatcherPriority.Loaded);
    }

    private void InstancesTabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source != InstancesTabControl || _ignoreSelectionChange) return;

        CloseHostDropdowns();

        if (_isGridViewEnabled && InstancesTabControl.SelectedItem != null)
        {
            // ExitGridView restores _lastSelectedTab, so point it at the tab that was just clicked.
            if (InstancesTabControl.SelectedItem is TabItem clickedTab)
                _lastSelectedTab = clickedTab;
            ExitGridView();
            return;
        }

        DoReposition();
        UpdateTabScrollButtons();
        if (InstancesTabControl.SelectedItem is TabItem selTab)
        {
            ScrollTabIntoView(selTab);
        }
    }

    private void CloseHostDropdowns()
    {
        global::Avalonia.Controls.Primitives.FlyoutBase.GetAttachedFlyout(GlobeBorder)?.Hide();
    }

    private void GlobeBorder_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        global::Avalonia.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(GlobeBorder);
    }

    private void GlobeBorder_PointerEntered(object? sender, PointerEventArgs e)
    {
        GlobeBorder[!Border.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaNavHoverBrush");
    }

    private void GlobeBorder_PointerExited(object? sender, PointerEventArgs e)
    {
        GlobeBorder[!Border.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaSurfaceAltBrush");
    }

    private void Options_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var options = sender as IScriptOption;
        if (options == null) return;
        
        int optionId = -1;
        bool val = false;
        
        switch (e.PropertyName)
        {
            case "LagKiller": optionId = 1; val = options.LagKiller; break;
            case "HeadlessMode": optionId = 2; val = options.HeadlessMode; break;
            case "HidePlayers": optionId = 3; val = options.HidePlayers; break;
            case "DisableFX": optionId = 4; val = options.DisableFX; break;
            case "InfiniteRange": optionId = 5; val = options.InfiniteRange; break;
            case "StreamerMode": optionId = 9; val = options.StreamerMode; break;
            case "AggroMonsters": optionId = 10; val = options.AggroMonsters; break;
        }
        
        if (optionId != -1)
        {
            foreach (var info in _tabs.Values)
                if (info.ChildHwnd != IntPtr.Zero)
                    PostChildMessage(info.ChildHwnd, WM_SKUA_SET_OPTION, new IntPtr(optionId), new IntPtr(val ? 1 : 0));
        }
    }

    private async void MenuItem_StartAllScripts_Click(object? sender, RoutedEventArgs e)
    {
        if (MenuStartScripts != null) MenuStartScripts.IsEnabled = false;
        if (MenuStopScripts != null) MenuStopScripts.IsEnabled = false;
        
        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero)
                PostChildMessage(info.ChildHwnd, WM_SKUA_SET_OPTION, new IntPtr(99), new IntPtr(1));
        
        await Task.Delay(2000);
        
        if (MenuStartScripts != null) MenuStartScripts.IsEnabled = true;
        if (MenuStopScripts != null) MenuStopScripts.IsEnabled = true;
    }

    private async void MenuItem_StopAllScripts_Click(object? sender, RoutedEventArgs e)
    {
        if (MenuStartScripts != null) MenuStartScripts.IsEnabled = false;
        if (MenuStopScripts != null) MenuStopScripts.IsEnabled = false;
        
        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero)
                PostChildMessage(info.ChildHwnd, WM_SKUA_SET_OPTION, new IntPtr(99), new IntPtr(0));
        
        await Task.Delay(4000);
        
        if (MenuStartScripts != null) MenuStartScripts.IsEnabled = true;
        if (MenuStopScripts != null) MenuStopScripts.IsEnabled = true;
    }

    private async void MenuItem_StartAutoAttackAll_Click(object? sender, RoutedEventArgs e)
    {
        if (MenuStartAutoAttack != null) MenuStartAutoAttack.IsEnabled = false;
        if (MenuStopAutoAttack != null) MenuStopAutoAttack.IsEnabled = false;

        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero)
                PostChildMessage(info.ChildHwnd, WM_SKUA_AUTO_ATTACK, new IntPtr(1), IntPtr.Zero);

        await Task.Delay(2000);

        if (MenuStartAutoAttack != null) MenuStartAutoAttack.IsEnabled = true;
        if (MenuStopAutoAttack != null) MenuStopAutoAttack.IsEnabled = true;
    }

    private async void MenuItem_StopAutoAttackAll_Click(object? sender, RoutedEventArgs e)
    {
        if (MenuStartAutoAttack != null) MenuStartAutoAttack.IsEnabled = false;
        if (MenuStopAutoAttack != null) MenuStopAutoAttack.IsEnabled = false;

        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero)
                PostChildMessage(info.ChildHwnd, WM_SKUA_AUTO_ATTACK, new IntPtr(0), IntPtr.Zero);

        await Task.Delay(2000);

        if (MenuStartAutoAttack != null) MenuStartAutoAttack.IsEnabled = true;
        if (MenuStopAutoAttack != null) MenuStopAutoAttack.IsEnabled = true;
    }

    private void BroadcastScriptAction(string scriptPath, int msg)
    {
        try
        {
            string tempFile = System.IO.Path.Combine(ClientFileSources.TabsDIR, "skua_global_script.txt");
            System.IO.File.WriteAllText(tempFile, scriptPath);

            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero)
                {
                    PostChildMessage(info.ChildHwnd, (uint)msg, IntPtr.Zero, IntPtr.Zero);
                }
            }
        }
        catch { }
    }

    private void MenuItem_LoadScriptAll_Click(object? sender, RoutedEventArgs e)
    {
        var windowService = Ioc.Default.GetRequiredService<IWindowService>();
        windowService.RegisterManagedWindow("Script Repo", Ioc.Default.GetRequiredService<ScriptRepoViewModel>());
        windowService.ShowManagedWindow("Script Repo");
    }

    private async void MenuItem_LoginAll_Click(object? sender, RoutedEventArgs e)
    {
        var menuItem = sender as MenuItem;
        string originalHeader = menuItem?.Header?.ToString() ?? "Login All Clients";
        if (menuItem != null) menuItem.IsEnabled = false;

        int count = 1;
        int total = _tabs.Values.Count(i => i.ChildHwnd != IntPtr.Zero);

        foreach (var info in _tabs.Values)
        {
            if (info.ChildHwnd != IntPtr.Zero)
            {
                if (menuItem != null) menuItem.Header = $"Logging in... ({count}/{total})";
                PostChildMessage(info.ChildHwnd, WM_SKUA_LOGIN, IntPtr.Zero, IntPtr.Zero);
                await Task.Delay(2000);
                count++;
            }
        }

        if (menuItem != null)
        {
            menuItem.Header = originalHeader;
            menuItem.IsEnabled = true;
        }
    }

    private void MenuItem_LogoutAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero)
                PostChildMessage(info.ChildHwnd, WM_SKUA_LOGOUT, IntPtr.Zero, IntPtr.Zero);
    }

    private string _lastJumpMap = "";
    private string _lastJumpCell = "";
    private string _lastJumpPlayer = "";
    private static string ArmyJumpSettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Skua", "army-control-jump.json");

    private sealed class ArmyJumpSettings
    {
        public string Map { get; set; } = "";
        public string Room { get; set; } = "";
        public string Cell { get; set; } = "Enter";
        public string Player { get; set; } = "";
        public List<ArmyJumpDestination> History { get; set; } = new();
        public List<ArmyJumpDestination> Presets { get; set; } = new();
    }

    private sealed class ArmyJumpDestination
    {
        public string Name { get; set; } = "";
        public string Map { get; set; } = "";
        public string Room { get; set; } = "";
        public string Cell { get; set; } = "Enter";
        public string Pad { get; set; } = "Spawn";

        public string DisplayName => $"{Map}{(string.IsNullOrWhiteSpace(Room) ? "" : $"-{Room}")} - {Cell}, {Pad}";
    }

    private sealed class ArmyClientLocation
    {
        public ArmyClientLocation() { }

        public string Map { get; set; } = "";
        public string Room { get; set; } = "";
        public string Cell { get; set; } = "Enter";
        public string Pad { get; set; } = "Spawn";
        public string Username { get; set; } = "";
        public DateTime UpdatedUtc { get; set; }
    }

    private readonly Dictionary<int, ArmyClientLocation> _latestClientLocations = new();
    private readonly Dictionary<int, string> _lastRecordedLocationByPid = new();

    private List<ArmyJumpDestination> _jumpMapHistory = new();
    private List<ArmyJumpDestination> _jumpMapPresets = new();
    private string _lastJumpRoom = "";

    private void LoadArmyJumpSettings()
    {
        try
        {
            string path = ArmyJumpSettingsPath;
            if (!File.Exists(path)) return;
            ArmyJumpSettings? settings = JsonSerializer.Deserialize<ArmyJumpSettings>(File.ReadAllText(path));
            if (settings == null) return;
            _lastJumpMap = settings.Map ?? "";
            _lastJumpCell = settings.Cell ?? "Enter";
            _lastJumpRoom = settings.Room ?? "";
            _lastJumpPlayer = settings.Player ?? "";
            _jumpMapHistory = settings.History ?? new List<ArmyJumpDestination>();
            _jumpMapPresets = settings.Presets ?? new List<ArmyJumpDestination>();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to load Army Control jump settings: {ex.Message}");
        }
    }

    private void SaveArmyJumpSettings()
    {
        try
        {
            string path = ArmyJumpSettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new ArmyJumpSettings
            {
                Map = _lastJumpMap,
                Cell = _lastJumpCell,
                Room = _lastJumpRoom,
                Player = _lastJumpPlayer,
                History = _jumpMapHistory,
                Presets = _jumpMapPresets
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to save Army Control jump settings: {ex.Message}");
        }
    }

    private async void MenuItem_MapTravel_Click(object? sender, RoutedEventArgs e)
    {
        Window dialog = new Window
        {
            Title = "Map Travel",
            Width = 640,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = this.Background
        };

        StackPanel panel = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "Choose a recent destination or a saved preset to jump all clients.",
            TextWrapping = TextWrapping.Wrap
        });

        TextBlock historyLabel = new TextBlock { Text = "Recent destinations" };
        ComboBox historyPicker = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 360
        };
        StackPanel historyButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        Button jumpHistoryButton = new Button { Content = "Select / Jump", MinWidth = 110 };
        Button clearHistoryButton = new Button { Content = "Clear History", MinWidth = 100 };

        void RefreshHistory()
        {
            List<ArmyJumpDestination> history = _jumpMapHistory.ToList();
            historyPicker.ItemsSource = history.Select(item => item.DisplayName).ToList();
            historyPicker.SelectedIndex = history.Count > 0 ? 0 : -1;
            jumpHistoryButton.IsEnabled = history.Count > 0;
            clearHistoryButton.IsEnabled = history.Count > 0;
        }

        jumpHistoryButton.Click += (_, _) =>
        {
            List<ArmyJumpDestination> history = _jumpMapHistory.ToList();
            int index = historyPicker.SelectedIndex;
            if (index >= 0 && index < history.Count)
                JumpArmyToDestination(history[index]);
        };
        clearHistoryButton.Click += (_, _) =>
        {
            _jumpMapHistory.Clear();
            SaveArmyJumpSettings();
            RefreshHistory();
        };
        historyButtons.Children.Add(jumpHistoryButton);
        historyButtons.Children.Add(clearHistoryButton);
        panel.Children.Add(historyLabel);
        panel.Children.Add(historyPicker);
        panel.Children.Add(historyButtons);

        panel.Children.Add(new Separator());

        TextBlock presetLabel = new TextBlock { Text = "Saved presets" };
        ComboBox presetPicker = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 360
        };
        StackPanel presetButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        Button jumpPresetButton = new Button { Content = "Select / Jump", MinWidth = 110 };
        Button createPresetButton = new Button { Content = "Create", MinWidth = 80 };
        Button editPresetButton = new Button { Content = "Edit", MinWidth = 70 };
        Button deletePresetButton = new Button { Content = "Delete", MinWidth = 80 };

        void RefreshPresets(int selectedIndex = 0)
        {
            presetPicker.ItemsSource = _jumpMapPresets.Select(item => $"{item.Name} — {item.DisplayName}").ToList();
            presetPicker.SelectedIndex = _jumpMapPresets.Count == 0 ? -1 : Math.Clamp(selectedIndex, 0, _jumpMapPresets.Count - 1);
            bool hasPresets = _jumpMapPresets.Count > 0;
            jumpPresetButton.IsEnabled = hasPresets;
            editPresetButton.IsEnabled = hasPresets;
            deletePresetButton.IsEnabled = hasPresets;
        }

        jumpPresetButton.Click += (_, _) =>
        {
            int index = presetPicker.SelectedIndex;
            if (index >= 0 && index < _jumpMapPresets.Count)
                JumpArmyToDestination(_jumpMapPresets[index]);
        };
        createPresetButton.Click += async (_, _) =>
        {
            dialog.Hide();
            await OpenJumpMapPresetEditor(null);
            RefreshPresets(_jumpMapPresets.Count - 1);
            dialog.Show();
        };
        editPresetButton.Click += async (_, _) =>
        {
            int index = presetPicker.SelectedIndex;
            if (index < 0 || index >= _jumpMapPresets.Count)
                return;

            ArmyJumpDestination selectedPreset = _jumpMapPresets[index];
            dialog.Hide();
            await OpenJumpMapPresetEditor(selectedPreset);
            RefreshPresets(index);
            dialog.Show();
        };
        deletePresetButton.Click += (_, _) =>
        {
            int index = presetPicker.SelectedIndex;
            if (index < 0 || index >= _jumpMapPresets.Count)
                return;

            _jumpMapPresets.RemoveAt(index);
            SaveArmyJumpSettings();
            RefreshPresets(index);
        };

        presetButtons.Children.Add(jumpPresetButton);
        presetButtons.Children.Add(createPresetButton);
        presetButtons.Children.Add(editPresetButton);
        presetButtons.Children.Add(deletePresetButton);
        panel.Children.Add(presetLabel);
        panel.Children.Add(presetPicker);
        panel.Children.Add(presetButtons);

        StackPanel bottomButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        Button manualJumpButton = new Button { Content = "Jump to Map / Cell...", MinWidth = 140 };
        Button closeButton = new Button { Content = "Close", MinWidth = 84, IsCancel = true };
        manualJumpButton.Click += (_, _) =>
        {
            dialog.Hide();
            MenuItem_JumpMap_Click(null, new RoutedEventArgs());
            dialog.Show();
        };
        closeButton.Click += (_, _) => dialog.Close();
        bottomButtons.Children.Add(manualJumpButton);
        bottomButtons.Children.Add(closeButton);
        panel.Children.Add(bottomButtons);

        RefreshHistory();
        RefreshPresets();
        dialog.Content = panel;
        await dialog.ShowDialog(this);
    }

    private void MenuItem_JumpMap_Click(object? sender, RoutedEventArgs e)
    {
        InputDialogViewModel vm = new("Jump Army to Map / Cell", "Enter destination as Map,Room,Cell (room is optional):", "e.g., yulgar,12345,Enter", false);
        vm.DialogTextInput = $"{_lastJumpMap},{_lastJumpRoom},{_lastJumpCell}";

        if (Ioc.Default.GetRequiredService<IDialogService>().ShowDialog(vm) != true)
            return;

        string[] parts = (vm.DialogTextInput ?? "").Split(',');
        string targetMap = parts.Length > 0 ? parts[0].Trim() : "";
        string targetRoom = parts.Length > 2 ? parts[1].Trim() : "";
        string targetCell = parts.Length > 2 ? parts[2].Trim() : (parts.Length > 1 ? parts[1].Trim() : "Enter");

        if (!string.IsNullOrWhiteSpace(targetMap))
            JumpArmyToDestination(new ArmyJumpDestination { Map = targetMap, Room = targetRoom, Cell = string.IsNullOrWhiteSpace(targetCell) ? "Enter" : targetCell });
    }

    private void JumpArmyToDestination(ArmyJumpDestination destination)
    {
        _lastJumpMap = destination.Map.Trim();
        _lastJumpRoom = destination.Room.Trim();
        _lastJumpCell = string.IsNullOrWhiteSpace(destination.Cell) ? "Enter" : destination.Cell.Trim();

        SaveArmyJumpSettings();
        string mapToJoin = string.IsNullOrWhiteSpace(_lastJumpRoom) ? _lastJumpMap : $"{_lastJumpMap}-{_lastJumpRoom}";
        string tempFile = Path.Combine(ClientFileSources.TabsDIR, "skua_global_jump.txt");
        File.WriteAllLines(tempFile, new[] { mapToJoin, _lastJumpCell, string.IsNullOrWhiteSpace(destination.Pad) ? "Spawn" : destination.Pad.Trim() });

        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero)
                PostChildMessage(info.ChildHwnd, WM_SKUA_JUMP_MAP, IntPtr.Zero, IntPtr.Zero);
    }

    private async void MenuItem_JumpMapHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (_jumpMapHistory.Count == 0)
        {
            Ioc.Default.GetRequiredService<IDialogService>().ShowMessageBox("No recently visited maps yet. Maps will appear here as your open clients travel.", "Jump Map History");
            return;
        }

        Window dialog = new Window
        {
            Title = "Jump to Recent Destination",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = this.Background
        };

        StackPanel panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Select a recent map, room and cell:", TextWrapping = TextWrapping.Wrap });

        List<ArmyJumpDestination> history = _jumpMapHistory.ToList();
        ComboBox historyPicker = new ComboBox
        {
            ItemsSource = history.Select(item => item.DisplayName).ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(historyPicker);

        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button clearButton = new Button { Content = "Clear History", MinWidth = 100 };
        Button cancelButton = new Button { Content = "Cancel", MinWidth = 84 };
        Button jumpButton = new Button { Content = "Jump", MinWidth = 84, IsDefault = true };
        clearButton.Click += (_, _) =>
        {
            _jumpMapHistory.Clear();
            SaveArmyJumpSettings();
            dialog.Close(false);
        };
        cancelButton.Click += (_, _) => dialog.Close(false);
        jumpButton.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(clearButton);
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(jumpButton);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        bool? confirmed = await dialog.ShowDialog<bool?>(this);
        if (confirmed != true || historyPicker.SelectedIndex < 0 || historyPicker.SelectedIndex >= history.Count)
            return;

        JumpArmyToDestination(history[historyPicker.SelectedIndex]);
    }

    private async void MenuItem_SaveJumpMapPreset_Click(object? sender, RoutedEventArgs e)
    {
        await OpenJumpMapPresetEditor(null);
    }

    private async Task OpenJumpMapPresetEditor(ArmyJumpDestination? existingPreset)
    {
        Window dialog = new Window
        {
            Title = existingPreset == null ? "Create Map Preset" : "Edit Map Preset",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = this.Background
        };

        StackPanel panel = new StackPanel { Spacing = 10, Margin = new Thickness(18) };
        TextBox nameInput = new TextBox { Watermark = "Preset name", Text = existingPreset?.Name ?? "" };
        TextBox mapInput = new TextBox { Watermark = "Map name", Text = existingPreset?.Map ?? "" };
        TextBox roomInput = new TextBox { Watermark = "Room number (optional)", Text = existingPreset?.Room ?? "" };
        TextBox cellInput = new TextBox { Watermark = "Cell", Text = existingPreset?.Cell ?? "Enter" };
        TextBox padInput = new TextBox { Watermark = "Pad", Text = existingPreset?.Pad ?? "Spawn" };

        // New presets start with the selected client's current location when available.
        if (existingPreset == null
            && InstancesTabControl.SelectedItem is TabItem currentTab
            && _tabs.TryGetValue(currentTab, out TabInfo? currentInfo)
            && currentInfo.Process != null
            && _latestClientLocations.TryGetValue(currentInfo.Process.Id, out ArmyClientLocation? currentLocation)
            && currentLocation != null
            && currentLocation.UpdatedUtc >= DateTime.UtcNow.AddSeconds(-5)
            && !string.IsNullOrWhiteSpace(currentLocation.Map))
        {
            mapInput.Text = currentLocation.Map;
            roomInput.Text = currentLocation.Room;
            cellInput.Text = currentLocation.Cell;
            padInput.Text = currentLocation.Pad;
            nameInput.Text = currentLocation.Map + (string.IsNullOrWhiteSpace(currentLocation.Room) ? "" : "-" + currentLocation.Room);
        }

        panel.Children.Add(new TextBlock { Text = "Preset name" });
        panel.Children.Add(nameInput);
        panel.Children.Add(new TextBlock { Text = "Map" });
        panel.Children.Add(mapInput);
        panel.Children.Add(new TextBlock { Text = "Room number (optional)" });
        panel.Children.Add(roomInput);
        panel.Children.Add(new TextBlock { Text = "Cell" });
        panel.Children.Add(cellInput);
        panel.Children.Add(new TextBlock { Text = "Pad" });
        panel.Children.Add(padInput);

        Button saveCurrentButton = new Button { Content = "Use Current Client Location", HorizontalAlignment = HorizontalAlignment.Left };
        saveCurrentButton.Click += (_, _) =>
        {
            if (InstancesTabControl.SelectedItem is not TabItem selectedTab
                || !_tabs.TryGetValue(selectedTab, out TabInfo? selectedInfo)
                || selectedInfo.Process == null
                || !_latestClientLocations.TryGetValue(selectedInfo.Process.Id, out ArmyClientLocation? location)
                || location == null
                || location.UpdatedUtc < DateTime.UtcNow.AddSeconds(-5)
                || string.IsNullOrWhiteSpace(location.Map))
            {
                Ioc.Default.GetRequiredService<IDialogService>().ShowMessageBox("The selected client has not reported a current location yet. Log in and wait a moment, then try again.", "Current Location");
                return;
            }

            mapInput.Text = location.Map;
            roomInput.Text = location.Room;
            cellInput.Text = location.Cell;
            padInput.Text = location.Pad;
            if (string.IsNullOrWhiteSpace(nameInput.Text))
                nameInput.Text = location.Map + (string.IsNullOrWhiteSpace(location.Room) ? "" : "-" + location.Room);
        };
        panel.Children.Add(saveCurrentButton);

        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button cancelButton = new Button { Content = "Cancel", MinWidth = 84 };
        Button saveButton = new Button { Content = "Save Preset", MinWidth = 100, IsDefault = true };
        cancelButton.Click += (_, _) => dialog.Close(false);
        saveButton.Click += (_, _) =>
        {
            string name = nameInput.Text?.Trim() ?? "";
            string map = mapInput.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(map))
            {
                Ioc.Default.GetRequiredService<IDialogService>().ShowMessageBox("Enter both a preset name and map name.", "Create Map Preset");
                return;
            }

            ArmyJumpDestination preset = existingPreset ?? new ArmyJumpDestination();
            preset.Name = name;
            preset.Map = map;
            preset.Room = roomInput.Text?.Trim() ?? "";
            preset.Cell = string.IsNullOrWhiteSpace(cellInput.Text) ? "Enter" : cellInput.Text.Trim();
            preset.Pad = string.IsNullOrWhiteSpace(padInput.Text) ? "Spawn" : padInput.Text.Trim();

            _jumpMapPresets.RemoveAll(item => !ReferenceEquals(item, preset) && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existingPreset == null)
                _jumpMapPresets.Add(preset);
            SaveArmyJumpSettings();
            dialog.Close(true);
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(saveButton);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        await dialog.ShowDialog<bool?>(this);
    }

    private async void MenuItem_JumpMapPreset_Click(object? sender, RoutedEventArgs e)
    {
        if (_jumpMapPresets.Count == 0)
        {
            Ioc.Default.GetRequiredService<IDialogService>().ShowMessageBox("No map presets saved yet. Use Save Current Map as Preset to add one.", "Map Presets");
            return;
        }

        Window dialog = new Window
        {
            Title = "Jump to Map Preset",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = this.Background
        };

        StackPanel panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Select a saved map, room and cell preset:", TextWrapping = TextWrapping.Wrap });

        List<ArmyJumpDestination> presets = _jumpMapPresets.ToList();
        ComboBox presetPicker = new ComboBox
        {
            ItemsSource = presets.Select(item => $"{item.Name} — {item.DisplayName}").ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(presetPicker);

        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button cancelButton = new Button { Content = "Cancel", MinWidth = 84 };
        Button jumpButton = new Button { Content = "Jump", MinWidth = 84, IsDefault = true };
        cancelButton.Click += (_, _) => dialog.Close(false);
        jumpButton.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(jumpButton);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        bool? confirmed = await dialog.ShowDialog<bool?>(this);
        if (confirmed != true || presetPicker.SelectedIndex < 0 || presetPicker.SelectedIndex >= presets.Count)
            return;

        JumpArmyToDestination(presets[presetPicker.SelectedIndex]);
    }

    private async void MenuItem_ManageJumpMapPresets_Click(object? sender, RoutedEventArgs e)
    {
        Window dialog = new Window
        {
            Title = "Manage Map Presets",
            Width = 600,
            MinHeight = 280,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = this.Background
        };

        StackPanel panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Create, inspect, edit, or delete saved map presets.", TextWrapping = TextWrapping.Wrap });

        ListBox presetList = new ListBox
        {
            ItemsSource = _jumpMapPresets.Select(item => $"{item.Name} — {item.DisplayName}").ToList(),
            SelectedIndex = _jumpMapPresets.Count > 0 ? 0 : -1,
            MinHeight = 100,
            MaxHeight = 320
        };
        panel.Children.Add(presetList);

        TextBlock destinationText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void UpdateDestination()
        {
            int index = presetList.SelectedIndex;
            destinationText.Text = index >= 0 && index < _jumpMapPresets.Count
                ? $"Destination: {_jumpMapPresets[index].DisplayName}"
                : "No preset selected.";
        }

        presetList.SelectionChanged += (_, _) => UpdateDestination();
        panel.Children.Add(destinationText);
        UpdateDestination();

        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button createButton = new Button { Content = "Create Preset...", MinWidth = 110 };
        Button editButton = new Button { Content = "Edit Selected...", MinWidth = 110, IsEnabled = _jumpMapPresets.Count > 0 };
        Button deleteButton = new Button { Content = "Delete Selected", MinWidth = 120, IsEnabled = _jumpMapPresets.Count > 0 };
        Button closeButton = new Button { Content = "Close", MinWidth = 84, IsCancel = true };

        void RefreshPresets(int selectedIndex)
        {
            presetList.ItemsSource = _jumpMapPresets.Select(item => $"{item.Name} — {item.DisplayName}").ToList();
            presetList.SelectedIndex = _jumpMapPresets.Count == 0 ? -1 : Math.Clamp(selectedIndex, 0, _jumpMapPresets.Count - 1);
            editButton.IsEnabled = _jumpMapPresets.Count > 0;
            deleteButton.IsEnabled = _jumpMapPresets.Count > 0;
            UpdateDestination();
        }

        createButton.Click += async (_, _) =>
        {
            dialog.Hide();
            await OpenJumpMapPresetEditor(null);
            dialog.Show();
            RefreshPresets(_jumpMapPresets.Count - 1);
        };
        editButton.Click += async (_, _) =>
        {
            int index = presetList.SelectedIndex;
            if (index < 0 || index >= _jumpMapPresets.Count)
                return;

            ArmyJumpDestination selectedPreset = _jumpMapPresets[index];
            dialog.Hide();
            await OpenJumpMapPresetEditor(selectedPreset);
            dialog.Show();
            RefreshPresets(index);
        };
        deleteButton.Click += (_, _) =>
        {
            int index = presetList.SelectedIndex;
            if (index < 0 || index >= _jumpMapPresets.Count)
                return;

            _jumpMapPresets.RemoveAt(index);
            SaveArmyJumpSettings();
            RefreshPresets(index);
        };
        closeButton.Click += (_, _) => dialog.Close();
        buttons.Children.Add(createButton);
        buttons.Children.Add(editButton);
        buttons.Children.Add(deleteButton);
        buttons.Children.Add(closeButton);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        await dialog.ShowDialog(this);
    }

    private async void MenuItem_JumpPlayer_Click(object? sender, RoutedEventArgs e)
    {
        SyncTabTitles();
        List<string> players = _tabs
            .Select(entry => GetTabLoggedInUsername(entry.Key, entry.Value, GetTabTitle(entry.Key)))
            .Where(username => !string.IsNullOrWhiteSpace(username))
            .Select(username => username!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(username => username, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (players.Count == 0)
        {
            Ioc.Default.GetService<IDialogService>()?.ShowMessageBox("No logged-in player names were found in the open client tabs. Log in to at least one tab and try again.", "Jump All to Player");
            return;
        }

        Window dialog = new Window
        {
            Title = "Jump Army to Player",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = this.Background
        };
        StackPanel panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Select a player from the open client tabs:", TextWrapping = TextWrapping.Wrap });
        string initialPlayer = players.FirstOrDefault(player => player.Equals(_lastJumpPlayer, StringComparison.OrdinalIgnoreCase)) ?? players[0];
        ComboBox playerPicker = new ComboBox
        {
            ItemsSource = players,
            SelectedItem = initialPlayer,
            Text = initialPlayer,
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(playerPicker);
        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button cancelButton = new Button { Content = "Cancel", MinWidth = 84 };
        Button jumpButton = new Button { Content = "Jump", MinWidth = 84, IsDefault = true };
        cancelButton.Click += (_, _) => dialog.Close(false);
        jumpButton.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(jumpButton);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        bool? confirmed = await dialog.ShowDialog<bool?>(this);
        string targetPlayer = playerPicker.Text?.Trim() ?? "";
        if (confirmed != true || string.IsNullOrWhiteSpace(targetPlayer))
            return;

        _lastJumpPlayer = targetPlayer;
        SaveArmyJumpSettings();
        string tempFile = Path.Combine(ClientFileSources.TabsDIR, "skua_global_jump_player.txt");
        File.WriteAllText(tempFile, targetPlayer);

        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero)
                PostChildMessage(info.ChildHwnd, WM_SKUA_JUMP_PLAYER, IntPtr.Zero, IntPtr.Zero);
    }

    private string GetTabTitle(TabItem tab)
    {
        if (tab.Header is StackPanel headerPanel)
            return headerPanel.Children.OfType<TextBlock>().FirstOrDefault(block => block.Text != "✕")?.Text ?? "";
        return tab.Header?.ToString() ?? "";
    }

    // Each client joins its own house (no input needed).
    private void MenuItem_JumpHouse_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero)
                PostChildMessage(info.ChildHwnd, WM_SKUA_JUMP_HOUSE, IntPtr.Zero, IntPtr.Zero);
    }

    private const int WM_SKUA_JUMP_HOUSE = 0x0400 + 465;
    private const int WM_SKUA_CHANGE_SERVER = 0x0400 + 467;
    private const int WM_SKUA_ACCEPT_QUEST = 0x0400 + 458;
    private string _lastAcceptQuestId = "";

    private async void MenuItem_ChangeServerAll_Click(object? sender, RoutedEventArgs e)
    {
        List<string> serverNames;
        try
        {
            IScriptServers? scriptServers = Ioc.Default.GetService<IScriptServers>();
            if (scriptServers is null)
            {
                Ioc.Default.GetService<IDialogService>()?.ShowMessageBox("The server service is unavailable. Please restart BonxieMux and try again.", "Change Server for All");
                return;
            }

            List<Skua.Core.Models.Servers.Server> servers = await scriptServers.GetServers(true);
            serverNames = servers
                .Where(server => server.Online && !string.IsNullOrWhiteSpace(server.Name) && !string.IsNullOrWhiteSpace(server.IP))
                .Select(server => server.Name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to load Army Control servers: {ex.GetType().Name}: {ex.Message}");
            Ioc.Default.GetService<IDialogService>()?.ShowMessageBox("Could not load the current server list. Check your connection and try again.", "Change Server for All");
            return;
        }

        if (serverNames.Count == 0)
        {
            Ioc.Default.GetService<IDialogService>()?.ShowMessageBox("The server API did not return any online servers.", "Change Server for All");
            return;
        }

        Window dialog = new Window
        {
            Title = "Change Server for All",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = this.Background
        };
        StackPanel panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Select the server for all open clients:", TextWrapping = TextWrapping.Wrap });

        string initialServer = serverNames.FirstOrDefault(name => name.Equals("Artix", StringComparison.OrdinalIgnoreCase)) ?? serverNames[0];
        ComboBox serverPicker = new ComboBox
        {
            ItemsSource = serverNames,
            SelectedItem = initialServer,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(serverPicker);

        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button cancelButton = new Button { Content = "Cancel", MinWidth = 84 };
        Button changeButton = new Button { Content = "Change Server", MinWidth = 110, IsDefault = true };
        cancelButton.Click += (_, _) => dialog.Close(false);
        changeButton.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(changeButton);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        bool? confirmed = await dialog.ShowDialog<bool?>(this);
        string targetServer = serverPicker.SelectedItem?.ToString()?.Trim() ?? string.Empty;
        if (confirmed != true || string.IsNullOrWhiteSpace(targetServer))
            return;

        try
        {
            string tempFile = Path.Combine(ClientFileSources.TabsDIR, "skua_global_change_server.txt");
            File.WriteAllText(tempFile, targetServer);
            int sentCount = 0;
            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero)
                {
                    PostChildMessage(info.ChildHwnd, WM_SKUA_CHANGE_SERVER, IntPtr.Zero, IntPtr.Zero);
                    sentCount++;
                }
            }

            if (sentCount == 0)
                Ioc.Default.GetService<IDialogService>()?.ShowMessageBox("No active client windows were available to receive the server change.", "Change Server for All");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to broadcast Army Control server change: {ex.GetType().Name}: {ex.Message}");
            Ioc.Default.GetService<IDialogService>()?.ShowMessageBox("Could not send the server change to the clients.", "Change Server for All");
        }
    }

    private void MenuItem_AcceptQuest_Click(object? sender, RoutedEventArgs e)
    {
        var vm = new InputDialogViewModel("Accept Quest (Army)", "Enter quest ID to accept/register on all accounts:", "e.g., 1907", false);
        vm.DialogTextInput = _lastAcceptQuestId;
        
        if (Ioc.Default.GetRequiredService<IDialogService>().ShowDialog(vm) == true)
        {
            string questId = vm.DialogTextInput?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(questId))
            {
                _lastAcceptQuestId = questId;
                string tempFile = System.IO.Path.Combine(ClientFileSources.TabsDIR, "skua_global_accept_quest.txt");
                System.IO.File.WriteAllLines(tempFile, new[] { questId, "-1" });
                
                foreach (var info in _tabs.Values)
                    if (info.ChildHwnd != IntPtr.Zero)
                        PostChildMessage(info.ChildHwnd, WM_SKUA_ACCEPT_QUEST, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }

    private void UpdateGridViewBorderColor()
    {
        // The layout button only shows while Grid View is on.
        if (GridLayoutBorder != null)
            GridLayoutBorder.IsVisible = _isGridViewEnabled;
        if (GameSizeBorder != null)
            GameSizeBorder.IsVisible = !_isGridViewEnabled;
        if (_isGridViewEnabled)
        {
            GridViewBorder[!Border.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaSelectionBrush");
            GridViewBorder[!Border.BorderBrushProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaAccentBrush");
            if (GridViewText != null) GridViewText[!TextBlock.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaAccentBrush");
            if (GridViewIcon != null) GridViewIcon[!MaterialIcon.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaAccentBrush");
        }
        else
        {
            GridViewBorder[!Border.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaSurfaceAltBrush");
            GridViewBorder[!Border.BorderBrushProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaBorderBrush");
            if (GridViewText != null) GridViewText[!TextBlock.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaTextBrush");
            if (GridViewIcon != null) GridViewIcon[!MaterialIcon.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaTextBrush");
        }
    }

    /// <summary>
    /// Hides all embedded game windows to prevent their pixels remaining visible when the client is hidden.
    /// </summary>
    public void HideEmbeddedClients()
    {
        foreach (TabInfo info in _tabs.Values)
        {
            if (info.ChildHwnd != IntPtr.Zero)
            {
                PlatformHideChild(info.ChildHwnd);
            }
        }

        HotKeys.ActiveChildHwnd = IntPtr.Zero;
    }

    /// <summary>
    /// Restores embedded game windows after the client is shown again.
    /// </summary>
    public void ShowEmbeddedClients()
    {
        _lastX = int.MinValue;
        _lastY = int.MinValue;
        _lastW = int.MinValue;
        _lastH = int.MinValue;
        _lastActiveHwnd = IntPtr.Zero;
        ScheduleReposition();
    }

    // Places the tabs now, even mid-drag.
    private void RepositionWithFreshCache()
    {
        if (_isClosing) return;

        bool dragging = _isDraggingWindow;
        _isDraggingWindow = false;
        _lastX = int.MinValue;
        _lastY = int.MinValue;
        _lastW = int.MinValue;
        _lastH = int.MinValue;
        _lastActiveHwnd = IntPtr.Zero;
        DoReposition();
        _isDraggingWindow = dragging;
    }

    private void OnWindowPositionChanged(PixelPoint newPos)
    {
        if (!IsLoaded || _isClosing || WindowState == WindowState.Minimized || !IsVisible)
            return;

        if (_isRepositioning)
            return;

        if (_childWidth <= 1 || _childHeight <= 1)
        {
            DoReposition();
            return;
        }

        if (newPos.X == _lastFastX && newPos.Y == _lastFastY)
            return;

        _lastFastX = newPos.X;
        _lastFastY = newPos.Y;

        FastReposition(newPos);
    }

    private void FastReposition(PixelPoint newPos)
    {
        // On Linux, child windows are reparented into _hostHwnd and move with it automatically in X11.
    }

    private void ScheduleReposition()
    {
        _needsReposition = true;
        if (_repositionScheduled || _isClosing) return;

        _repositionScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _repositionScheduled = false;
            if (_isClosing || !IsLoaded) return;

            _needsReposition = false;
            DoReposition();
        }, DispatcherPriority.Render);
    }

    private void DoReposition()
    {
        if (!IsLoaded || _isClosing || _isDraggingWindow) return;

        if (_isRepositioning)
        {
            _needsReposition = true;
            return;
        }

        _isRepositioning = true;
        try
        {
            TabItem? selectedTab = InstancesTabControl.SelectedItem as TabItem;
            if (!_isGridViewEnabled && selectedTab == null)
            {
                selectedTab = (_lastSelectedTab != null && InstancesTabControl.Items.Contains(_lastSelectedTab))
                    ? _lastSelectedTab
                    : InstancesTabControl.Items.OfType<TabItem>().FirstOrDefault();

                if (selectedTab != null)
                {
                    _ignoreSelectionChange = true;
                    InstancesTabControl.SelectedItem = selectedTab;
                    _ignoreSelectionChange = false;
                }
            }

            if (!_isGridViewEnabled && selectedTab == null) return;

            if (WindowState == WindowState.Minimized || !IsVisible)
            {
                foreach (var info in _tabs.Values)
                {
                    if (info.ChildHwnd != IntPtr.Zero)
                    {
                        PlatformHideChild(info.ChildHwnd);
                        PostChildMessage(info.ChildHwnd, HotKeys.WM_SKUA_VISIBILITY, new IntPtr(4), IntPtr.Zero);
                    }
                }
                return;
            }

            Point? hostPos = HostContainer.TranslatePoint(new Point(0, 0), this);
            int x = (int)(hostPos?.X ?? 0);
            int y = Math.Max((int)(hostPos?.Y ?? 68), 68);
            int w = (int)Math.Max(HostContainer.Bounds.Width > 1 ? HostContainer.Bounds.Width : Bounds.Width, 1);
            int h = (int)Math.Max(HostContainer.Bounds.Height > 1 ? HostContainer.Bounds.Height : Bounds.Height - y, 1);

            if (w <= 1 || h <= 1)
            {
                Dispatcher.UIThread.Post(DoReposition, DispatcherPriority.Loaded);
                return;
            }

            _childOffsetX = x - Position.X;
            _childOffsetY = y - Position.Y;
            _childWidth = w;
            _childHeight = h;
            _lastFastX = Position.X;
            _lastFastY = Position.Y;

            var activeWindows = InstancesTabControl.Items.OfType<TabItem>()
                .Where(t => _tabs.ContainsKey(t))
                .Select(t => _tabs[t])
                .Where(v => v.ChildHwnd != IntPtr.Zero)
                .ToList();

            PublishChildVisibility(selectedTab, activeWindows);
            
            bool isLoading = false;
            if (_isGridViewEnabled)
            {
                isLoading = _tabs.Values.Any(v => v.ChildHwnd == IntPtr.Zero || !v.IsLoaded);
            }
            else
            {
                if (selectedTab != null && _tabs.TryGetValue(selectedTab, out TabInfo? activeInfo))
                {
                    if (!activeInfo.IsLoaded && activeInfo.Process != null)
                    {
                        string lFile = Path.Combine(ClientFileSources.TabsDIR, $"loaded_{activeInfo.Process.Id}.txt");
                        if (File.Exists(lFile))
                            activeInfo.IsLoaded = true;
                    }
                    if (activeInfo.ChildHwnd == IntPtr.Zero || !activeInfo.IsLoaded)
                        isLoading = true;
                }
                else
                {
                    isLoading = true;
                }
            }
            if (LoadingIndicator != null)
            {
                LoadingIndicator.IsVisible = isLoading;
                if (TabLoadingBar != null)
                    TabLoadingBar.IsIndeterminate = isLoading;
            }

            if (activeWindows.Count == 0) return;

            if (_isGridViewEnabled)
            {
                int n = activeWindows.Count;
                (int cols, int rows) = GridShape(_gridLayout, n);

                TabInfo? gridSelected = null;
                if (selectedTab != null)
                    _tabs.TryGetValue(selectedTab, out gridSelected);

                _gridTiles.Clear();
                for (int i = 0; i < n; i++)
                {
                    var (tileX, tileY, tileW, tileH) = GridTileRect(i, n, cols, rows, w, h);
                    _gridTiles.Add(new GridTile
                    {
                        Hwnd = activeWindows[i].ChildHwnd,
                        OffsetX = _childOffsetX + tileX,
                        OffsetY = _childOffsetY + tileY,
                        Width = tileW,
                        Height = tileH
                    });
                    PlatformMoveResizeChild(activeWindows[i].ChildHwnd, x + tileX, y + tileY, tileW, tileH);

                    if (activeWindows[i].IsThrottled)
                    {
                        activeWindows[i].IsThrottled = false;
                        PostChildMessage(activeWindows[i].ChildHwnd, WM_SKUA_THROTTLE, new IntPtr(0), IntPtr.Zero);
                    }
                    PostChildMessage(activeWindows[i].ChildHwnd, (uint)HotKeys.WM_SKUA_FOCUS, new IntPtr(1), IntPtr.Zero);
                }
                _lastGridViewState = true;
                _lastActiveHwnd = IntPtr.Zero;
                _lastTabCount = activeWindows.Count;
                if (selectedTab != null && _tabs.TryGetValue(selectedTab, out TabInfo? selInfo) && selInfo.ChildHwnd != IntPtr.Zero)
                    HotKeys.ActiveChildHwnd = selInfo.ChildHwnd;
                else if (activeWindows.Count > 0)
                    HotKeys.ActiveChildHwnd = activeWindows[0].ChildHwnd;
            }
            else
            {
                TabInfo? activeInfo = null;
                IntPtr activeChildHwnd = IntPtr.Zero;
                if (selectedTab != null && _tabs.TryGetValue(selectedTab, out activeInfo) && activeInfo.ChildHwnd != IntPtr.Zero)
                {
                    activeChildHwnd = activeInfo.ChildHwnd;
                }
                HotKeys.ActiveChildHwnd = activeChildHwnd;

                (int gameX, int gameY, int gameW, int gameH) = FitGameSize(x, y, w, h, _gameSize);
                int currentTabCount = _tabs.Count;
                if (gameX == _lastX && gameY == _lastY && gameW == _lastW && gameH == _lastH &&
                    activeChildHwnd == _lastActiveHwnd && _lastGridViewState == _isGridViewEnabled &&
                    currentTabCount == _lastTabCount)
                {
                    return;
                }

                _lastX = gameX;
                _lastY = gameY;
                _lastW = gameW;
                _lastH = gameH;
                _lastActiveHwnd = activeChildHwnd;
                _lastGridViewState = false;
                _lastTabCount = currentTabCount;

                if (activeChildHwnd != IntPtr.Zero)
                {
                    if (activeInfo == null || activeInfo.IsLoaded)
                    {
                        PlatformMoveResizeChild(activeChildHwnd, gameX, gameY, gameW, gameH, focus: true);

                        if (activeInfo != null)
                        {
                            activeInfo.IsThrottled = false;
                            PostChildMessage(activeChildHwnd, WM_SKUA_THROTTLE, new IntPtr(0), IntPtr.Zero);
                        }
                        PostChildMessage(activeChildHwnd, (uint)HotKeys.WM_SKUA_FOCUS, new IntPtr(1), IntPtr.Zero);
                    }
                    else
                    {
                        PlatformHideChild(activeChildHwnd);
                    }
                }

                foreach (var kvp in _tabs)
                {
                    if (kvp.Key == selectedTab) continue;

                    TabInfo info = kvp.Value;
                    if (info.ChildHwnd == IntPtr.Zero) continue;

                    if (!info.IsThrottled)
                    {
                        info.IsThrottled = true;
                        PostChildMessage(info.ChildHwnd, WM_SKUA_THROTTLE, new IntPtr(1), IntPtr.Zero);
                    }
                    PostChildMessage(info.ChildHwnd, (uint)HotKeys.WM_SKUA_FOCUS, IntPtr.Zero, IntPtr.Zero);

                    PlatformHideChild(info.ChildHwnd);
                }
            }
        }
        finally
        {
            _isRepositioning = false;
            if (_needsReposition)
            {
                _needsReposition = false;
                Dispatcher.UIThread.Post(DoReposition, DispatcherPriority.Render);
            }
        }
    }

    private void PublishChildVisibility(TabItem? selectedTab, List<TabInfo> activeWindows)
    {
        int selectedGridIndex = -1;
        if (_isGridViewEnabled && selectedTab != null && _tabs.TryGetValue(selectedTab, out TabInfo? selectedInfo))
            selectedGridIndex = activeWindows.IndexOf(selectedInfo);

        for (int i = 0; i < activeWindows.Count; i++)
        {
            TabInfo info = activeWindows[i];
            int visibility;
            if (_isGridViewEnabled)
            {
                bool isSelectedTile = (_gridFocusedChild != IntPtr.Zero && info.ChildHwnd == _gridFocusedChild)
                    || (_gridFocusedChild == IntPtr.Zero && i == selectedGridIndex);
                visibility = isSelectedTile ? 2 : 3;
            }
            else
            {
                visibility = selectedTab != null && _tabs.TryGetValue(selectedTab, out TabInfo? activeInfo) && ReferenceEquals(info, activeInfo) ? 0 : 1;
            }
            PostChildMessage(info.ChildHwnd, HotKeys.WM_SKUA_VISIBILITY, new IntPtr(visibility), IntPtr.Zero);
        }
    }

    private void RequestGridViewForBatch(string[] clientArgs)
    {
        int at = Array.FindIndex(clientArgs, a => string.Equals(a, "--grid-view", StringComparison.OrdinalIgnoreCase));
        if (at < 0 || at + 1 >= clientArgs.Length || _tabs.Count < 2)
            return;
        if (!_isGridViewEnabled)
            Dispatcher.UIThread.Post(EnterGridView, DispatcherPriority.Background);
    }

    // Keep the 960x550 game aspect ratio in Grid View instead of stretching the embedded player.
    private static (int X, int Y, int W, int H) GridTileRect(int index, int count, int cols, int rows, int areaW, int areaH)
    {
        int row = index / cols, col = index % cols;
        int inRow = row == rows - 1 ? count - row * cols : cols;
        int cellW = areaW / cols, cellH = areaH / rows;
        int rowShift = (cols - inRow) * cellW / 2;
        int tileW = cellW, tileH = tileW * 550 / 960;
        if (tileH > cellH)
        {
            tileH = cellH;
            tileW = tileH * 960 / 550;
        }
        return (col * cellW + rowShift + (cellW - tileW) / 2,
            row * cellH + (cellH - tileH) / 2, Math.Max(tileW, 1), Math.Max(tileH, 1));
    }
    #endregion

    #region Tab Management
    private void CloseTab(TabItem tab)
    {
        if (!_tabs.ContainsKey(tab) && !InstancesTabControl.Items.Contains(tab))
            return;

        if (_tabs.TryGetValue(tab, out TabInfo? info))
        {
            _tabs.Remove(tab);
            Task.Run(() =>
            {
                try
                {
                    KillProcessTree(info.Process);
                    if (info.ChildHwnd != IntPtr.Zero)
                    {
                        PlatformUnmapChild(info.ChildHwnd);
                    }
                    if (info.Process != null)
                    {
                        TabIpc.CleanUp(info.Process.Id);
                        try { File.Delete(Path.Combine(ClientFileSources.TabsDIR, $"{info.Process.Id}.txt")); } catch { }
                        try { File.Delete(Path.Combine(ClientFileSources.TabsDIR, $"ready_{info.Process.Id}.txt")); } catch { }
                        try { File.Delete(Path.Combine(ClientFileSources.TabsDIR, $"loaded_{info.Process.Id}.txt")); } catch { }
                    }
                }
                catch { }
            });
        }
        InstancesTabControl.Items.Remove(tab);
        UpdateTabScrollButtons();

        if (_tabs.Count == 0 || !InstancesTabControl.Items.OfType<TabItem>().Any())
        {
            // Army Queue passes this flag so its monitored host process exits after the
            // final account finishes. Without it, keep the normal reusable tab host open.
            if (_initialArgs.Contains("--exit-on-script-stop", StringComparer.OrdinalIgnoreCase))
            {
                Close();
                return;
            }

            // Keep the host window alive when the final account tab closes.
            // This allows the user to open another tab without restarting BonxieMux.
            _lastSelectedTab = null;
            _lastActiveHwnd = IntPtr.Zero;
            _isGridViewEnabled = false;

            // Explicitly keep the host alive and visible after the final child exits.
            // The host is the desktop MainWindow, so closing a child tab must not leave
            // the application with no visible window or trigger an accidental shutdown.
            if (!IsVisible)
                Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            DoReposition();
            return;
        }

        if (_isGridViewEnabled)
        {
            if (_tabs.Count <= 1)
            {
                ExitGridView();
                return;
            }

            DoReposition();
            return;
        }

        var remaining = InstancesTabControl.Items.OfType<TabItem>().FirstOrDefault();
        if (remaining != null && InstancesTabControl.SelectedItem == null)
            InstancesTabControl.SelectedItem = remaining;
    }

    // ---- Client crash recovery (BonxieMux only: local, no alerts, nothing leaves this machine) -----------

    private readonly Dictionary<string, List<DateTime>> _relaunches = new();

    /// <summary>Only clients with a saved login, at most 5 restarts per account per 30 minutes.</summary>
    private bool AllowRelaunch(string[] args)
    {
        string key = AccountKey(args);
        if (key.Length == 0)
            return false;
        if (!_relaunches.TryGetValue(key, out List<DateTime>? times))
            _relaunches[key] = times = new();
        times.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(30));
        if (times.Count >= 5)
        {
            HostLog(args, " the client keeps crashing (5 times in 30 minutes); not starting it again.");
            return false;
        }
        times.Add(DateTime.UtcNow);
        return true;
    }

    /// <summary>The client's arguments with the script it had loaded (running or not), from its state file.</summary>
    internal static string[] WithLastScript(string[] args, int pid)
    {
        try
        {
            string file = Path.Combine(ClientFileSources.TabsDIR, $"{pid}.state");
            if (!File.Exists(file))
                return args;
            string[] lines = File.ReadAllLines(file);
            File.Delete(file);
            string script = lines.Length > 0 ? lines[0].Trim() : "";
            bool running = lines.Length > 1 && lines[1].Trim() == "1";
            if (script.Length == 0 || !File.Exists(script))
                return args;
            List<string> result = new();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] is "--run-script" or "--load-script")
                {
                    i++; // skip its value
                    continue;
                }
                if (args[i] == "--unattended")
                    continue;
                result.Add(args[i]);
            }
            result.Add(running ? "--run-script" : "--load-script");
            result.Add(script);
            if (running)
                result.Add("--unattended"); // use the script's saved options (no options window to click)
            return result.ToArray();
        }
        catch
        {
            return args;
        }
    }

    private static void HostLog(string[] args, string message)
    {
        try
        {
            string who = AccountKey(args);
            string path = Path.Combine(ClientFileSources.SkuaDIR, "client-recovery.log");
            FileInfo info = new(path);
            if (info.Exists && info.Length > 200_000)
                info.Delete();
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{(who.Length > 0 ? who : "?")}] (tab host){message}{Environment.NewLine}");
        }
        catch { }
    }

    public void AddNewInstance(string rawArgs)
    {
        AddNewInstance(ParseRawCommandLine(rawArgs));
    }

    public void AddNewInstance(string[]? clientArgs = null)
    {
        clientArgs ??= Array.Empty<string>();

        int availableId = 1;
        var currentTitles = InstancesTabControl.Items.OfType<TabItem>()
            .Select(t => t.Header as StackPanel)
            .Where(p => p != null)
            .Select(p => p!.Children.OfType<TextBlock>().FirstOrDefault(tb => tb.Text != "✕")?.Text)
            .Where(t => t != null && (t.StartsWith("User ") || t.StartsWith("Skua ")))
            .Select(t => { 
                string numStr = t!.StartsWith("User ") ? t.Substring(5) : t.Substring(5);
                int.TryParse(numStr, out int id); 
                return id; 
            })
            .ToList();

        while (currentTitles.Contains(availableId))
        {
            availableId++;
        }
        
        string tabName = "User " + availableId;
        for (int i = 0; i < clientArgs.Length; i++)
        {
            if ((clientArgs[i] == "-u" || clientArgs[i] == "--user") && i + 1 < clientArgs.Length)
            {
                if (!string.IsNullOrWhiteSpace(clientArgs[i + 1]))
                    tabName = clientArgs[i + 1];
                break;
            }
        }

        #region Tab Header UI
        StackPanel headerPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        
        MaterialIcon tabIcon = new MaterialIcon
        {
            Kind = MaterialIconKind.ShieldAccountOutline,
            Width = 13,
            Height = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        tabIcon[!MaterialIcon.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaAccentBrush");

        TextBox editTitle = new TextBox
        {
            Text = tabName, 
            IsVisible = false, 
            MinWidth = 60, 
            MaxWidth = 180,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(0, 0, 6, 0),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
        };
        editTitle[!TextBox.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaSurfaceAltBrush");
        editTitle[!TextBox.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaTextBrush");
        editTitle[!TextBox.CaretBrushProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaTextBrush");
        editTitle[!TextBox.BorderBrushProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaBorderBrush");
        
        TextBlock title = new TextBlock
        {
            Text = tabName,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            FontWeight = FontWeight.SemiBold,
            FontSize = 12
        };
        title[!TextBlock.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaTextBrush");

        Border closeBtn = new Border
        {
            Background = Brushes.Transparent,
            Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            VerticalAlignment = VerticalAlignment.Center
        };
        TextBlock closeTxt = new TextBlock
        {
            Text = "✕",
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        closeTxt[!TextBlock.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaMutedTextBrush");
        closeBtn.Child = closeTxt;

        // The icon sits in a grid so the account that is followed can wear a crown above it.
        MaterialIcon crown = new MaterialIcon
        {
            Kind = MaterialIconKind.Crown,
            Width = 10,
            Height = 10,
            IsVisible = false,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -8, 6, 0),
            Foreground = new SolidColorBrush(Color.Parse("#FBBF24")),
        };
        Grid iconHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
        iconHost.Children.Add(tabIcon);
        iconHost.Children.Add(crown);
        headerPanel.Children.Add(iconHost);
        headerPanel.Children.Add(editTitle);
        headerPanel.Children.Add(title);
        headerPanel.Children.Add(closeBtn);

        closeBtn.PointerEntered += (s, ev) => { closeTxt.Foreground = Brushes.White; closeBtn.Background = new SolidColorBrush(Color.Parse("#EF4444")); };
        closeBtn.PointerExited += (s, ev) => { closeTxt[!TextBlock.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaMutedTextBrush"); closeBtn.Background = Brushes.Transparent; };
        
        TabInfo info = new TabInfo { AccountOrderKey = AccountKey(clientArgs ?? Array.Empty<string>()), HeaderPanel = headerPanel, Crown = crown };
        
        Action closeEditMode = () =>
        {
            if (!editTitle.IsVisible) return;
            string newName = string.IsNullOrWhiteSpace(editTitle.Text) ? title.Text : editTitle.Text;
            if (title.Text != newName)
            {
                title.Text = newName;
                info.IsCustomNamed = true;
            }
            title.IsVisible = true;
            editTitle.IsVisible = false;
        };

        title.DoubleTapped += (s, ev) =>
        {
            editTitle.Text = title.Text;
            title.IsVisible = false;
            editTitle.IsVisible = true;
            
            Dispatcher.UIThread.Post(() => {
                editTitle.Focus();
                editTitle.SelectAll();
            }, DispatcherPriority.Input);
            
            ev.Handled = true;
        };
        
        editTitle.LostFocus += (s, ev) => closeEditMode();
        
        editTitle.KeyDown += (s, ev) =>
        {
            if (ev.Key == Key.Enter)
            {
                closeEditMode();
                ev.Handled = true;
            }
            else if (ev.Key == Key.Escape)
            {
                editTitle.Text = title.Text; // revert
                closeEditMode();
                ev.Handled = true;
            }
        };
        #endregion

        TabItem newTab = new TabItem { Header = headerPanel };
        DragDrop.SetAllowDrop(newTab, true);

        InstancesTabControl.Items.Add(newTab);
        if (InstancesTabControl.SelectedItem == null)
        {
            InstancesTabControl.SelectedItem = newTab;
        }
        Dispatcher.UIThread.Post(() =>
        {
            TabScrollViewer.Offset = new Vector(Math.Max(0, TabScrollViewer.Extent.Width - TabScrollViewer.Viewport.Width), 0);
            UpdateTabScrollButtons();
        }, DispatcherPriority.Background);
        
        _tabs[newTab] = info;
        RequestGridViewForBatch(clientArgs);

        EnqueueSpawn(() =>
        {
            if (_isClosing || !_tabs.ContainsKey(newTab))
            {
                _isSpawning = false;
                ProcessNextSpawn();
                return;
            }
            
            Process p = new Process();
            string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            p.StartInfo.FileName = exe;
            if (System.IO.Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                p.StartInfo.ArgumentList.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "BonxieMux.dll"));
            }
            p.StartInfo.ArgumentList.Add("--embed");
            p.StartInfo.ArgumentList.Add(_hostHwnd.ToInt64().ToString());
            p.StartInfo.ArgumentList.Add("--host-pid");
            p.StartInfo.ArgumentList.Add(Process.GetCurrentProcess().Id.ToString());

            string? embeddedUsername = null;
            string? embeddedPassword = null;
            for (int i = 0; i < clientArgs.Length; i++)
            {
                string arg = clientArgs[i];
                if ((string.Equals(arg, "-u", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--user", StringComparison.OrdinalIgnoreCase)) && i + 1 < clientArgs.Length)
                {
                    embeddedUsername = clientArgs[i + 1];
                    i++; // skip username value
                    continue;
                }
                if ((string.Equals(arg, "-p", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--password", StringComparison.OrdinalIgnoreCase)) && i + 1 < clientArgs.Length)
                {
                    embeddedPassword = clientArgs[i + 1];
                    i++; // skip password value
                    continue;
                }

                if (!string.IsNullOrEmpty(arg))
                    p.StartInfo.ArgumentList.Add(arg);
            }

            p.StartInfo.EnvironmentVariables.Remove("BONXIEMUX_USERNAME");
            p.StartInfo.EnvironmentVariables.Remove("BONXIEMUX_PASSWORD");

            if (!string.IsNullOrWhiteSpace(embeddedUsername))
            {
                p.StartInfo.EnvironmentVariables["BONXIEMUX_USERNAME"] = embeddedUsername;
            }
            if (!string.IsNullOrWhiteSpace(embeddedPassword))
            {
                p.StartInfo.EnvironmentVariables["BONXIEMUX_PASSWORD"] = embeddedPassword;
            }

            p.EnableRaisingEvents = true;
            p.Exited += (s, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_isClosing)
                    {
                        // Still in _tabs = nobody closed it (closing a tab removes it first): it crashed or
                        // was killed. Start it again with the same login (and its script) before closing
                        // the old tab, so the host doesn't close with its last tab.
                        int code = 0;
                        try { code = p.ExitCode; } catch { }
                        if (_tabs.ContainsKey(newTab) && code != 0 && AllowRelaunch(clientArgs))
                        {
                            string[] relaunchArgs = WithLastScript(clientArgs, p.Id);
                            HostLog(clientArgs, $" the client closed unexpectedly (exit code {code}); starting it again.");
                            AddNewInstance(relaunchArgs);
                        }
                        CloseTab(newTab);
                    }
                });
            };

            try
            {
                p.Start();
            }
            catch (Exception ex)
            {
                p.Dispose();
                _isSpawning = false;
                Trace.WriteLine($"BonxieMux failed to start child process for tab '{tabName}': {ex}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (_tabs.ContainsKey(newTab))
                        CloseTab(newTab);
                    ProcessNextSpawn();
                }, DispatcherPriority.Background);
                return;
            }
            info.Process = p;
            HotKeys.RegisterSkuaPid((uint)p.Id);

            int pid = p.Id;
            Task.Run(async () =>
            {
                bool attached = false;
                string readyFile = Path.Combine(ClientFileSources.TabsDIR, $"ready_{pid}.txt");
                for (int attempt = 0; attempt < 300 && !p.HasExited && info.ChildHwnd == IntPtr.Zero && !_isClosing; attempt++)
                {
                    await Task.Delay(50);
                    if (info.ChildHwnd != IntPtr.Zero) break;
                    IntPtr childHwnd = IntPtr.Zero;
                    if (File.Exists(readyFile))
                    {
                        try
                        {
                            string text = File.ReadAllText(readyFile).Trim();
                            if (long.TryParse(text, out long handleVal) && handleVal != 0)
                                childHwnd = (IntPtr)handleVal;
                        }
                        catch { }
                    }


                    if (childHwnd != IntPtr.Zero)
                    {
                        attached = true;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!_isClosing && info.ChildHwnd == IntPtr.Zero)
                            {
                                AttachChildWindow(info, childHwnd);
                            }
                        });
                        break;
                    }
                }

                if (!attached && info.ChildHwnd == IntPtr.Zero)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _isSpawning = false;
                        ProcessNextSpawn();
                    });
                }
            });
        });

        #region Tab Interactions
        closeBtn.PointerPressed += (s, ev) => 
        {
            CloseTab(newTab);
            ev.Handled = true;
        };
        
        headerPanel.PointerReleased += (s, ev) =>
        {
            if (ev.InitialPressMouseButton == MouseButton.Middle)
                CloseTab(newTab);
        };

        ContextMenu ctx = new ContextMenu();
        MenuItem closeItem = new MenuItem { Header = "Close Tab" };
        closeItem.Click += (s, ev) => CloseTab(newTab);
        MenuItem closeOthersItem = new MenuItem { Header = "Close Other Tabs" };
        closeOthersItem.Click += (s, ev) =>
        {
            var toRemove = InstancesTabControl.Items.OfType<TabItem>().Where(t => t != newTab && t.Header is StackPanel).ToList();
            foreach (var tab in toRemove) CloseTab(tab);
        };
        MenuItem closeRightItem = new MenuItem { Header = "Close Tabs to the Right" };
        closeRightItem.Click += (s, ev) =>
        {
            int idx = InstancesTabControl.Items.IndexOf(newTab);
            var toRemove = InstancesTabControl.Items.OfType<TabItem>().Where((t, i) => i > idx && t.Header is StackPanel).ToList();
            foreach (var tab in toRemove) CloseTab(tab);
        };
        ctx.Items.Add(closeItem);
        ctx.Items.Add(closeOthersItem);
        ctx.Items.Add(closeRightItem);
        ctx.Items.Add(new Separator());

        MenuItem addToGroupItem = new MenuItem
        {
            Header = "Add to Group",
            Icon = new MaterialIcon { Kind = MaterialIconKind.AccountMultiplePlus, Width = 14, Height = 14 }
        };
        ctx.Items.Add(addToGroupItem);

        void UpdateGroupSubMenu()
        {
            PopulateAddToGroupMenu(addToGroupItem, newTab, info, title.Text);
        }

        UpdateGroupSubMenu();
        headerPanel.ContextRequested += (s, ev) => UpdateGroupSubMenu();
        newTab.ContextRequested += (s, ev) => UpdateGroupSubMenu();

        headerPanel.ContextMenu = ctx;
        newTab.ContextMenu = ctx;

        Point startPoint = new Point();
        headerPanel.PointerPressed += (s, ev) => 
        {
            var prop = ev.GetCurrentPoint(headerPanel).Properties;
            if (prop.IsRightButtonPressed)
            {
                InstancesTabControl.SelectedItem = newTab;
                return;
            }

            if (!prop.IsLeftButtonPressed)
                return;

            startPoint = ev.GetPosition(null);
            InstancesTabControl.SelectedItem = newTab;
            
            if (_isGridViewEnabled)
            {
                _isGridViewEnabled = false;
                UpdateGridViewBorderColor();
                foreach (var tInfo in _tabs.Values)
                {
                    if (tInfo.ChildHwnd != IntPtr.Zero)
                    {
                        PostChildMessage(tInfo.ChildHwnd, WM_SKUA_GRIDVIEW, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                DoReposition();
            }
            ev.Handled = true;
        };

        newTab.PointerPressed += (s, ev) =>
        {
            if (ev.GetCurrentPoint(newTab).Properties.IsRightButtonPressed)
            {
                InstancesTabControl.SelectedItem = newTab;
            }
        };
        
        headerPanel.PointerMoved += async (s, ev) =>
        {
            if (editTitle.IsVisible) return;
            
            if (ev.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            {
                Point mousePos = ev.GetPosition(null);
                Vector diff = startPoint - mousePos;
                if (Math.Abs(diff.X) > 4 || Math.Abs(diff.Y) > 4)
                {
                    var dragData = new global::Avalonia.Input.DataObject();
                    dragData.Set("TabItem", newTab);
                    await DragDrop.DoDragDrop(ev, dragData, DragDropEffects.Move);
                }
            }
        };
        
        newTab.AddHandler(DragDrop.DropEvent, (s, ev) =>
        {
            if (ev.Data.Contains("TabItem"))
            {
                TabItem? droppedTab = ev.Data.Get("TabItem") as TabItem;
                if (droppedTab != null && droppedTab != newTab)
                {
                    int targetIndex = InstancesTabControl.Items.IndexOf(newTab);
                    InstancesTabControl.Items.Remove(droppedTab);
                    InstancesTabControl.Items.Insert(targetIndex, droppedTab);
                    InstancesTabControl.SelectedItem = droppedTab;
                }
            }
        });
        #endregion
    }

    #region Group Assignment Helper
    private string? GetTabLoggedInUsername(TabItem tab, TabInfo? info, string currentTitle)
    {
        if (info?.Process != null && !info.Process.HasExited)
        {
            try
            {
                string file = Path.Combine(ClientFileSources.TabsDIR, $"{info.Process.Id}.txt");
                if (File.Exists(file))
                {
                    string name = File.ReadAllText(file).Trim();
                    if (!string.IsNullOrWhiteSpace(name) &&
                        !name.StartsWith("User ", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith("Skua ", StringComparison.OrdinalIgnoreCase))
                    {
                        return name;
                    }
                }
            }
            catch { }
        }

        string title = currentTitle?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(title) &&
            !title.StartsWith("User ", StringComparison.OrdinalIgnoreCase) &&
            !title.StartsWith("Skua ", StringComparison.OrdinalIgnoreCase))
        {
            return title;
        }

        return null;
    }

    private void PopulateAddToGroupMenu(MenuItem parentItem, TabItem tab, TabInfo? info, string currentTitle)
    {
        parentItem.Items.Clear();

        string? loggedInUsername = GetTabLoggedInUsername(tab, info, currentTitle);
        if (string.IsNullOrWhiteSpace(loggedInUsername))
        {
            MenuItem notLoggedInItem = new MenuItem
            {
                Header = "Please log in first",
                Icon = new MaterialIcon { Kind = MaterialIconKind.AlertCircleOutline, Width = 14, Height = 14 }
            };
            notLoggedInItem.Click += (s, ev) =>
            {
                Ioc.Default.GetService<IDialogService>()?.ShowMessageBox(
                    "Please log in to your game account first before adding it to an account group.",
                    "Account Not Logged In");
            };
            parentItem.Items.Add(notLoggedInItem);
            return;
        }

        string accountName = loggedInUsername;
        ISettingsService? settings = Ioc.Default.GetService<ISettingsService>();
        settings?.ReloadSettings();
        List<GroupData> groups = settings?.Get<List<GroupData>>("AccountGroups") ?? new List<GroupData>();

        if (groups.Count > 0)
        {
            foreach (var group in groups)
            {
                bool alreadyInGroup = group.Accounts.Contains(accountName, StringComparer.OrdinalIgnoreCase);
                MenuItem groupItem = new MenuItem
                {
                    Header = group.Name,
                    Icon = alreadyInGroup ? new MaterialIcon { Kind = MaterialIconKind.Check, Width = 14, Height = 14 } : null
                };
                groupItem.Click += (s, ev) =>
                {
                    AddAccountToGroupAndManaged(accountName, group, settings);
                };
                parentItem.Items.Add(groupItem);
            }
            parentItem.Items.Add(new Separator());
        }
        else
        {
            MenuItem emptyItem = new MenuItem { Header = "No groups created", IsEnabled = false };
            parentItem.Items.Add(emptyItem);
            parentItem.Items.Add(new Separator());
        }

        MenuItem newGroupItem = new MenuItem
        {
            Header = "+ New Group...",
            Icon = new MaterialIcon { Kind = MaterialIconKind.Plus, Width = 14, Height = 14 }
        };
        newGroupItem.Click += (s, ev) =>
        {
            var dialogService = Ioc.Default.GetService<IDialogService>();
            if (dialogService == null) return;

            var inputVm = new InputDialogViewModel("New Group", "Enter group name:", "Group Name", false);
            if (dialogService.ShowDialog(inputVm) == true && !string.IsNullOrWhiteSpace(inputVm.DialogTextInput))
            {
                string newGroupName = inputVm.DialogTextInput.Trim();
                var existing = groups.FirstOrDefault(g => g.Name.Equals(newGroupName, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = new GroupData { Name = newGroupName };
                    groups.Add(existing);
                }
                AddAccountToGroupAndManaged(accountName, existing, settings);
            }
        };
        parentItem.Items.Add(newGroupItem);
    }

    private void AddAccountToGroupAndManaged(string accountName, GroupData group, ISettingsService? settings)
    {
        if (settings == null) return;
        var dialogService = Ioc.Default.GetService<IDialogService>();

        settings.ReloadSettings();

        // 1. Ensure the account exists in ManagedAccounts so it appears in BonxieMux Manager!
        var accs = settings.Get<Dictionary<string, AccountData>>("ManagedAccounts") ?? new();
        if (!accs.ContainsKey(accountName))
        {
            accs[accountName] = new AccountData
            {
                DisplayName = accountName,
                Password = string.Empty,
                Tags = new List<string>(),
                UseCheck = false
            };
            settings.Set("ManagedAccounts", accs);
        }

        // 2. Ensure account is in the selected group
        var currentGroups = settings.Get<List<GroupData>>("AccountGroups") ?? new List<GroupData>();
        var targetGroup = currentGroups.FirstOrDefault(g => g.Name.Equals(group.Name, StringComparison.OrdinalIgnoreCase));
        if (targetGroup == null)
        {
            targetGroup = new GroupData { Name = group.Name };
            currentGroups.Add(targetGroup);
        }

        if (!targetGroup.Accounts.Contains(accountName, StringComparer.OrdinalIgnoreCase))
        {
            targetGroup.Accounts.Add(accountName);
            settings.Set("AccountGroups", currentGroups);
            dialogService?.ShowMessageBox($"Added '{accountName}' to group '{targetGroup.Name}'.", "Add to Group");
        }
        else
        {
            dialogService?.ShowMessageBox($"'{accountName}' is already in group '{targetGroup.Name}'.", "Add to Group");
        }
    }
    #endregion
    #endregion
}
