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
        public StackPanel? HeaderPanel { get; set; }
        public MaterialIcon? Crown { get; set; }
        public Border? FocusDot { get; set; }
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
        Skua.Core.AppStartup.HotKeys.OpenQuickLaunchRequested = OpenQuickLaunchFromHotkey;

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
        LoadGameSizeSetting(Ioc.Default.GetService<ISettingsService>()?.Get("BonxieMuxGameSize", "auto"));
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
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Skua.Shared.Avalonia.Theming.UiTokens.Bind(_titleText, TextBlock.ForegroundProperty, Skua.Shared.Avalonia.Theming.UiTokens.SkuaTextBrush);
            Skua.Shared.Avalonia.Theming.UiTokens.Bind(_titleText, TextBlock.FontSizeProperty, Skua.Shared.Avalonia.Theming.UiTokens.UiFontBody);

            var titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0),
                Children = { _titleText }
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
                        [!MaterialIcon.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(Skua.Shared.Avalonia.Theming.UiTokens.SkuaAccentBrush)
                    }
                }
            };

            _titleBorder = new Border
            {
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Child = titleRow
            };
            Skua.Shared.Avalonia.Theming.UiTokens.Bind(_titleBorder, Border.BackgroundProperty, Skua.Shared.Avalonia.Theming.UiTokens.SkuaSurfaceBrush);

            _rootBorder = new Border
            {
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
            Skua.Shared.Avalonia.Theming.UiTokens.Bind(_rootBorder, Border.BackgroundProperty, Skua.Shared.Avalonia.Theming.UiTokens.SkuaPageBrush);
            Skua.Shared.Avalonia.Theming.UiTokens.Bind(_rootBorder, Border.BorderBrushProperty, Skua.Shared.Avalonia.Theming.UiTokens.SkuaAccentBrush);
            Skua.Shared.Avalonia.Theming.UiTokens.Bind(_rootBorder, Border.BorderThicknessProperty, Skua.Shared.Avalonia.Theming.UiTokens.UiFocusBorderThickness);
            Skua.Shared.Avalonia.Theming.UiTokens.Bind(_rootBorder, Border.CornerRadiusProperty, Skua.Shared.Avalonia.Theming.UiTokens.UiRadiusCard);

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
            Skua.Shared.Avalonia.Theming.UiTokens.Bind(sp, Panel.BackgroundProperty, Skua.Shared.Avalonia.Theming.UiTokens.SkuaSelectionBrush);
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
            RootGrid.RowDefinitions = new RowDefinitions("0,*");
            HostContainer.Margin = new Thickness(0);
        }
        else
        {
            RootGrid.RowDefinitions = new RowDefinitions("30,*");
            HostContainer.Margin = new Thickness(0);
        }
        MainTitleBar.IsVisible = !on;
        ActivityBarBorder.IsVisible = !on;
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
            _lastActiveHwnd = IntPtr.Zero;
            DoReposition();
        }
        else
        {
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
}
