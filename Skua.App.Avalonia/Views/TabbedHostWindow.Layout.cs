using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.DependencyInjection;
using Material.Icons;
using Material.Icons.Avalonia;
using Skua.App.Avalonia.Services;
using Skua.Core.AppStartup;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Skua.App.Avalonia.Views;

public partial class TabbedHostWindow
{
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

    private static readonly (int W, int H)[] GameSizes = { (480, 275), (960, 550), (1885, 1080) };
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
        if (_gameSize is { } t)
        {
            string scale = t.W switch
            {
                480 => "0.5x",
                960 => "1x",
                1885 => "1.96x (1080p)",
                _ => $"{t.W / 960.0:0.##}x"
            };
            ToolTip.SetTip(GameSizeBorder, $"Game size: {scale} ({t.W} x {t.H}). Click for the next size.");
        }
        else
        {
            ToolTip.SetTip(GameSizeBorder, "Game size: Auto (fills window). Click to play at a fixed size (0.5x, 1x, 1080p).");
        }
    }

    private void GameSizeBorder_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        int next = _gameSize is { } s ? Array.IndexOf(GameSizes, s) + 1 : 0;
        _gameSize = next < GameSizes.Length ? GameSizes[next] : null;
        Ioc.Default.GetService<ISettingsService>()?.Set("BonxieMuxGameSize", _gameSize is { } g ? $"{g.W}x{g.H}" : "auto");
        UpdateGameSizeButton();
        _lastX = int.MinValue;
        DoReposition();
    }

    private void UpdateGridLayoutButton()
    {
        (string _, string tip) = _gridLayout switch
        {
            "vertical" => ("Stacked", "Grid View layout: stacked top to bottom. Click for side by side."),
            "horizontal" => ("Side by side", "Grid View layout: side by side. Click for automatic."),
            _ => ("Auto", "Grid View layout: automatic grid. Click to stack the accounts top to bottom."),
        };
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
            GridViewBorder.Background = Brushes.Transparent;
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

    private void UpdateTabFocusDots()
    {
        TabItem? selected = InstancesTabControl.SelectedItem as TabItem;
        foreach (KeyValuePair<TabItem, TabInfo> kvp in _tabs)
        {
            if (kvp.Value.FocusDot != null)
                kvp.Value.FocusDot.IsVisible = !_isGridViewEnabled && ReferenceEquals(kvp.Key, selected);
        }
    }

    private void InstancesTabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source != InstancesTabControl || _ignoreSelectionChange) return;

        UpdateTabFocusDots();
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

    private bool _isArmyFlyoutOpen;
    private bool _armyFlyoutHooked;

    private void GlobeBorder_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (global::Avalonia.Controls.Primitives.FlyoutBase.GetAttachedFlyout(GlobeBorder) is { } flyout)
        {
            if (!_armyFlyoutHooked)
            {
                _armyFlyoutHooked = true;
                flyout.Opened += (_, _) =>
                {
                    _isArmyFlyoutOpen = true;
                    GlobeBorder[!Border.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaNavHoverBrush");
                };
                flyout.Closed += (_, _) =>
                {
                    _isArmyFlyoutOpen = false;
                    if (!GlobeBorder.IsPointerOver)
                        GlobeBorder.Background = Brushes.Transparent;
                };
            }
            flyout.ShowAt(GlobeBorder);
        }
    }

    private void GlobeBorder_PointerEntered(object? sender, PointerEventArgs e)
    {
        GlobeBorder[!Border.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaNavHoverBrush");
    }

    private void GlobeBorder_PointerExited(object? sender, PointerEventArgs e)
    {
        if (!_isArmyFlyoutOpen)
            GlobeBorder.Background = Brushes.Transparent;
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
        UpdateTabFocusDots();
        if (GridLayoutBorder != null)
            GridLayoutBorder.IsVisible = _isGridViewEnabled;
        if (GameSizeBorder != null)
            GameSizeBorder.IsVisible = !_isGridViewEnabled;
        if (_isGridViewEnabled)
        {
            GridViewBorder[!Border.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaNavHoverBrush");
            if (GridViewActiveBar != null) GridViewActiveBar.IsVisible = true;
            if (GridViewIcon != null) GridViewIcon[!MaterialIcon.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaAccentBrush");
        }
        else
        {
            GridViewBorder.Background = Brushes.Transparent;
            if (GridViewActiveBar != null) GridViewActiveBar.IsVisible = false;
            if (GridViewIcon != null) GridViewIcon[!MaterialIcon.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaMutedTextBrush");
        }
    }

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

    public void ShowEmbeddedClients()
    {
        _lastX = int.MinValue;
        _lastY = int.MinValue;
        _lastW = int.MinValue;
        _lastH = int.MinValue;
        _lastActiveHwnd = IntPtr.Zero;
        ScheduleReposition();
    }

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
}
