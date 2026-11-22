using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        if (msg == (uint)SkuaIpcMessages.CheckLogin)
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
        public int LastVisibility { get; set; } = -1;
        public bool IsCustomNamed { get; set; } = false;
        public int ClientNumber { get; set; } = 1;
        public string LastCharacterName { get; set; } = string.Empty;
        public bool IsLoaded { get; set; } = false;
        public bool IsDetached { get; set; } = false;
        public bool WasDetachedBeforeGrid { get; set; } = false;
        public int DetachedScreenX { get; set; } = 0;
        public int DetachedScreenY { get; set; } = 0;
        public MaterialIcon? DetachedIcon { get; set; }
        public TabItem? TabItem { get; set; }
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
        Skua.Shared.Avalonia.Services.WindowPlacementHelper.CenterOnActiveScreen(this);
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
                foreach (var info in _tabs.Values)
                    info.LastVisibility = -1;
            }

            ScheduleReposition();
        };
        Activated += (s, e) =>
        {
            _lastActiveHwnd = IntPtr.Zero;
            _lastGridViewState = false;
            ScheduleReposition();
            UpdateTabScrollButtons();
            RefreshHeadlessOverlays();
            if (_isGridViewEnabled)
                foreach (var info in _tabs.Values)
                    if (info.ChildHwnd != IntPtr.Zero)
                        PostChildMessage(info.ChildHwnd, (uint)SkuaIpcMessages.Focus, new IntPtr(1), IntPtr.Zero);
            else if (HotKeys.ActiveChildHwnd != IntPtr.Zero)
                PostChildMessage(HotKeys.ActiveChildHwnd, (uint)SkuaIpcMessages.Focus, new IntPtr(1), IntPtr.Zero);
        };
        StartGridFocusTracking();
        Deactivated += (s, e) =>
        {
            CloseHostDropdowns();
            RefreshHeadlessOverlays();
            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero)
                    PostChildMessage(info.ChildHwnd, (uint)SkuaIpcMessages.Focus, IntPtr.Zero, IntPtr.Zero);
            }
        };
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;

        StrongReferenceMessenger.Default.Register<TabbedHostWindow, LoadScriptMessage, int>(this, (int)MessageChannels.ScriptStatus, (r, m) => r.BroadcastScriptAction(m.Path, SkuaIpcMessages.LoadScript));
        StrongReferenceMessenger.Default.Register<TabbedHostWindow, ScriptSchedulerViewModel.ArmySchedulerMessage>(this, (r, m) => r.OnArmySchedulerStart(m));
        StrongReferenceMessenger.Default.Register<TabbedHostWindow, ScriptSchedulerViewModel.ArmySchedulerStopMessage>(this, (r, m) => r.OnArmySchedulerStop(m));
        StrongReferenceMessenger.Default.Register<TabbedHostWindow, Skua.App.Avalonia.AppStartup.AppSettingChangedMessage>(this, (r, m) =>
        {
            if (m.Key == "ShowCharNameOnTabs")
                Dispatcher.UIThread.Post(r.RefreshAllTabTitles);
            else if (m.Key == "ShowCharNameInDetachedTitle" && m.Value is bool b)
            {
                foreach (var info in r._tabs.Values)
                    if (info.ChildHwnd != IntPtr.Zero)
                        r.PostChildMessage(info.ChildHwnd, SkuaIpcMessages.SetOption, new IntPtr(11), new IntPtr(b ? 1 : 0));
            }
        });

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
        SetupTabDragHandlers();

        AddHandler(InputElement.KeyDownEvent, (s, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (_gameOnly)
                {
                    ToggleGameOnly();
                    e.Handled = true;
                }
                else if (WindowState == WindowState.FullScreen)
                {
                    ToggleFullScreen();
                    e.Handled = true;
                }
            }
        }, RoutingStrategies.Tunnel);
    }

    #region Tab Drag & Reordering System
    private static readonly global::Avalonia.Input.Cursor _horizontalDragCursor = new(global::Avalonia.Input.StandardCursorType.SizeWestEast);
    private TabItem? _draggedTab;
    private Point _dragStartPos;
    private bool _isDraggingTab;
    private TearOffGhostWindow? _ghostWindow;
    private int _lastGhostX = int.MinValue;
    private int _lastGhostY = int.MinValue;
    private bool _lastGhostAsWindow = true;

    private class TearOffGhostWindow : Window
    {
        private readonly TextBlock _titleText;
        private readonly Border _bodyArea;
        private readonly Border _rootBorder;
        private readonly Border _titleBorder;
        private bool _isWindowMode = true;

        public TearOffGhostWindow()
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
            ShowInTaskbar = false;
            CanResize = false;
            ShowActivated = false;
            Focusable = false;
            Width = 948;
            Height = 600;
            Opacity = 0.85;
            Background = Brushes.Transparent;
            Topmost = true;
            IsHitTestVisible = false;

            _titleText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var tabIcon = new MaterialIcon
            {
                Kind = MaterialIconKind.ShieldAccountOutline,
                Width = 13,
                Height = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse("#38BDF8"))
            };

            var titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0),
                Children = { tabIcon, _titleText }
            };

            _bodyArea = new Border
            {
                [Grid.RowProperty] = 1,
                Child = new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    Opacity = 0.3,
                    Child = new MaterialIcon
                    {
                        Kind = MaterialIconKind.DockWindow,
                        Width = 96,
                        Height = 96,
                        Foreground = new SolidColorBrush(Color.Parse("#38BDF8"))
                    }
                }
            };

            _titleBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 15, 23, 42)),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Child = titleRow
            };

            _rootBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(160, 20, 24, 33)),
                BorderBrush = new SolidColorBrush(Color.Parse("#38BDF8")),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Child = new Grid
                {
                    RowDefinitions = new RowDefinitions("32,*"),
                    Children =
                    {
                        _titleBorder,
                        _bodyArea
                    }
                }
            };

            Content = _rootBorder;
        }

        public void SetMode(bool asWindow)
        {
            if (_isWindowMode == asWindow) return;
            _isWindowMode = asWindow;
            if (asWindow)
            {
                Width = 948;
                Height = 600;
                _bodyArea.IsVisible = true;
                _rootBorder.CornerRadius = new CornerRadius(8);
                _titleBorder.CornerRadius = new CornerRadius(6, 6, 0, 0);
            }
            else
            {
                Width = 140;
                Height = 34;
                _bodyArea.IsVisible = false;
                _rootBorder.CornerRadius = new CornerRadius(6);
                _titleBorder.CornerRadius = new CornerRadius(4);
            }
        }

        public void UpdateTitle(string title)
        {
            _titleText.Text = string.IsNullOrWhiteSpace(title) ? "Skua Client" : title;
        }
    }

    private void ShowGhostPreview(string title, int screenX, int screenY, bool asWindow)
    {
        if (OperatingSystem.IsLinux())
        {
            Cursor = asWindow
                ? new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.DragMove)
                : _horizontalDragCursor;
            return;
        }

        if (_ghostWindow == null)
        {
            _ghostWindow = new TearOffGhostWindow();
        }
        bool modeChanged = _lastGhostAsWindow != asWindow;
        _lastGhostAsWindow = asWindow;
        _ghostWindow.SetMode(asWindow);
        _ghostWindow.UpdateTitle(title);
        if (!_ghostWindow.IsVisible)
        {
            _ghostWindow.Position = new PixelPoint(screenX, screenY);
            _lastGhostX = screenX;
            _lastGhostY = screenY;
            _ghostWindow.Show();
        }
        else if (modeChanged || Math.Abs(screenX - _lastGhostX) >= 2 || Math.Abs(screenY - _lastGhostY) >= 2)
        {
            _ghostWindow.Position = new PixelPoint(screenX, screenY);
            _lastGhostX = screenX;
            _lastGhostY = screenY;
        }
    }

    private void HideGhostPreview()
    {
        if (_ghostWindow != null && _ghostWindow.IsVisible)
        {
            _ghostWindow.Hide();
            _lastGhostX = int.MinValue;
            _lastGhostY = int.MinValue;
        }
    }

    private void ApplyTabDragVisuals(TabItem tab)
    {
        if (!tab.Classes.Contains("tab-dragging"))
        {
            tab.Classes.Add("tab-dragging");
        }
        if (tab.Header is StackPanel sp)
        {
            sp.Background = new SolidColorBrush(Color.FromArgb(60, 56, 189, 248));
        }
        Cursor = _horizontalDragCursor;
    }

    private void ClearTabDragVisuals(TabItem tab)
    {
        tab.Classes.Remove("tab-dragging");
        if (tab.Header is StackPanel sp)
        {
            sp.Background = Brushes.Transparent;
        }
        Cursor = global::Avalonia.Input.Cursor.Default;
    }

    private void SetupTabDragHandlers()
    {
        this.AddHandler(InputElement.PointerMovedEvent, OnWindowPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        this.AddHandler(InputElement.PointerReleasedEvent, OnWindowPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        this.AddHandler(InputElement.PointerCaptureLostEvent, (s, e) =>
        {
            // Do not abort drag while mouse button is still pressed;
            // mapping ghost windows on X11 fires a transient capture-lost notification.
            if (_draggedTab != null)
            {
                try { e.Pointer.Capture(this); } catch { }
                return;
            }

            HideGhostPreview();
            _isDraggingTab = false;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs ev)
    {
        if (_draggedTab == null) return;

        var prop = ev.GetCurrentPoint(this).Properties;
        if (!prop.IsLeftButtonPressed)
        {
            if (_draggedTab != null)
            {
                ev.Pointer.Capture(null);
                HideGhostPreview();
                ClearTabDragVisuals(_draggedTab);
                _draggedTab = null;
                _isDraggingTab = false;
            }
            return;
        }

        Point currentPoint = ev.GetPosition(this);
        Vector diff = currentPoint - _dragStartPos;
        if (!_isDraggingTab && (Math.Abs(diff.X) > 6 || Math.Abs(diff.Y) > 6))
        {
            _isDraggingTab = true;
            ApplyTabDragVisuals(_draggedTab);
        }

        if (_isDraggingTab)
        {
            if (ev.Pointer.Captured != this)
            {
                ev.Pointer.Capture(this);
            }

            var tabCtrlPos = InstancesTabControl.TranslatePoint(new Point(0, 0), this);
            double tabTop = tabCtrlPos?.Y ?? 0;
            double tabBottom = tabTop + Math.Max(InstancesTabControl.Bounds.Height, 34);

            bool isOutsideTabStrip = currentPoint.Y < tabTop - 15 || 
                                     currentPoint.Y > tabBottom + 30 ||
                                     currentPoint.X < -15 ||
                                     currentPoint.X > this.Bounds.Width + 15;

            if (isOutsideTabStrip)
            {
                var (gx, gy) = GetGlobalPointerPosition();
                int targetX = gx >= 0 ? gx - 200 : (int)(this.Position.X + currentPoint.X - 200);
                int targetY = gy >= 0 ? gy - 16 : (int)(this.Position.Y + currentPoint.Y - 16);

                ShowGhostPreview(GetTabTitle(_draggedTab), targetX, targetY, asWindow: true);
            }
            else
            {
                var (gx, gy) = GetGlobalPointerPosition();
                int tabGhostX = gx >= 0 ? gx - 40 : (int)(this.Position.X + currentPoint.X - 40);
                int tabGhostY = gy >= 0 ? gy - 17 : (int)(this.Position.Y + currentPoint.Y - 17);

                ShowGhostPreview(GetTabTitle(_draggedTab), tabGhostX, tabGhostY, asWindow: false);

                int currentIndex = InstancesTabControl.Items.IndexOf(_draggedTab);
                if (currentIndex >= 0)
                {
                    var tabItems = InstancesTabControl.Items.OfType<TabItem>().ToList();
                    foreach (var otherTab in tabItems)
                    {
                        if (otherTab == _draggedTab) continue;
                        var otherBounds = otherTab.TranslatePoint(new Point(0, 0), this);
                        if (otherBounds.HasValue)
                        {
                            double otherCenterX = otherBounds.Value.X + (otherTab.Bounds.Width / 2.0);
                            int otherIndex = InstancesTabControl.Items.IndexOf(otherTab);
                            if (currentIndex < otherIndex && currentPoint.X > otherCenterX)
                            {
                                _ignoreSelectionChange = true;
                                try
                                {
                                    InstancesTabControl.Items.Remove(_draggedTab);
                                    InstancesTabControl.Items.Insert(otherIndex, _draggedTab);
                                    InstancesTabControl.SelectedItem = _draggedTab;
                                }
                                finally
                                {
                                    _ignoreSelectionChange = false;
                                }
                                ApplyTabDragVisuals(_draggedTab);
                                break;
                            }
                            else if (currentIndex > otherIndex && currentPoint.X < otherCenterX)
                            {
                                _ignoreSelectionChange = true;
                                try
                                {
                                    InstancesTabControl.Items.Remove(_draggedTab);
                                    InstancesTabControl.Items.Insert(otherIndex, _draggedTab);
                                    InstancesTabControl.SelectedItem = _draggedTab;
                                }
                                finally
                                {
                                    _ignoreSelectionChange = false;
                                }
                                ApplyTabDragVisuals(_draggedTab);
                                break;
                            }
                        }
                    }
                }
            }
            ev.Handled = true;
        }
    }

    private void OnWindowPointerReleased(object? sender, PointerReleasedEventArgs ev)
    {
        if (_draggedTab == null) return;

        TabItem tabToProcess = _draggedTab;
        bool wasDragging = _isDraggingTab;

        ev.Pointer.Capture(null);
        HideGhostPreview();
        ClearTabDragVisuals(tabToProcess);
        _draggedTab = null;
        _isDraggingTab = false;

        if (wasDragging)
        {
            Point releasePoint = ev.GetPosition(this);
            var tabCtrlPos = InstancesTabControl.TranslatePoint(new Point(0, 0), this);
            double tabTop = tabCtrlPos?.Y ?? 0;
            double tabBottom = tabTop + Math.Max(InstancesTabControl.Bounds.Height, 34);

            bool isOutsideTabStrip = releasePoint.Y < tabTop - 15 || 
                                     releasePoint.Y > tabBottom + 30 ||
                                     releasePoint.X < -15 ||
                                     releasePoint.X > this.Bounds.Width + 15;

            if (isOutsideTabStrip)
            {
                var (gx, gy) = GetGlobalPointerPosition();
                int targetX = gx >= 0 ? gx - 200 : (int)(this.Position.X + releasePoint.X - 200);
                int targetY = gy >= 0 ? gy - 16 : (int)(this.Position.Y + releasePoint.Y - 16);
                DetachTab(tabToProcess, new PixelPoint(targetX, targetY));
            }
            ev.Handled = true;
        }
    }
    #endregion

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
                        if (!_tabs.TryGetValue(tab, out TabInfo? tInfo))
                            continue;

                        tInfo.LastCharacterName = username;
                        ApplyTabHeaderTitle(tab, tInfo);
                    }
                }, DispatcherPriority.Background);
            }
        });
    }

    private bool ShouldShowCharNameOnTabs()
    {
        try
        {
            var options = Ioc.Default.GetService<IScriptOption>();
            if (options?.StreamerMode == true)
                return false;
            var settings = Ioc.Default.GetService<ISettingsService>();
            return settings?.Get("ShowCharNameOnTabs", true) ?? true;
        }
        catch
        {
            return true;
        }
    }

    private void ApplyTabHeaderTitle(TabItem tab, TabInfo info)
    {
        if (tab.Header is not StackPanel headerPanel)
            return;

        bool showCharName = ShouldShowCharNameOnTabs();
        if (info.IsCustomNamed && showCharName)
            return;

        string displayName = showCharName && !string.IsNullOrWhiteSpace(info.LastCharacterName)
            ? info.LastCharacterName
            : $"Tab {info.ClientNumber}";

        var titleBlock = headerPanel.Children.OfType<TextBlock>().FirstOrDefault(tb => tb.Text != "✕");
        var editBox = headerPanel.Children.OfType<TextBox>().FirstOrDefault();
        if (titleBlock != null && titleBlock.Text != displayName)
        {
            titleBlock.Text = displayName;
            if (editBox != null && !editBox.IsVisible)
                editBox.Text = displayName;
        }
    }

    private void RefreshAllTabTitles()
    {
        foreach (var kvp in _tabs)
            ApplyTabHeaderTitle(kvp.Key, kvp.Value);
    }

    #region Lifecycle
    private void TabbedHostWindow_Opened(object? sender, EventArgs e)
    {
        _hostHwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (_hostHwnd == IntPtr.Zero)
            _hostHwnd = new IntPtr(Environment.ProcessId);

        // F11 is grabbed by the host while an embedded Mugetsu child owns focus.
        // Toggle the host window here; the child process must not be fullscreened.
        HotKeys.ToggleFullscreenRequested = () => Dispatcher.UIThread.Post(() => ToggleFullScreen());
        HotKeys.ToggleGameFullScreenRequested = () => Dispatcher.UIThread.Post(ToggleGameOnly);

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
                    PostChildMessage(childHwnd, SkuaIpcMessages.OpenBank, IntPtr.Zero, IntPtr.Zero);
                    return;
                }

                int actionId = HotKeys.GetActionId(actionName);
                if (actionId >= 0)
                {
                    PostChildMessage(childHwnd, SkuaIpcMessages.ExecuteHotkey, new IntPtr(actionId), IntPtr.Zero);
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
                    PostChildMessage(info.ChildHwnd, SkuaIpcMessages.ReloadHotkeys, IntPtr.Zero, IntPtr.Zero);
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



    private WindowState _stateBeforeFullScreen = WindowState.Normal;
    private bool _gameOnly;
    private bool _gameOnlySetFullScreen;

    /// <summary>F11: the tab window goes borderless full screen, and back to what it was (it also ends game-only full screen).</summary>
    private void ToggleFullScreen(bool keepGameOnly = false)
    {
        bool wasGameOnly = _gameOnly && !keepGameOnly;
        if (wasGameOnly)
            _gameOnlySetFullScreen = false;

        if (WindowState == WindowState.FullScreen)
        {
            WindowState = (_stateBeforeFullScreen == WindowState.FullScreen || _stateBeforeFullScreen == WindowState.Minimized)
                ? WindowState.Normal
                : _stateBeforeFullScreen;
            if (wasGameOnly)
                ApplyGameOnly(false);
            return;
        }
        _stateBeforeFullScreen = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        WindowState = WindowState.FullScreen;
        if (wasGameOnly)
            ApplyGameOnly(false);
    }

    /// <summary>Shift+F11: full screen with only the game. The title bar, the tab bar, the notices and every client's menu are hidden;
    /// leaving it puts the window back as it was.</summary>
    private void ToggleGameOnly()
    {
        if (_gameOnly)
        {
            if (_gameOnlySetFullScreen && WindowState == WindowState.FullScreen)
                ToggleFullScreen(keepGameOnly: true);
            _gameOnlySetFullScreen = false;
            ApplyGameOnly(false);
            return;
        }
        _gameOnlySetFullScreen = WindowState != WindowState.FullScreen;
        if (_gameOnlySetFullScreen)
            ToggleFullScreen(keepGameOnly: true);
        ApplyGameOnly(true);
    }

    private void ApplyGameOnly(bool on)
    {
        _gameOnly = on;
        if (on)
        {
            RootGrid.RowDefinitions = new RowDefinitions("0,0,*");
            HostContainer.Margin = new Thickness(0);
        }
        else
        {
            RootGrid.RowDefinitions = new RowDefinitions("32,Auto,*");
            HostContainer.Margin = new Thickness(7, 0, 7, 0);
        }
        MainTitleBar.IsVisible = !on;
        TabBarBorder.IsVisible = !on;
        if (!on)
        {
            MainTitleBar.InvalidateMeasure();
            MainTitleBar.InvalidateVisual();
            RootGrid.InvalidateMeasure();
            RootGrid.InvalidateArrange();
        }

        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero && !info.IsDetached)
                PostChildMessage(info.ChildHwnd, (uint)SkuaIpcMessages.GameOnly, new IntPtr(on ? 1 : 0), IntPtr.Zero);

        _lastX = int.MinValue;
        _lastY = int.MinValue;
        _lastW = int.MinValue;
        _lastH = int.MinValue;
        _lastActiveHwnd = IntPtr.Zero;
        Dispatcher.UIThread.Post(DoReposition, DispatcherPriority.Background);
    }

    private void AttachChildWindow(TabInfo info, IntPtr childHwnd)
    {
        if (info.ChildHwnd != IntPtr.Zero) return;
        
        info.ChildHwnd = childHwnd;
        if (info.Process != null)
            HotKeys.RegisterSkuaPid((uint)info.Process.Id);

        PlatformReparentChild(childHwnd);

        if (_gameOnly)
            PostChildMessage(childHwnd, (uint)SkuaIpcMessages.GameOnly, new IntPtr(1), IntPtr.Zero);

        TabItem? selectedTab = InstancesTabControl.SelectedItem as TabItem;
        bool isSelected = (selectedTab != null && _tabs.TryGetValue(selectedTab, out var selInfo) && selInfo == info);

        if (_isGridViewEnabled)
        {
            info.IsThrottled = false;
            PostChildMessage(childHwnd, SkuaIpcMessages.GridView, new IntPtr(1), IntPtr.Zero);
            PostChildMessage(childHwnd, SkuaIpcMessages.Throttle, IntPtr.Zero, IntPtr.Zero);
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
            PostChildMessage(childHwnd, SkuaIpcMessages.Throttle, new IntPtr(1), IntPtr.Zero);
            PostChildMessage(childHwnd, (uint)SkuaIpcMessages.Focus, IntPtr.Zero, IntPtr.Zero);
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

    private static List<int> CollectDescendantPids(int rootPid)
    {
        List<int> result = new();
        HashSet<int> visited = new() { rootPid };
        Queue<int> queue = new();
        queue.Enqueue(rootPid);

        while (queue.Count > 0)
        {
            int pid = queue.Dequeue();
            string taskDir = $"/proc/{pid}/task";
            if (!Directory.Exists(taskDir))
                continue;

            try
            {
                foreach (string tidDir in Directory.EnumerateDirectories(taskDir))
                {
                    string childrenFile = Path.Combine(tidDir, "children");
                    if (!File.Exists(childrenFile))
                        continue;

                    string content = File.ReadAllText(childrenFile);
                    foreach (string token in content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(token, out int childPid) && childPid > 0 && visited.Add(childPid))
                        {
                            result.Add(childPid);
                            queue.Enqueue(childPid);
                        }
                    }
                }
            }
            catch { }
        }

        return result;
    }

    private static void KillProcessTree(Process? process)
    {
        if (process == null) return;
        int rootPid = 0;
        try { rootPid = process.Id; } catch { }
        if (rootPid <= 0) return;

        List<int> descendants = CollectDescendantPids(rootPid);

        try
        {
            try
            {
                using var pkill = Process.Start(new ProcessStartInfo
                {
                    FileName = "pkill",
                    Arguments = $"-9 -P {rootPid}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                pkill?.WaitForExit(100);
            }
            catch { }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(200);
            }
        }
        catch { }

        // Kill bottom-up so child helpers like Mugetsu cannot survive wrapper/parent termination.
        for (int i = descendants.Count - 1; i >= 0; i--)
        {
            int descPid = descendants[i];
            if (!Directory.Exists($"/proc/{descPid}"))
                continue;

            try
            {
                using Process descProc = Process.GetProcessById(descPid);
                if (!descProc.HasExited)
                {
                    descProc.Kill(entireProcessTree: true);
                    descProc.WaitForExit(100);
                }
            }
            catch { }
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        _isClosing = true;
        App.DisposeTrayIcon();
        try { _pipeCts.Cancel(); } catch { }
        try { _ghostWindow?.Close(); _ghostWindow = null; } catch { }

        PlatformClearAllViewports();

        var tabsToClose = _tabs.Values.ToList();
        _tabs.Clear();
        _latestClientLocations.Clear();
        _lastRecordedLocationByPid.Clear();

        foreach (var info in tabsToClose)
        {
            try
            {
                int childPid = info.Process?.Id ?? (int)info.ChildHwnd.ToInt64();
                KillProcessTree(info.Process);
                if (childPid > 0)
                    CleanUpChildResources(childPid);
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

        Task.Run(async () =>
        {
            await Task.Delay(300);
            Environment.Exit(0);
        });
    }
    #endregion

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

    #region Tab Switching & Positioning

    
    private bool _ignoreSelectionChange = false;

    public void EnterGridView()
    {
        int attachedCount = _tabs.Values.Count(t => !t.IsDetached);
        if (attachedCount <= 1 || _isTogglingGridView) return;

        _isTogglingGridView = true;
        try
        {
            _lastGridViewToggleTime = DateTime.UtcNow;
            _isGridViewEnabled = true;
            UpdateGridViewBorderColor();

            if (InstancesTabControl.SelectedItem is TabItem currentTab)
            {
                if (_tabs.TryGetValue(currentTab, out var cInfo) && !cInfo.IsDetached)
                    _lastSelectedTab = currentTab;
            }
            _lastSelectedTab ??= InstancesTabControl.Items.OfType<TabItem>().FirstOrDefault(t => _tabs.TryGetValue(t, out var i) && !i.IsDetached);

            _ignoreSelectionChange = true;
            InstancesTabControl.SelectedItem = null;
            foreach (var item in InstancesTabControl.Items.OfType<TabItem>())
            {
                if (_tabs.TryGetValue(item, out var tInfo) && !tInfo.IsDetached)
                    item.IsSelected = false;
            }
            _ignoreSelectionChange = false;

            DoReposition();

            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero && !info.IsDetached)
                {
                    info.IsThrottled = false;
                    PostChildMessage(info.ChildHwnd, SkuaIpcMessages.GridView, new IntPtr(1), IntPtr.Zero);
                    PostChildMessage(info.ChildHwnd, SkuaIpcMessages.Throttle, IntPtr.Zero, IntPtr.Zero);
                }
            }
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
            UpdateGridViewBorderColor();

            _ignoreSelectionChange = true;
            if (_lastSelectedTab != null && InstancesTabControl.Items.Contains(_lastSelectedTab))
            {
                InstancesTabControl.SelectedItem = _lastSelectedTab;
            }
            else
            {
                var firstTab = InstancesTabControl.Items.OfType<TabItem>().FirstOrDefault(t => _tabs.TryGetValue(t, out var i) && !i.IsDetached);
                if (firstTab != null)
                    InstancesTabControl.SelectedItem = firstTab;
            }
            _ignoreSelectionChange = false;

            DoReposition();

            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero && !info.IsDetached)
                {
                    PostChildMessage(info.ChildHwnd, SkuaIpcMessages.GridView, IntPtr.Zero, IntPtr.Zero);
                }
            }
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
        _lastGridViewState = false;
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
            int attachedCount = _tabs.Values.Count(t => !t.IsDetached);
            if (attachedCount <= 1)
            {
                // If 1 or 0 attached accounts in host, Grid View has nothing to tile
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
            if (InstancesTabControl.SelectedItem is TabItem clickedTab && _tabs.TryGetValue(clickedTab, out var cInfo))
            {
                if (cInfo.IsDetached)
                {
                    if (cInfo.ChildHwnd != IntPtr.Zero)
                    {
                        PlatformRaiseChild(cInfo.ChildHwnd);
                    }
                    _ignoreSelectionChange = true;
                    InstancesTabControl.SelectedItem = null;
                    _ignoreSelectionChange = false;
                    return;
                }
            }

            // ExitGridView restores _lastSelectedTab, so point it at the tab that was just clicked.
            if (InstancesTabControl.SelectedItem is TabItem attachedTab)
                _lastSelectedTab = attachedTab;
            ExitGridView();
            return;
        }

        if (InstancesTabControl.SelectedItem is TabItem selTab && _tabs.TryGetValue(selTab, out var tabInfo))
        {
            if (tabInfo.IsDetached && tabInfo.ChildHwnd != IntPtr.Zero)
            {
                PlatformRaiseChild(tabInfo.ChildHwnd);
            }
        }

        DoReposition();
        UpdateTabScrollButtons();
        if (OperatingSystem.IsLinux())
            SendActiveChildCommand("GET_AUTO_STATE");
        if (InstancesTabControl.SelectedItem is TabItem scrollTab)
        {
            ScrollTabIntoView(scrollTab);
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
            case "StreamerMode":
                optionId = 9;
                val = options.StreamerMode;
                Dispatcher.UIThread.Post(RefreshAllTabTitles);
                break;
            case "AggroMonsters": optionId = 10; val = options.AggroMonsters; break;
        }
        
        if (optionId != -1)
        {
            foreach (var info in _tabs.Values)
                if (info.ChildHwnd != IntPtr.Zero)
                    PostChildMessage(info.ChildHwnd, SkuaIpcMessages.SetOption, new IntPtr(optionId), new IntPtr(val ? 1 : 0));
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

            if (!_isGridViewEnabled && selectedTab == null)
            {
                PlatformClearAllViewports();
                return;
            }

            if (WindowState == WindowState.Minimized || !IsVisible)
            {
                foreach (var info in _tabs.Values)
                {
                    if (info.ChildHwnd != IntPtr.Zero && !info.IsDetached)
                    {
                        PlatformHideChild(info.ChildHwnd);
                        if (!info.IsLoaded || info.LastVisibility != 4)
                        {
                            info.LastVisibility = 4;
                            PostChildMessage(info.ChildHwnd, SkuaIpcMessages.Visibility, new IntPtr(4), IntPtr.Zero);
                        }
                    }
                }
                return;
            }

            Point? hostPos = HostContainer.TranslatePoint(new Point(0, 0), this);
            int x = _gameOnly ? 0 : (int)(hostPos?.X ?? 0);
            int y = _gameOnly ? 0 : Math.Max((int)(hostPos?.Y ?? 68), 68);
            int w = (int)Math.Max(_gameOnly ? Bounds.Width : (HostContainer.Bounds.Width > 1 ? HostContainer.Bounds.Width : Bounds.Width), 1);
            int h = (int)Math.Max(_gameOnly ? Bounds.Height : (HostContainer.Bounds.Height > 1 ? HostContainer.Bounds.Height : Bounds.Height - y), 1);

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
                .Where(v => v.ChildHwnd != IntPtr.Zero && !v.IsDetached)
                .ToList();

            PublishChildVisibility(selectedTab, activeWindows);
            
            bool isLoading = false;
            if (_isGridViewEnabled)
            {
                if (DetachedPlaceholder != null) DetachedPlaceholder.IsVisible = false;
                isLoading = _tabs.Values.Where(v => !v.IsDetached).Any(v => v.ChildHwnd == IntPtr.Zero || !v.IsLoaded);
            }
            else
            {
                if (selectedTab != null && _tabs.TryGetValue(selectedTab, out TabInfo? activeInfo))
                {
                    if (DetachedPlaceholder != null)
                    {
                        DetachedPlaceholder.IsVisible = activeInfo.IsDetached;
                        if (activeInfo.IsDetached && DetachedTabNameText != null)
                        {
                            DetachedTabNameText.Text = $"{GetTabTitle(selectedTab)} is running in Window Mode";
                        }
                    }

                    if (!activeInfo.IsDetached)
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
                }
                else
                {
                    if (DetachedPlaceholder != null) DetachedPlaceholder.IsVisible = false;
                    isLoading = true;
                }
            }
            if (LoadingIndicator != null)
            {
                LoadingIndicator.IsVisible = isLoading;
                if (TabLoadingBar != null)
                    TabLoadingBar.IsIndeterminate = isLoading;
            }

            if (activeWindows.Count == 0 && (_isGridViewEnabled || (selectedTab != null && _tabs.TryGetValue(selectedTab, out var sInfo) && !sInfo.IsDetached))) return;

            if (_isGridViewEnabled)
            {
                int n = activeWindows.Count;
                if (x == _lastX && y == _lastY && w == _lastW && h == _lastH &&
                    _lastGridViewState && n == _lastTabCount)
                {
                    return;
                }

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
                        PostChildMessage(activeWindows[i].ChildHwnd, SkuaIpcMessages.Throttle, new IntPtr(0), IntPtr.Zero);
                    }
                    PostChildMessage(activeWindows[i].ChildHwnd, (uint)SkuaIpcMessages.Focus, new IntPtr(IsActive ? 1 : 0), IntPtr.Zero);
                }
                _lastX = x;
                _lastY = y;
                _lastW = w;
                _lastH = h;
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
                    if (!activeInfo.IsDetached)
                    {
                        activeChildHwnd = activeInfo.ChildHwnd;
                    }
                }
                HotKeys.ActiveChildHwnd = activeInfo?.ChildHwnd ?? IntPtr.Zero;

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
                            PostChildMessage(activeChildHwnd, SkuaIpcMessages.Throttle, new IntPtr(0), IntPtr.Zero);
                        }
                        PostChildMessage(activeChildHwnd, (uint)SkuaIpcMessages.Focus, new IntPtr(IsActive ? 1 : 0), IntPtr.Zero);
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
                    if (info.ChildHwnd == IntPtr.Zero || info.IsDetached) continue;

                    if (!info.IsThrottled)
                    {
                        info.IsThrottled = true;
                        PostChildMessage(info.ChildHwnd, SkuaIpcMessages.Throttle, new IntPtr(1), IntPtr.Zero);
                    }
                    PostChildMessage(info.ChildHwnd, (uint)SkuaIpcMessages.Focus, IntPtr.Zero, IntPtr.Zero);

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
                bool isSelectedTile = i == selectedGridIndex;
                visibility = isSelectedTile ? 2 : 3;
            }
            else
            {
                visibility = selectedTab != null && _tabs.TryGetValue(selectedTab, out TabInfo? activeInfo) && ReferenceEquals(info, activeInfo) ? 0 : 1;
            }
            if (!info.IsLoaded || info.LastVisibility != visibility)
            {
                info.LastVisibility = visibility;
                PostChildMessage(info.ChildHwnd, SkuaIpcMessages.Visibility, new IntPtr(visibility), IntPtr.Zero);
            }
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
    public void DetachTab(TabItem tab, PixelPoint? targetScreenPos = null)
    {
        if (!_tabs.TryGetValue(tab, out TabInfo? info) || info.IsDetached)
            return;

        info.IsDetached = true;
        info.TabItem = tab;

        int clientWidth = Math.Max(400, (int)Math.Round(Bounds.Width > 0 ? Bounds.Width : 948));
        int clientHeight = Math.Max(300, (int)Math.Round(Bounds.Height > 0 ? Bounds.Height : 600));
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        int screenX = targetScreenPos?.X ?? (screen != null
            ? screen.WorkingArea.X + Math.Max(0, (screen.WorkingArea.Width - clientWidth) / 2)
            : this.Position.X);
        int screenY = targetScreenPos?.Y ?? (screen != null
            ? screen.WorkingArea.Y + Math.Max(0, (screen.WorkingArea.Height - clientHeight) / 2)
            : this.Position.Y);

        info.DetachedScreenX = screenX;
        info.DetachedScreenY = screenY;

        if (info.DetachedIcon != null)
        {
            info.DetachedIcon.IsVisible = true;
        }
        if (!tab.Classes.Contains("tab-detached"))
        {
            tab.Classes.Add("tab-detached");
        }

        // Keep the tab in InstancesTabControl!
        InstancesTabControl.SelectedItem = tab;
        UpdateTabScrollButtons();

        // Detach/reattach uses IPC; Wayland windows cannot be reparented across processes.
        if (info.ChildHwnd != IntPtr.Zero)
        {
            PlatformDetachChild(info.ChildHwnd, screenX, screenY, clientWidth, clientHeight);
            PostChildMessage(info.ChildHwnd, SkuaIpcMessages.Detach, new IntPtr(screenX), new IntPtr(screenY));
        }

        DoReposition();
    }

    public void DockChildInstance(int childPid)
    {
        var entry = _tabs.FirstOrDefault(kvp => kvp.Value.Process != null && kvp.Value.Process.Id == childPid);
        if (entry.Key == null || entry.Value == null)
            return;

        TabItem tab = entry.Key;
        TabInfo info = entry.Value;

        if (!info.IsDetached)
            return;

        info.IsDetached = false;
        info.WasDetachedBeforeGrid = false;

        if (info.DetachedIcon != null)
        {
            info.DetachedIcon.IsVisible = false;
        }
        tab.Classes.Remove("tab-detached");

        if (info.ChildHwnd != IntPtr.Zero)
        {
            PlatformReattachChild(info.ChildHwnd);
            PostChildMessage(info.ChildHwnd, SkuaIpcMessages.Dock, IntPtr.Zero, IntPtr.Zero);
        }

        InstancesTabControl.SelectedItem = tab;
        UpdateTabScrollButtons();
        DoReposition();
    }

    private void FocusDetachedButton_Click(object? sender, RoutedEventArgs e)
    {
        if (InstancesTabControl.SelectedItem is TabItem selTab && _tabs.TryGetValue(selTab, out var info))
        {
            if (info.IsDetached && info.ChildHwnd != IntPtr.Zero)
            {
                PlatformRaiseChild(info.ChildHwnd);
            }
        }
    }

    private void DockDetachedButton_Click(object? sender, RoutedEventArgs e)
    {
        if (InstancesTabControl.SelectedItem is TabItem selTab && _tabs.TryGetValue(selTab, out var info))
        {
            if (info.IsDetached && info.Process != null)
            {
                DockChildInstance(info.Process.Id);
            }
        }
    }

    public void DockAllInstances()
    {
        var detached = _tabs.Where(kvp => kvp.Value.IsDetached).ToList();
        foreach (var kvp in detached)
        {
            DockChildInstance(kvp.Value.Process.Id);
        }
    }

    private void MenuItem_DockAllWindows_Click(object? sender, RoutedEventArgs e)
    {
        DockAllInstances();
    }

    private void CloseTab(TabItem tab)
    {
        if (!_tabs.ContainsKey(tab) && !InstancesTabControl.Items.Contains(tab))
            return;

        if (_tabs.TryGetValue(tab, out TabInfo? info))
        {
            _tabs.Remove(tab);

            int childPid = 0;
            try { childPid = info.Process?.Id ?? 0; } catch { }
            if (childPid <= 0 && info.ChildHwnd != IntPtr.Zero)
                childPid = (int)info.ChildHwnd.ToInt64();

            IntPtr unmapHandle = info.ChildHwnd != IntPtr.Zero
                ? info.ChildHwnd
                : (childPid > 0 ? new IntPtr(childPid) : IntPtr.Zero);
            if (unmapHandle != IntPtr.Zero)
                PlatformUnmapChild(unmapHandle);

            if (childPid > 0)
            {
                _latestClientLocations.Remove(childPid);
                _lastRecordedLocationByPid.Remove(childPid);
            }

            Process? procToKill = info.Process;
            Task.Run(() =>
            {
                try
                {
                    KillProcessTree(procToKill);
                    if (childPid > 0)
                        CleanUpChildResources(childPid);
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
            PlatformClearAllViewports();

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
        else
            DoReposition();
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
        var usedIds = _tabs.Values.Select(t => t.ClientNumber).ToHashSet();
        while (usedIds.Contains(availableId))
            availableId++;

        string userArg = string.Empty;
        for (int i = 0; i < clientArgs.Length; i++)
        {
            if ((clientArgs[i] == "-u" || clientArgs[i] == "--user") && i + 1 < clientArgs.Length)
            {
                if (!string.IsNullOrWhiteSpace(clientArgs[i + 1]))
                    userArg = clientArgs[i + 1];
                break;
            }
        }

        string tabName = ShouldShowCharNameOnTabs() && !string.IsNullOrWhiteSpace(userArg)
            ? userArg
            : $"Tab {availableId}";

        #region Tab Header UI
        StackPanel headerPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
        
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

        MaterialIcon detachedIcon = new MaterialIcon
        {
            Kind = MaterialIconKind.DockWindow,
            Width = 12,
            Height = 12,
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };
        detachedIcon[!MaterialIcon.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaAccentBrush");
        ToolTip.SetTip(detachedIcon, "Window Mode: click to focus, double-click to dock into tabs");

        Grid iconHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
        iconHost.Children.Add(tabIcon);
        iconHost.Children.Add(crown);
        headerPanel.Children.Add(iconHost);
        headerPanel.Children.Add(detachedIcon);
        headerPanel.Children.Add(editTitle);
        headerPanel.Children.Add(title);
        headerPanel.Children.Add(closeBtn);

        closeBtn.PointerEntered += (s, ev) => { closeTxt.Foreground = Brushes.White; closeBtn.Background = new SolidColorBrush(Color.Parse("#EF4444")); };
        closeBtn.PointerExited += (s, ev) => { closeTxt[!TextBlock.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaMutedTextBrush"); closeBtn.Background = Brushes.Transparent; };
        
        TabInfo info = new TabInfo
        {
            ClientNumber = availableId,
            LastCharacterName = userArg,
            AccountOrderKey = AccountKey(clientArgs ?? Array.Empty<string>()),
            HeaderPanel = headerPanel,
            Crown = crown,
            DetachedIcon = detachedIcon
        };
        
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
        info.TabItem = newTab;

        headerPanel.DoubleTapped += (s, ev) =>
        {
            if (_tabs.TryGetValue(newTab, out var tInfo) && tInfo.IsDetached && tInfo.Process != null)
            {
                DockChildInstance(tInfo.Process.Id);
                ev.Handled = true;
            }
        };
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

            var (wlHandle, wlSocket) = Skua.App.Avalonia.Controls.GameHost.CreateWaylandHostForChild(1280, 720);
            if (!string.IsNullOrEmpty(wlSocket))
                p.StartInfo.EnvironmentVariables["BONXIE_WL_DISPLAY"] = wlSocket;
            else
                p.StartInfo.EnvironmentVariables.Remove("BONXIE_WL_DISPLAY");

            try
            {
                p.Start();
            }
            catch (Exception ex)
            {
                Skua.App.Avalonia.Controls.GameHost.ReleasePendingWaylandHost(wlHandle);
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
            Skua.App.Avalonia.Controls.GameHost.RegisterPendingWaylandHost(p.Id, wlHandle, wlSocket);
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

        MenuItem toggleWindowItem = new MenuItem
        {
            Header = "Detach to Window",
            Icon = new MaterialIcon { Kind = MaterialIconKind.DockWindow, Width = 14, Height = 14 }
        };
        toggleWindowItem.Click += (s, ev) =>
        {
            if (_tabs.TryGetValue(newTab, out var tInfo))
            {
                if (tInfo.IsDetached && tInfo.Process != null)
                    DockChildInstance(tInfo.Process.Id);
                else
                    DetachTab(newTab);
            }
        };
        ctx.Opened += (s, ev) =>
        {
            bool isDet = _tabs.TryGetValue(newTab, out var tInfo) && tInfo.IsDetached;
            toggleWindowItem.Header = isDet ? "Dock to Tabs" : "Detach to Window";
        };
        ctx.Items.Add(toggleWindowItem);
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

        newTab.AddHandler(InputElement.PointerPressedEvent, (s, ev) =>
        {
            var prop = ev.GetCurrentPoint(newTab).Properties;
            if (prop.IsRightButtonPressed)
            {
                InstancesTabControl.SelectedItem = newTab;
                return;
            }

            if (!prop.IsLeftButtonPressed)
                return;

            if (ev.Source is Visual hit)
            {
                Visual? cur = hit;
                while (cur != null && cur != newTab)
                {
                    if (cur == closeBtn || cur == editTitle)
                        return;
                    cur = cur.GetVisualParent();
                }
            }

            _draggedTab = newTab;
            _dragStartPos = ev.GetPosition(this);
            _isDraggingTab = false;
            ev.Pointer.Capture(this);
            InstancesTabControl.SelectedItem = newTab;

            if (_isGridViewEnabled)
            {
                if (info.IsDetached)
                {
                    if (info.ChildHwnd != IntPtr.Zero)
                    {
                        PlatformRaiseChild(info.ChildHwnd);
                    }
                    return;
                }

                _isGridViewEnabled = false;
                UpdateGridViewBorderColor();
                foreach (var tInfo in _tabs.Values)
                {
                    if (tInfo.ChildHwnd != IntPtr.Zero && !tInfo.IsDetached)
                    {
                        PostChildMessage(tInfo.ChildHwnd, SkuaIpcMessages.GridView, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                DoReposition();
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
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
