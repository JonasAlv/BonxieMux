using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using Newtonsoft.Json.Linq;
using Skua.Shared.Avalonia.Controls.Shell;
using Skua.Shared.Avalonia.Services;
using Skua.Shared.Avalonia.Theming;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Skua.App.Avalonia.Controls;
using Skua.App.Avalonia.Services;
using Skua.Core.AppStartup;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;
using Skua.Core.Models;
using CommunityToolkit.Mvvm.DependencyInjection;

namespace Skua.App.Avalonia.Views;

public partial class TabbedHostWindow : Window
{
    private readonly Dictionary<IntPtr, GameHost> _viewports = new();
    private readonly Dictionary<IntPtr, Border> _headlessOverlays = new();
    private readonly Dictionary<IntPtr, bool> _manualHeadlessStates = new();
    private readonly Dictionary<IntPtr, bool> _autoHeadlessStates = new();
    private readonly Dictionary<IntPtr, long> _headlessSeq = new();
    private Grid? _hostGrid;
    private Canvas? _viewportCanvas;
    private bool _hostInitialized;

    private void StartGridFocusTracking()
    {
        EnsureHostLayout();
    }

    private void EnsureHostLayout()
    {
        if (_hostInitialized)
            return;
        _hostInitialized = true;

        _viewportCanvas = new Canvas
        {
            ClipToBounds = true,
            Background = Brushes.Transparent
        };

        MainTitleBar.LeftContent = BuildTopMenuBar();
        BuildActivityBarControls();

        _hostGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("*")
        };
        Grid.SetRow(_viewportCanvas, 0);
        _hostGrid.Children.Add(_viewportCanvas);

        HostContainer.Child = _hostGrid;
        HostContainer.SizeChanged += (_, _) => ScheduleReposition();

        AddHandler(InputElement.KeyDownEvent, OnHostKeyDown, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyUpEvent, OnHostKeyUp, RoutingStrategies.Tunnel);

        Task.Run(SweepStaleProcessFiles);
        UpdateStatusBar();
    }

    private void OnHostKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_mainQuickBar.AutoToggle?.IsChecked == true)
            {
                _mainQuickBar.AutoToggle.IsChecked = false;
                e.Handled = true;
                return;
            }
            if (_mainQuickBar.JumpToggle?.IsChecked == true)
            {
                _mainQuickBar.JumpToggle.IsChecked = false;
                e.Handled = true;
                return;
            }
            if (AccountLaunchToggle?.IsChecked == true)
            {
                AccountLaunchToggle.IsChecked = false;
                e.Handled = true;
                return;
            }
        }
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            int tabIndex = e.Key switch
            {
                >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
                >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1,
                _ => -1
            };
            if (tabIndex >= 0 && tabIndex < InstancesTabControl.ItemCount)
            {
                InstancesTabControl.SelectedIndex = tabIndex;
                e.Handled = true;
                return;
            }

            if (e.Key == Key.K && (_tabs.Count == 0 || HotKeys.ActiveChildHwnd == IntPtr.Zero))
            {
                OpenHostCommandPalette();
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            int count = InstancesTabControl.ItemCount;
            if (count > 1)
            {
                int current = InstancesTabControl.SelectedIndex < 0 ? 0 : InstancesTabControl.SelectedIndex;
                int delta = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
                InstancesTabControl.SelectedIndex = (current + delta + count) % count;
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Escape && (_gameOnly || WindowState == WindowState.FullScreen))
            return;
        MainWindow.ForwardKeyEvent(true, e);
    }

    private void OpenHostCommandPalette()
    {
        if (QuickPickWindow.DismissActive())
            return;

        Dictionary<string, string> hotkeys = CommandPaletteWindow.LoadHotKeyMap();
        string quickLaunchKey = hotkeys.GetValueOrDefault("OpenQuickLaunch", "Ctrl+Shift+L");
        string fullscreenKey = hotkeys.GetValueOrDefault("ToggleFullscreen", "F11");

        var items = new List<QuickPickItem>
        {
            new("Workspace", "Quick Launch Account", null, OpenQuickLaunchFromHotkey, quickLaunchKey),
            new("View", "Toggle Grid View", _isGridViewEnabled ? "Active" : null, () => { if (_isGridViewEnabled) ExitGridView(); else EnterGridView(); }),
            new("View", "Toggle Fullscreen", WindowState == WindowState.FullScreen ? "Active" : null, () => ToggleFullScreen(), fullscreenKey),
        };

        if (Ioc.Default.GetService<IThemeService>() is AvaloniaThemeService themeService)
            QuickPickWindow.AddThemePreferenceItems(items, themeService, this);

        new QuickPickWindow(items, "Command palette", "Type a host command or preference…").ShowModeless(this);
    }

    private void OnHostKeyUp(object? sender, KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;
        MainWindow.ForwardKeyEvent(false, e);
    }

    #region Platform Window Operations
    private void PostChildMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var tab = _tabs.Values.FirstOrDefault(t => t.ChildHwnd == hWnd);
        if (tab?.Process != null && !tab.Process.HasExited)
        {
            TabIpc.SendMessage(tab.Process.Id, msg, wParam, lParam);
        }
    }

    private void PlatformReparentChild(IntPtr childHwnd)
    {
        EnsureHostLayout();
        if (_viewportCanvas == null || _viewports.ContainsKey(childHwnd))
            return;

        TabInfo? tab = _tabs.Values.FirstOrDefault(t => t.ChildHwnd == childHwnd);
        int childPid = tab?.Process?.Id ?? (int)childHwnd.ToInt64();
        if (childPid <= 0)
            return;

        GameHost viewport = new(childPid)
        {
            IsVisible = false
        };
        viewport.AddHandler(InputElement.PointerPressedEvent, (_, _) =>
        {
            HotKeys.ActiveChildHwnd = childHwnd;
            viewport.SetActiveInstance();
            if (_isGridViewEnabled)
            {
                KeyValuePair<TabItem, TabInfo> match = _tabs.FirstOrDefault(kvp => kvp.Value.ChildHwnd == childHwnd);
                if (match.Key != null)
                {
                    _lastSelectedTab = match.Key;
                    List<TabInfo> activeGrid = InstancesTabControl.Items.OfType<TabItem>()
                        .Where(t => _tabs.ContainsKey(t))
                        .Select(t => _tabs[t])
                        .Where(v => v.ChildHwnd != IntPtr.Zero && !v.IsDetached)
                        .ToList();
                    PublishChildVisibility(match.Key, activeGrid);
                }
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        _viewports[childHwnd] = viewport;
        _viewportCanvas.Children.Add(viewport);

        Border headlessOverlay = CreateHeadlessOverlay();
        _headlessOverlays[childHwnd] = headlessOverlay;
        _viewportCanvas.Children.Add(headlessOverlay);
    }

    private static Border CreateHeadlessOverlay()
    {
        var icon = new MaterialIcon
        {
            Kind = MaterialIconKind.MonitorOff,
            Width = 36,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, UiTokens.SkuaMutedTextBrush);

        var title = new TextBlock
        {
            Text = "Headless Mode Active",
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        UiTokens.Bind(title, TextBlock.FontSizeProperty, UiTokens.UiFontDisplay);
        UiTokens.Bind(title, TextBlock.ForegroundProperty, UiTokens.SkuaTextBrush);

        var subtitle = new TextBlock
        {
            Text = "Viewport suspended to minimize CPU and GPU usage",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        UiTokens.Bind(subtitle, TextBlock.FontSizeProperty, UiTokens.UiFontBody);
        UiTokens.Bind(subtitle, TextBlock.ForegroundProperty, UiTokens.SkuaMutedTextBrush);

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = UiTokens.GetDouble(UiTokens.UiGapMd, 8)
        };
        stack.Children.Add(icon);
        stack.Children.Add(title);
        stack.Children.Add(subtitle);

        var overlay = new Border
        {
            Child = stack,
            IsVisible = false,
            IsHitTestVisible = true
        };
        UiTokens.Bind(overlay, Border.BackgroundProperty, UiTokens.SkuaPageBrush);
        return overlay;
    }

    private void ApplyChildHeadlessState(int childPid, bool manual, bool auto, long seq)
    {
        TabInfo? tab = _tabs.Values.FirstOrDefault(t => t.Process != null && t.Process.Id == childPid);
        if (tab == null || tab.ChildHwnd == IntPtr.Zero)
            return;

        IntPtr childHwnd = tab.ChildHwnd;
        if (_headlessSeq.TryGetValue(childHwnd, out long prevSeq) && seq < prevSeq)
            return;

        _headlessSeq[childHwnd] = seq;
        _manualHeadlessStates[childHwnd] = manual;
        _autoHeadlessStates[childHwnd] = auto;

        if (tab.IsDetached)
        {
            if (_viewports.TryGetValue(childHwnd, out GameHost? detVp) &&
                _headlessOverlays.TryGetValue(childHwnd, out Border? detOverlay))
            {
                bool isHeadless = PredictEffectiveHeadless(childHwnd, false, isShownTile: true);
                detVp.IsVisible = !isHeadless;
                detOverlay.IsVisible = isHeadless;
                if (!isHeadless)
                    detVp.ForceRefreshFrame();
            }
            UpdateStatusBar();
            return;
        }

        if (_viewports.TryGetValue(childHwnd, out GameHost? viewport) &&
            _headlessOverlays.TryGetValue(childHwnd, out Border? overlay))
        {
            bool shownInHost = viewport.IsVisible || overlay.IsVisible;
            if (shownInHost)
            {
                bool isHeadless = PredictEffectiveHeadless(childHwnd, _isGridViewEnabled, isShownTile: true);
                viewport.IsVisible = !isHeadless;
                overlay.IsVisible = isHeadless;
                if (!isHeadless)
                    viewport.ForceRefreshFrame();
            }
        }
        UpdateStatusBar();
    }

    private bool PredictEffectiveHeadless(IntPtr childHwnd, bool isGrid, bool isShownTile)
    {
        bool manual = _manualHeadlessStates.TryGetValue(childHwnd, out bool m) && m;
        bool auto = false;
        try
        {
            IScriptOption options = Ioc.Default.GetRequiredService<IScriptOption>();
            if (options.AutoHeadless)
            {
                bool isWindowFocused = IsActive && WindowState != WindowState.Minimized && IsVisible;
                bool isFocusedGridTile = isWindowFocused && isGrid;
                bool isFocusedTab = isWindowFocused && (isGrid ? childHwnd == HotKeys.ActiveChildHwnd : isShownTile);
                bool excluded = (options.AutoHeadlessExcludeFocused && isFocusedTab)
                    || (options.AutoHeadlessExcludeGridView && isFocusedGridTile);
                auto = !excluded;
            }
        }
        catch { }
        _autoHeadlessStates[childHwnd] = auto;
        return manual || auto;
    }

    private void ToggleAutoHeadless()
    {
        try
        {
            IScriptOption hostOpts = Ioc.Default.GetRequiredService<IScriptOption>();
            bool next = !hostOpts.AutoHeadless;
            hostOpts.AutoHeadless = next;
            string val = next ? "True" : "False";
            foreach (TabInfo tab in _tabs.Values)
            {
                if (tab.Process is { HasExited: false } proc)
                    Skua.Core.Services.OptionSharing.Send(proc.Id, new Skua.Core.Services.SharedOptionCommand("set", nameof(IScriptOption.AutoHeadless), val));
            }
            RefreshHeadlessOverlays();
        }
        catch { }
    }

    private void RefreshHeadlessOverlays()
    {
        foreach (KeyValuePair<IntPtr, GameHost> kvp in _viewports)
        {
            IntPtr childHwnd = kvp.Key;
            GameHost viewport = kvp.Value;
            if (!_headlessOverlays.TryGetValue(childHwnd, out Border? overlay))
                continue;
            bool isDetached = _detachedWindows.ContainsKey(childHwnd);
            if (!isDetached && !viewport.IsVisible && !overlay.IsVisible)
                continue;

            bool isHeadless = PredictEffectiveHeadless(childHwnd, !isDetached && _isGridViewEnabled, true);
            viewport.IsVisible = !isHeadless;
            overlay.IsVisible = isHeadless;
            if (!isHeadless)
                viewport.ForceRefreshFrame();
        }
        UpdateStatusBar();
    }

    private sealed class DetachedWindowContext
    {
        public required IntPtr ChildHwnd { get; init; }
        public required int ChildPid { get; init; }
        public required Window Window { get; init; }
        public required Grid Layout { get; init; }
        public required Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar TitleBar { get; init; }
        public required ContentControl ViewportHost { get; init; }
        public required Grid ViewportContainer { get; init; }
        public required GameHost Viewport { get; init; }
        public required Border HeadlessOverlay { get; init; }
        public required QuickBarControls QuickBar { get; init; }
        public Border? StatusBarBorder { get; set; }
        public MaterialIcon? ButlerIcon { get; set; }
        public TextBlock? ButlerText { get; set; }
        public Button? ButlerButton { get; set; }
        public MaterialIcon? WatchdogIcon { get; set; }
        public TextBlock? WatchdogText { get; set; }
        public Button? WatchdogButton { get; set; }
        public MaterialIcon? LocationIcon { get; set; }
        public TextBlock? LocationText { get; set; }
        public Button? LocationButton { get; set; }
        public MaterialIcon? ScriptIcon { get; set; }
        public TextBlock? ScriptText { get; set; }
        public Button? ScriptButton { get; set; }
        public Button? AutoButton { get; set; }
        public MaterialIcon? HeadlessIcon { get; set; }
        public TextBlock? HeadlessText { get; set; }
        public Button? HeadlessButton { get; set; }
        public TextBlock? GameSizeText { get; set; }
        public Button? GameSizeButton { get; set; }
        public bool StretchGame { get; set; } = true;
        public bool GameOnly { get; set; }
        public bool GameOnlySetFullScreen { get; set; }
        public WindowState StateBeforeFullScreen { get; set; } = WindowState.Normal;
    }

    private readonly Dictionary<IntPtr, Window> _detachedWindows = new();
    private readonly Dictionary<IntPtr, ContentControl> _detachedViewportHosts = new();
    private readonly Dictionary<IntPtr, DetachedWindowContext> _detachedContexts = new();

    private void PlatformDetachChild(IntPtr childHwnd, int screenX, int screenY, int width, int height)
    {
        if (_detachedWindows.TryGetValue(childHwnd, out Window? existing))
        {
            existing.Activate();
            return;
        }

        if (!_viewports.TryGetValue(childHwnd, out GameHost? viewport))
            return;

        if (!_headlessOverlays.TryGetValue(childHwnd, out Border? overlay))
        {
            overlay = CreateHeadlessOverlay();
            _headlessOverlays[childHwnd] = overlay;
        }

        TabInfo? info = _tabs.Values.FirstOrDefault(t => t.ChildHwnd == childHwnd);
        int childPid = info?.Process?.Id ?? (int)childHwnd;
        string title = info?.LastCharacterName is { Length: > 0 } lbl
            ? $"{lbl} — BonxieMux"
            : $"BonxieMux V{Skua.Core.Models.ClientFileSources.DisplayVersion}";

        if (viewport.Parent is Panel vpParent)
            vpParent.Children.Remove(viewport);
        else
            _viewportCanvas?.Children.Remove(viewport);

        if (overlay.Parent is Panel ovParent)
            ovParent.Children.Remove(overlay);
        else
            _viewportCanvas?.Children.Remove(overlay);

        Canvas.SetLeft(viewport, 0);
        Canvas.SetTop(viewport, 0);
        viewport.Width = double.NaN;
        viewport.Height = double.NaN;
        viewport.RenderResolutionScale = 1.0;
        viewport.HorizontalAlignment = HorizontalAlignment.Stretch;
        viewport.VerticalAlignment = VerticalAlignment.Stretch;

        Canvas.SetLeft(overlay, 0);
        Canvas.SetTop(overlay, 0);
        overlay.Width = double.NaN;
        overlay.Height = double.NaN;
        overlay.HorizontalAlignment = HorizontalAlignment.Stretch;
        overlay.VerticalAlignment = VerticalAlignment.Stretch;

        Grid viewportContainer = new()
        {
            Children = { viewport, overlay }
        };

        ContentControl viewportHost = new()
        {
            Content = viewportContainer,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        _detachedViewportHosts[childHwnd] = viewportHost;

        Menu detachedMenu = new()
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        detachedMenu.Classes.Add("top-toolbar");
        MenuItem rootMenuItem = new()
        {
            Header = CreateNavIcon(MaterialIconKind.Menu, UiTokens.UiIconMd, UiTokens.SkuaTextBrush)
        };
        rootMenuItem.Classes.Add("top-nav-item");
        ToolTip.SetTip(rootMenuItem, "Application Menu");
        foreach (var entry in _topMenuEntries)
        {
            MenuItem sub = BuildTopMenuItemFromSpec(entry.Spec, childPid);
            sub.Classes.Remove("top-nav-item");
            rootMenuItem.Items.Add(sub);
        }
        detachedMenu.Items.Add(rootMenuItem);

        QuickBarControls detachedQuickBar = new();
        _detachedQuickBars[childPid] = detachedQuickBar;

        static Panel WrapTitleQuickToggle(ToggleButton toggle, Popup popup)
        {
            toggle.Classes.Remove("activity-item");
            toggle.Classes.Add("titlebar");
            UiTokens.Bind(toggle, Layoutable.WidthProperty, UiTokens.UiSmallControlHeight);
            UiTokens.Bind(toggle, Layoutable.HeightProperty, UiTokens.UiSmallControlHeight);
            UiTokens.Bind(toggle, TemplatedControl.CornerRadiusProperty, UiTokens.UiRadiusControl);
            Panel host = new() { Children = { toggle, popup } };
            UiTokens.Bind(host, Layoutable.WidthProperty, UiTokens.UiSmallControlHeight);
            UiTokens.Bind(host, Layoutable.HeightProperty, UiTokens.UiSmallControlHeight);
            return host;
        }

        var (autoToggle, autoPopup) = BuildAutoControls(detachedQuickBar, PlacementMode.BottomEdgeAlignedRight, childPid);
        Panel autoHost = WrapTitleQuickToggle(autoToggle, autoPopup);

        var (jumpToggle, jumpPopup) = BuildJumpControls(detachedQuickBar, PlacementMode.BottomEdgeAlignedRight, childPid);
        Panel jumpHost = WrapTitleQuickToggle(jumpToggle, jumpPopup);

        Button botWinBtn = CreateDetachedTitleButton(MaterialIconKind.Apps, "Show All Bot Windows & Tools", () =>
            TabIpc.SendCommand(childPid, "BOT_WINDOW"));

        StackPanel rightButtons = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { autoHost, jumpHost, botWinBtn }
        };
        UiTokens.Bind(rightButtons, StackPanel.SpacingProperty, UiTokens.UiGap2xs);

        var titleBar = new Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar
        {
            TitleText = title,
            CanDock = true,
            LeftContent = detachedMenu,
            RightContent = rightButtons
        };
        titleBar.DockClicked += (_, _) => DockChildInstance(childPid);

        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(viewportHost, 1);
        layout.Children.Add(titleBar);
        layout.Children.Add(viewportHost);

        Border frame = new() { Child = layout };
        frame.Classes.Add("window-frame");

        Window detachedWindow = new()
        {
            Title = title,
            Width = Math.Max(640, width),
            Height = Math.Max(420, height),
            MinWidth = 640,
            MinHeight = 420,
            Position = new PixelPoint(screenX, screenY),
            WindowStartupLocation = WindowStartupLocation.Manual,
            CanResize = true,
            Icon = Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar.DefaultWindowIcon,
            Content = frame
        };
        Skua.Shared.Avalonia.Services.ShellWindowHelper.ConfigureFramelessWindow(detachedWindow);

        DetachedWindowContext ctx = new()
        {
            ChildHwnd = childHwnd,
            ChildPid = childPid,
            Window = detachedWindow,
            Layout = layout,
            TitleBar = titleBar,
            ViewportHost = viewportHost,
            ViewportContainer = viewportContainer,
            Viewport = viewport,
            HeadlessOverlay = overlay,
            QuickBar = detachedQuickBar,
            StretchGame = _stretchGame
        };

        Border statusBar = BuildDetachedStatusBar(ctx);
        Grid.SetRow(statusBar, 2);
        layout.Children.Add(statusBar);
        _detachedContexts[childHwnd] = ctx;

        viewportHost.SizeChanged += (_, _) => ApplyDetachedViewportSizing(ctx);
        ApplyDetachedViewportSizing(ctx);

        detachedWindow.Activated += (_, _) =>
        {
            HotKeys.ActiveChildHwnd = childHwnd;
            PostChildMessage(childHwnd, (uint)SkuaIpcMessages.Focus, new IntPtr(1), IntPtr.Zero);
            RefreshHeadlessOverlays();
        };
        detachedWindow.Deactivated += (_, _) =>
        {
            if (detachedQuickBar.AutoToggle != null) detachedQuickBar.AutoToggle.IsChecked = false;
            if (detachedQuickBar.JumpToggle != null) detachedQuickBar.JumpToggle.IsChecked = false;
            PostChildMessage(childHwnd, (uint)SkuaIpcMessages.Focus, IntPtr.Zero, IntPtr.Zero);
            RefreshHeadlessOverlays();
        };
        detachedWindow.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (detachedQuickBar.AutoToggle?.IsChecked == true)
                {
                    detachedQuickBar.AutoToggle.IsChecked = false;
                    e.Handled = true;
                    return;
                }
                if (detachedQuickBar.JumpToggle?.IsChecked == true)
                {
                    detachedQuickBar.JumpToggle.IsChecked = false;
                    e.Handled = true;
                    return;
                }
                if (ctx.GameOnly)
                {
                    ToggleDetachedGameOnly(ctx);
                    e.Handled = true;
                    return;
                }
                if (detachedWindow.WindowState == WindowState.FullScreen)
                {
                    ToggleDetachedFullScreen(ctx);
                    e.Handled = true;
                    return;
                }
            }
            if (detachedWindow.FocusManager?.GetFocusedElement() is TextBox)
                return;

            if (e.Key == Key.F11)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    ToggleDetachedGameOnly(ctx);
                else
                    ToggleDetachedFullScreen(ctx);
                e.Handled = true;
                return;
            }

            HotKeys.ActiveChildHwnd = childHwnd;
            MainWindow.ForwardKeyEvent(true, e);
        }, RoutingStrategies.Tunnel);
        detachedWindow.AddHandler(InputElement.KeyUpEvent, (_, e) =>
        {
            if (detachedWindow.FocusManager?.GetFocusedElement() is TextBox)
                return;
            HotKeys.ActiveChildHwnd = childHwnd;
            MainWindow.ForwardKeyEvent(false, e);
        }, RoutingStrategies.Tunnel);

        detachedWindow.Closing += (_, e) =>
        {
            if (_detachedWindows.ContainsKey(childHwnd))
            {
                e.Cancel = true;
                DockChildInstance(childPid);
            }
        };

        _detachedWindows[childHwnd] = detachedWindow;
        detachedWindow.Show();
        detachedWindow.Activate();
        HotKeys.ActiveChildHwnd = childHwnd;

        bool isHeadless = PredictEffectiveHeadless(childHwnd, false, isShownTile: true);
        viewport.IsVisible = !isHeadless;
        overlay.IsVisible = isHeadless;
        viewport.SetHostedVisible(true);
        UpdateStatusBar();
        Dispatcher.UIThread.Post(() =>
        {
            if (!isHeadless)
                viewport.ForceRefreshFrame();
        }, DispatcherPriority.Render);
    }

    private static void ApplyDetachedViewportSizing(DetachedWindowContext ctx)
    {
        if (ctx.StretchGame || ctx.GameOnly)
        {
            ctx.ViewportContainer.Width = double.NaN;
            ctx.ViewportContainer.Height = double.NaN;
            ctx.ViewportContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
            ctx.ViewportContainer.VerticalAlignment = VerticalAlignment.Stretch;
            return;
        }

        double cw = ctx.ViewportHost.Bounds.Width;
        double ch = ctx.ViewportHost.Bounds.Height;
        if (cw <= 1 || ch <= 1)
            return;

        const double aspect = 960.0 / 550.0;
        double targetW;
        double targetH;
        if (cw / ch > aspect)
        {
            targetH = ch;
            targetW = Math.Max(1.0, Math.Round(ch * aspect));
        }
        else
        {
            targetW = cw;
            targetH = Math.Max(1.0, Math.Round(cw / aspect));
        }
        ctx.ViewportContainer.HorizontalAlignment = HorizontalAlignment.Center;
        ctx.ViewportContainer.VerticalAlignment = VerticalAlignment.Center;
        ctx.ViewportContainer.Width = targetW;
        ctx.ViewportContainer.Height = targetH;
    }

    private static void ToggleDetachedFullScreen(DetachedWindowContext ctx, bool keepGameOnly = false)
    {
        bool wasGameOnly = ctx.GameOnly && !keepGameOnly;
        if (wasGameOnly)
            ctx.GameOnlySetFullScreen = false;

        Window w = ctx.Window;
        if (w.WindowState == WindowState.FullScreen)
        {
            w.WindowState = (ctx.StateBeforeFullScreen == WindowState.FullScreen || ctx.StateBeforeFullScreen == WindowState.Minimized)
                ? WindowState.Normal
                : ctx.StateBeforeFullScreen;
            if (wasGameOnly)
                ApplyDetachedGameOnly(ctx, false);
            return;
        }

        ctx.StateBeforeFullScreen = w.WindowState == WindowState.Minimized ? WindowState.Normal : w.WindowState;
        w.WindowState = WindowState.FullScreen;
        if (wasGameOnly)
            ApplyDetachedGameOnly(ctx, false);
    }

    private static void ToggleDetachedGameOnly(DetachedWindowContext ctx)
    {
        if (ctx.GameOnly)
        {
            if (ctx.GameOnlySetFullScreen && ctx.Window.WindowState == WindowState.FullScreen)
                ToggleDetachedFullScreen(ctx, keepGameOnly: true);
            ctx.GameOnlySetFullScreen = false;
            ApplyDetachedGameOnly(ctx, false);
            return;
        }
        ctx.GameOnlySetFullScreen = ctx.Window.WindowState != WindowState.FullScreen;
        if (ctx.GameOnlySetFullScreen)
            ToggleDetachedFullScreen(ctx, keepGameOnly: true);
        ApplyDetachedGameOnly(ctx, true);
    }

    private static void ApplyDetachedGameOnly(DetachedWindowContext ctx, bool on)
    {
        ctx.GameOnly = on;
        ctx.Layout.RowDefinitions = on ? new RowDefinitions("0,*,0") : new RowDefinitions("Auto,*,Auto");
        ctx.TitleBar.IsVisible = !on;
        if (ctx.StatusBarBorder != null)
            ctx.StatusBarBorder.IsVisible = !on;
        ApplyDetachedViewportSizing(ctx);
        if (!on)
        {
            ctx.TitleBar.InvalidateMeasure();
            ctx.TitleBar.InvalidateVisual();
        }
    }

    private static Button CreateDetachedTitleButton(Material.Icons.MaterialIconKind kind, string tooltip, Action onClick)
    {
        Button btn = new()
        {
            Padding = new Thickness(0),
            Content = CreateNavIcon(kind, UiTokens.UiIconSm, UiTokens.SkuaMutedTextBrush)
        };
        btn.Classes.Add("titlebar");
        UiTokens.Bind(btn, Layoutable.WidthProperty, UiTokens.UiSmallControlHeight);
        UiTokens.Bind(btn, Layoutable.HeightProperty, UiTokens.UiSmallControlHeight);
        UiTokens.Bind(btn, TemplatedControl.CornerRadiusProperty, UiTokens.UiRadiusControl);
        ToolTip.SetTip(btn, tooltip);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void PlatformReattachChild(IntPtr childHwnd)
    {
        TabInfo? info = _tabs.Values.FirstOrDefault(t => t.ChildHwnd == childHwnd);
        if (info?.Process != null)
            _detachedQuickBars.Remove(info.Process.Id);

        if (_detachedContexts.Remove(childHwnd, out DetachedWindowContext? ctx))
            ctx.ViewportContainer.Children.Clear();

        if (_detachedViewportHosts.TryGetValue(childHwnd, out ContentControl? viewportHost))
        {
            viewportHost.Content = null;
            _detachedViewportHosts.Remove(childHwnd);
        }

        if (_detachedWindows.TryGetValue(childHwnd, out Window? detachedWindow))
        {
            _detachedWindows.Remove(childHwnd);
            try { detachedWindow.Close(); } catch { }
        }

        if (_viewports.TryGetValue(childHwnd, out GameHost? viewport) &&
            _viewportCanvas != null &&
            !_viewportCanvas.Children.Contains(viewport))
        {
            _viewportCanvas.Children.Insert(0, viewport);
        }

        if (_headlessOverlays.TryGetValue(childHwnd, out Border? overlay) &&
            _viewportCanvas != null &&
            !_viewportCanvas.Children.Contains(overlay))
        {
            _viewportCanvas.Children.Add(overlay);
        }

        Dispatcher.UIThread.Post(() =>
        {
            _lastActiveHwnd = IntPtr.Zero;
            DoReposition();
            if (_viewports.TryGetValue(childHwnd, out GameHost? vp) && vp.IsVisible)
                vp.ForceRefreshFrame();
        }, DispatcherPriority.Background);
    }

    private void PlatformRaiseChild(IntPtr childHwnd)
    {
        if (_detachedWindows.TryGetValue(childHwnd, out Window? detachedWindow))
        {
            if (detachedWindow.WindowState == WindowState.Minimized)
                detachedWindow.WindowState = WindowState.Normal;
            detachedWindow.Activate();
        }
    }

    public static (int rootX, int rootY) GetGlobalPointerPosition()
    {
        return (-1, -1);
    }

    private void PlatformMoveResizeChild(IntPtr childHwnd, int x, int y, int width, int height, bool focus = false)
    {
        if (_detachedWindows.ContainsKey(childHwnd))
            return;

        EnsureHostLayout();
        if (!_viewports.TryGetValue(childHwnd, out GameHost? viewport))
        {
            PlatformReparentChild(childHwnd);
            if (!_viewports.TryGetValue(childHwnd, out viewport))
                return;
        }

        double topNavH = 0.0;
        Point? hostPos = HostContainer.TranslatePoint(new Point(0, 0), this);
        double hostX = hostPos?.X ?? 0.0;
        double hostY = Math.Max(hostPos?.Y ?? 0.0, 0.0);

        double containerW = HostContainer.Bounds.Width > 1 ? HostContainer.Bounds.Width : Math.Max(width, 1);
        double containerH = HostContainer.Bounds.Height > 1 ? HostContainer.Bounds.Height : Math.Max(height, 1);
        double canvasW = Math.Max(containerW, 1.0);
        double canvasH = Math.Max(containerH - topNavH, 1.0);

        double left;
        double top;
        double targetW;
        double targetH;

        bool stretch = (_stretchGame || _gameOnly) && !_isGridViewEnabled;

        if (_isGridViewEnabled)
        {
            left = Math.Max(0.0, x - hostX);
            top = Math.Max(0.0, y - hostY);
            targetW = Math.Max(1.0, Math.Min(width, canvasW - left));
            targetH = Math.Max(1.0, Math.Min(height, canvasH - top));
        }
        else if (stretch)
        {
            left = 0.0;
            top = 0.0;
            targetW = canvasW;
            targetH = canvasH;
        }
        else
        {
            const double aspect = 960.0 / 550.0;
            if (canvasW / canvasH > aspect)
            {
                targetH = canvasH;
                targetW = Math.Max(1.0, Math.Round(canvasH * aspect));
            }
            else
            {
                targetW = canvasW;
                targetH = Math.Max(1.0, Math.Round(canvasW / aspect));
            }
            left = Math.Max(0.0, (canvasW - targetW) / 2.0);
            top = Math.Max(0.0, (canvasH - targetH) / 2.0);
        }

        if (!_isGridViewEnabled)
        {
            foreach (KeyValuePair<IntPtr, GameHost> kvp in _viewports)
            {
                if (kvp.Key != childHwnd)
                    kvp.Value.IsVisible = false;
            }
            foreach (KeyValuePair<IntPtr, Border> kvp in _headlessOverlays)
            {
                if (kvp.Key != childHwnd)
                    kvp.Value.IsVisible = false;
            }
        }

        bool isHeadless = PredictEffectiveHeadless(childHwnd, _isGridViewEnabled, true);
        double gridScale = 1.0;
        if (_isGridViewEnabled)
        {
            int attachedCount = _tabs.Values.Count(t => !t.IsDetached);
            gridScale = attachedCount >= 4 ? 0.65 : 0.85;
        }

        Canvas.SetLeft(viewport, left);
        Canvas.SetTop(viewport, top);
        viewport.StretchToFill = stretch;
        viewport.RenderResolutionScale = gridScale;
        viewport.Width = targetW;
        viewport.Height = targetH;
        viewport.IsVisible = !isHeadless;

        if (_headlessOverlays.TryGetValue(childHwnd, out Border? overlay))
        {
            Canvas.SetLeft(overlay, left);
            Canvas.SetTop(overlay, top);
            overlay.Width = targetW;
            overlay.Height = targetH;
            overlay.IsVisible = isHeadless;
        }

        if (focus && !isHeadless)
            viewport.SetActiveInstance();
        if (!isHeadless)
            viewport.RefreshLayout();
    }

    private void PlatformHideChild(IntPtr childHwnd)
    {
        if (_detachedWindows.ContainsKey(childHwnd))
            return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => PlatformHideChild(childHwnd));
            return;
        }

        if (_viewports.TryGetValue(childHwnd, out GameHost? viewport))
            viewport.IsVisible = false;
        if (_headlessOverlays.TryGetValue(childHwnd, out Border? overlay))
            overlay.IsVisible = false;
    }

    private void PlatformUnmapChild(IntPtr childHwnd)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => PlatformUnmapChild(childHwnd));
            return;
        }

        TabInfo? unmapInfo = _tabs.Values.FirstOrDefault(t => t.ChildHwnd == childHwnd);
        if (unmapInfo?.Process != null)
            _detachedQuickBars.Remove(unmapInfo.Process.Id);

        if (_detachedContexts.Remove(childHwnd, out DetachedWindowContext? detCtx))
            detCtx.ViewportContainer.Children.Clear();

        if (_detachedViewportHosts.Remove(childHwnd, out ContentControl? detachedHost))
            detachedHost.Content = null;
        if (_detachedWindows.Remove(childHwnd, out Window? detachedWindow))
        {
            try { detachedWindow.Close(); } catch { }
        }

        if (_viewports.Remove(childHwnd, out GameHost? viewport))
        {
            viewport.IsVisible = false;
            _viewportCanvas?.Children.Remove(viewport);
            viewport.DisposeResources();
        }
        if (_headlessOverlays.Remove(childHwnd, out Border? overlay))
        {
            overlay.IsVisible = false;
            _viewportCanvas?.Children.Remove(overlay);
        }
        _manualHeadlessStates.Remove(childHwnd);
        _autoHeadlessStates.Remove(childHwnd);
        _headlessSeq.Remove(childHwnd);
    }

    private void PlatformClearAllViewports()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(PlatformClearAllViewports);
            return;
        }

        if (LoadingIndicator != null)
            LoadingIndicator.IsVisible = false;
        if (DetachedPlaceholder != null)
            DetachedPlaceholder.IsVisible = false;

        foreach (GameHost viewport in _viewports.Values)
        {
            viewport.IsVisible = false;
            _viewportCanvas?.Children.Remove(viewport);
            viewport.DisposeResources();
        }
        _viewports.Clear();
        foreach (Border overlay in _headlessOverlays.Values)
        {
            overlay.IsVisible = false;
            _viewportCanvas?.Children.Remove(overlay);
        }
        _headlessOverlays.Clear();
        _manualHeadlessStates.Clear();
        _autoHeadlessStates.Clear();
        _headlessSeq.Clear();
        HotKeys.ActiveChildHwnd = IntPtr.Zero;
    }

    private static void CleanUpChildResources(int pid)
    {
        if (pid <= 0)
            return;

        TabIpc.CleanUp(pid);

        string dir = ClientFileSources.TabsDIR;
        string[] perPidFiles =
        {
            $"{pid}.txt",
            $"ready_{pid}.txt",
            $"loaded_{pid}.txt",
            $"{pid}.state",
            $"{pid}.location.json",
            $"{pid}.army-options.json",
            $"{pid}.options-full.json",
            $"{pid}.dog-notice.json"
        };

        foreach (string fileName in perPidFiles)
        {
            try
            {
                string path = Path.Combine(dir, fileName);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }
    }

    private static void SweepStaleProcessFiles()
    {
        static bool IsProcessAlive(int pid) => pid > 0 && Directory.Exists($"/proc/{pid}");

        try
        {
            string tabsDir = ClientFileSources.TabsDIR;
            if (!Directory.Exists(tabsDir))
                return;

            foreach (string path in Directory.EnumerateFiles(tabsDir))
            {
                string fileName = Path.GetFileName(path);
                int pid = 0;
                bool isPerPid = false;

                if ((fileName.StartsWith("ready_", StringComparison.Ordinal) ||
                     fileName.StartsWith("loaded_", StringComparison.Ordinal) ||
                     fileName.StartsWith("child_", StringComparison.Ordinal)) &&
                    (fileName.EndsWith(".txt", StringComparison.Ordinal) || fileName.EndsWith(".pipe", StringComparison.Ordinal)))
                {
                    int us = fileName.IndexOf('_');
                    int dot = fileName.LastIndexOf('.');
                    if (us >= 0 && dot > us + 1 && int.TryParse(fileName[(us + 1)..dot], out pid))
                        isPerPid = true;
                }
                else
                {
                    int dot = fileName.IndexOf('.');
                    if (dot > 0 && int.TryParse(fileName[..dot], out pid))
                        isPerPid = true;
                }

                if (isPerPid && !IsProcessAlive(pid))
                {
                    try { File.Delete(path); } catch { }
                }
            }
        }
        catch { }
    }
    #endregion
}
