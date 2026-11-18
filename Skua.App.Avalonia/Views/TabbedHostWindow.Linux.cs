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
    private const double DockedNavHeight = 34.0;

    private readonly Dictionary<IntPtr, GameHost> _linuxViewports = new();
    private readonly Dictionary<IntPtr, Border> _linuxHeadlessOverlays = new();
    private readonly Dictionary<IntPtr, bool> _linuxManualHeadlessStates = new();
    private readonly Dictionary<IntPtr, bool> _linuxAutoHeadlessStates = new();
    private readonly Dictionary<IntPtr, long> _linuxHeadlessSeq = new();
    private Grid? _linuxHostGrid;
    private Border? _dockedTopNavBorder;
    private Canvas? _viewportCanvas;
    private bool _linuxHostInitialized;

    private ToggleButton? _linuxAutoToggle;
    private ToggleButton? _linuxJumpToggle;
    private Border? _linuxAutoActiveDot;
    private Border? _linuxAutoRunningBadge;
    private Button? _linuxAutoAttackBtn;
    private Button? _linuxAutoHuntBtn;
    private Button? _linuxAutoStopBtn;
    private ComboBox? _linuxAutoClassBox;
    private ComboBox? _linuxAutoModeBox;
    private TextBox? _linuxAutoMapIdsBox;
    private ComboBox? _linuxJumpCellBox;
    private ComboBox? _linuxJumpPadBox;
    private bool _suppressLinuxPopupSync;

    private void StartGridFocusTracking()
    {
        EnsureLinuxHostLayout();
    }

    private void EnsureLinuxHostLayout()
    {
        if (_linuxHostInitialized)
            return;
        _linuxHostInitialized = true;

        _viewportCanvas = new Canvas
        {
            ClipToBounds = true,
            Background = Brushes.Transparent
        };

        _dockedTopNavBorder = BuildDockedTopNavBar();

        _linuxHostGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        Grid.SetRow(_dockedTopNavBorder, 0);
        Grid.SetRow(_viewportCanvas, 1);
        _linuxHostGrid.Children.Add(_dockedTopNavBorder);
        _linuxHostGrid.Children.Add(_viewportCanvas);

        HostContainer.Child = _linuxHostGrid;

        Task.Run(SweepStaleProcessFiles);
    }

    private Border BuildDockedTopNavBar()
    {
        Border navBorder = new()
        {
            Height = DockedNavHeight,
            Padding = new Thickness(4, 0),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        navBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("SkuaSurfaceBrush");
        navBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SkuaBorderBrush");

        DockPanel dock = new() { LastChildFill = true };

        StackPanel rightTools = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(rightTools, Dock.Right);

        var (autoToggle, autoPopup) = BuildLinuxAutoControls();
        var (jumpToggle, jumpPopup) = BuildLinuxJumpControls();

        Border sep = new()
        {
            Width = 1,
            Height = 16,
            Margin = new Thickness(2, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        sep[!Border.BackgroundProperty] = new DynamicResourceExtension("SkuaBorderBrush");

        Button botWinBtn = new()
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Content = CreateNavIcon(MaterialIconKind.Plus, 15, "SkuaAccentBrush")
        };
        botWinBtn.Classes.Add("top-nav-item");
        ToolTip.SetTip(botWinBtn, "Show All Bot Windows & Tools");
        botWinBtn.Click += (_, _) => SendActiveChildCommand("BOT_WINDOW");

        rightTools.Children.Add(autoToggle);
        rightTools.Children.Add(autoPopup);
        rightTools.Children.Add(jumpToggle);
        rightTools.Children.Add(jumpPopup);
        rightTools.Children.Add(sep);
        rightTools.Children.Add(botWinBtn);

        Menu menu = new() { VerticalAlignment = VerticalAlignment.Center };
        menu.Classes.Add("top-toolbar");

        menu.Items.Add(CreateMenuGroup("Scripts", MaterialIconKind.ScriptTextOutline,
            ("Script Loader", MaterialIconKind.ScriptOutline),
            ("Scheduler", MaterialIconKind.CalendarClock)));
        menu.Items.Add(CreateMenuGroup("Options", MaterialIconKind.CogOutline,
            ("Game", MaterialIconKind.GamepadVariantOutline),
            ("Watch Dog", MaterialIconKind.ShieldCheckOutline),
            ("Application", MaterialIconKind.ApplicationCogOutline),
            ("CoreBots", MaterialIconKind.RobotOutline),
            ("Application Themes", MaterialIconKind.PaletteOutline),
            ("HotKeys", MaterialIconKind.KeyboardOutline)));
        menu.Items.Add(CreateMenuGroup("Tools & Helpers", MaterialIconKind.WrenchOutline,
            ("Loadouts Manager", MaterialIconKind.AccountSwitchOutline),
            ("Fast Travel", MaterialIconKind.CompassOutline),
            ("Loader", MaterialIconKind.DownloadOutline),
            ("Grabber", MaterialIconKind.HandBackRightOutline),
            ("Current Drops", MaterialIconKind.GiftOutline),
            ("Junk Items", MaterialIconKind.DeleteSweepOutline),
            ("Ultra Wiki", MaterialIconKind.CompassOutline),
            ("Calendar", MaterialIconKind.CalendarMonthOutline)));
        menu.Items.Add(CreateMenuGroup("Combat", MaterialIconKind.SwordCross,
            ("Skills", MaterialIconKind.LightningBoltOutline),
            ("Stats", MaterialIconKind.ChartLine),
            ("Runtime", MaterialIconKind.PlayNetworkOutline)));

        MenuItem bankItem = CreateTopMenuItem("Bank", MaterialIconKind.BankOutline);
        bankItem.Click += (_, _) => SendActiveChildCommand("BANK");
        menu.Items.Add(bankItem);

        menu.Items.Add(CreateMenuGroup("Diagnostics", MaterialIconKind.BugOutline,
            ("Logs", MaterialIconKind.FileDocumentOutline),
            ("Console", MaterialIconKind.Console),
            ("Spammer", MaterialIconKind.SendClockOutline),
            ("Logger", MaterialIconKind.TextSearch),
            ("Interceptor", MaterialIconKind.ShieldBugOutline)));
        menu.Items.Add(BuildPluginsMenuItem());

        dock.Children.Add(rightTools);
        dock.Children.Add(menu);
        navBorder.Child = dock;
        return navBorder;
    }

    private Button CreateNavButton(MaterialIconKind kind, string label, string tip)
    {
        Button btn = new()
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    CreateNavIcon(kind, 13, "SkuaAccentBrush"),
                    new TextBlock
                    {
                        Text = label,
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
        btn.Classes.Add("top-nav-item");
        ToolTip.SetTip(btn, tip);
        return btn;
    }

    private static MaterialIcon CreateNavIcon(MaterialIconKind kind, double size, string brushKey)
    {
        MaterialIcon icon = new()
        {
            Kind = kind,
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon[!MaterialIcon.ForegroundProperty] = new DynamicResourceExtension(brushKey);
        return icon;
    }

    private static MenuItem CreateTopMenuItem(string header, MaterialIconKind kind)
    {
        MenuItem item = new()
        {
            Header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    CreateNavIcon(kind, 14, "SkuaAccentBrush"),
                    new TextBlock
                    {
                        Text = header,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 12
                    }
                }
            }
        };
        item.Classes.Add("top-nav-item");
        return item;
    }

    private MenuItem BuildPluginsMenuItem()
    {
        MenuItem parent = CreateMenuGroup("Plugins", MaterialIconKind.PuzzleOutline,
            ("View Plugins", MaterialIconKind.PuzzleOutline),
            ("ButlerV5 Avalonia", MaterialIconKind.RobotOutline),
            ("Daily Tracker", MaterialIconKind.CalendarCheckOutline),
            ("Cosmetics", MaterialIconKind.PaletteOutline),
            ("Perf Recorder", MaterialIconKind.ChartTimelineVariant));

        parent.SubmenuOpened += (_, _) =>
        {
            IPluginHelper? pluginHelper = Ioc.Default.GetService<IPluginHelper>();
            if (pluginHelper == null)
                return;

            HashSet<string> existing = new(
                parent.Items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);

            foreach (IMainMenuItemViewModel btn in pluginHelper.MenuButtons)
            {
                if (string.IsNullOrWhiteSpace(btn.Header) || !existing.Add(btn.Header))
                    continue;

                string targetHeader = btn.Header;
                MenuItem extra = new()
                {
                    Header = targetHeader,
                    Icon = CreateNavIcon(MaterialIconKind.PuzzleOutline, 14, "SkuaAccentBrush")
                };
                extra.Click += (_, _) => SendActiveChildCommand("MENU|" + targetHeader);
                parent.Items.Add(extra);
            }
        };

        return parent;
    }

    private MenuItem CreateMenuGroup(string header, MaterialIconKind kind, params (string Label, MaterialIconKind Icon)[] subItems)
    {
        MenuItem parent = CreateTopMenuItem(header, kind);
        foreach ((string label, MaterialIconKind subKind) in subItems)
        {
            MenuItem sub = new()
            {
                Header = label,
                Icon = CreateNavIcon(subKind, 14, "SkuaAccentBrush")
            };
            sub.Click += (_, _) => SendActiveChildCommand("MENU|" + label);
            parent.Items.Add(sub);
        }
        return parent;
    }

    private static MenuItem CreateFlyoutItem(string header, Action onClick)
    {
        MenuItem item = new() { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void SendActiveChildCommand(string command)
    {
        int pid = 0;
        if (InstancesTabControl.SelectedItem is TabItem selected &&
            _tabs.TryGetValue(selected, out TabInfo? info) &&
            info.Process != null &&
            !info.Process.HasExited)
        {
            pid = info.Process.Id;
        }
        else if (Skua.Core.AppStartup.HotKeys.ActiveChildHwnd != IntPtr.Zero)
        {
            TabInfo? active = _tabs.Values.FirstOrDefault(t => t.ChildHwnd == Skua.Core.AppStartup.HotKeys.ActiveChildHwnd);
            if (active?.Process != null && !active.Process.HasExited)
                pid = active.Process.Id;
        }

        if (pid > 0)
            TabIpc.SendCommand(pid, command);
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
        EnsureLinuxHostLayout();
        if (_viewportCanvas == null || _linuxViewports.ContainsKey(childHwnd))
            return;

        TabInfo? tab = _tabs.Values.FirstOrDefault(t => t.ChildHwnd == childHwnd);
        int childPid = tab?.Process?.Id ?? (int)childHwnd.ToInt64();
        if (childPid <= 0)
            return;

        GameHost viewport = new(childPid)
        {
            IsVisible = false
        };
        void OnViewportActivated()
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
        }
        viewport.Clicked += OnViewportActivated;
        viewport.AddHandler(InputElement.PointerPressedEvent, (_, _) => OnViewportActivated(), RoutingStrategies.Tunnel, handledEventsToo: true);
        _linuxViewports[childHwnd] = viewport;
        _viewportCanvas.Children.Add(viewport);

        Border headlessOverlay = CreateHeadlessOverlay();
        _linuxHeadlessOverlays[childHwnd] = headlessOverlay;
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
        icon[!MaterialIcon.ForegroundProperty] = new DynamicResourceExtension("SkuaMutedTextBrush");

        var title = new TextBlock
        {
            Text = "Headless Mode Active",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SkuaTextBrush");

        var subtitle = new TextBlock
        {
            Text = "Viewport suspended to minimize CPU and GPU usage",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        subtitle[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SkuaMutedTextBrush");

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8
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
        overlay[!Border.BackgroundProperty] = new DynamicResourceExtension("SkuaPageBrush");
        return overlay;
    }

    private void ApplyChildHeadlessState(int childPid, bool manual, bool auto, long seq)
    {
        TabInfo? tab = _tabs.Values.FirstOrDefault(t => t.Process != null && t.Process.Id == childPid);
        if (tab == null || tab.ChildHwnd == IntPtr.Zero)
            return;

        IntPtr childHwnd = tab.ChildHwnd;
        if (_linuxHeadlessSeq.TryGetValue(childHwnd, out long prevSeq) && seq < prevSeq)
            return;

        _linuxHeadlessSeq[childHwnd] = seq;
        _linuxManualHeadlessStates[childHwnd] = manual;
        _linuxAutoHeadlessStates[childHwnd] = auto;

        if (tab.IsDetached)
            return;

        if (_linuxViewports.TryGetValue(childHwnd, out GameHost? viewport) &&
            _linuxHeadlessOverlays.TryGetValue(childHwnd, out Border? overlay))
        {
            bool shownInHost = viewport.IsVisible || overlay.IsVisible;
            if (shownInHost)
            {
                // Host state (active tab, Grid Mode, window focus) is authoritative for AutoHeadless;
                // prevents stale in-flight child HEADLESS_STATE messages from sticking the overlay on.
                bool isHeadless = PredictEffectiveHeadless(childHwnd, _isGridViewEnabled, isShownTile: true);
                viewport.IsVisible = !isHeadless;
                overlay.IsVisible = isHeadless;
                if (!isHeadless)
                    viewport.ForceRefreshFrame();
            }
        }
    }

    private bool PredictEffectiveHeadless(IntPtr childHwnd, bool isGrid, bool isShownTile)
    {
        bool manual = _linuxManualHeadlessStates.TryGetValue(childHwnd, out bool m) && m;
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
        _linuxAutoHeadlessStates[childHwnd] = auto;
        return manual || auto;
    }

    private void RefreshLinuxHeadlessOverlays()
    {
        foreach (KeyValuePair<IntPtr, GameHost> kvp in _linuxViewports)
        {
            IntPtr childHwnd = kvp.Key;
            GameHost viewport = kvp.Value;
            if (!_linuxHeadlessOverlays.TryGetValue(childHwnd, out Border? overlay))
                continue;
            if (!viewport.IsVisible && !overlay.IsVisible)
                continue;

            bool isHeadless = PredictEffectiveHeadless(childHwnd, _isGridViewEnabled, true);
            viewport.IsVisible = !isHeadless;
            overlay.IsVisible = isHeadless;
            if (!isHeadless)
                viewport.ForceRefreshFrame();
        }
    }

    private void PlatformDetachChild(IntPtr childHwnd, int screenX, int screenY, int width, int height)
    {
        if (_linuxViewports.TryGetValue(childHwnd, out GameHost? viewport))
            viewport.IsVisible = false;
        if (_linuxHeadlessOverlays.TryGetValue(childHwnd, out Border? overlay))
            overlay.IsVisible = false;

        if (_dockedTopNavBorder != null &&
            InstancesTabControl.SelectedItem is TabItem selected &&
            _tabs.TryGetValue(selected, out TabInfo? selectedInfo) &&
            selectedInfo.ChildHwnd == childHwnd)
        {
            _dockedTopNavBorder.IsVisible = false;
        }
    }

    private void PlatformReattachChild(IntPtr childHwnd)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _lastActiveHwnd = IntPtr.Zero;
            DoReposition();
        }, DispatcherPriority.Background);
    }

    private void PlatformRaiseChild(IntPtr childHwnd)
    {
    }

    public static (int rootX, int rootY) GetGlobalPointerPosition()
    {
        return (-1, -1);
    }

    private void PlatformMoveResizeChild(IntPtr childHwnd, int x, int y, int width, int height, bool focus = false)
    {
        EnsureLinuxHostLayout();
        if (!_linuxViewports.TryGetValue(childHwnd, out GameHost? viewport))
        {
            PlatformReparentChild(childHwnd);
            if (!_linuxViewports.TryGetValue(childHwnd, out viewport))
                return;
        }

        if (_dockedTopNavBorder != null)
            _dockedTopNavBorder.IsVisible = !_isGridViewEnabled && !_gameOnly;

        double topNavH = (_dockedTopNavBorder?.IsVisible == true) ? DockedNavHeight : 0.0;
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

        if (_isGridViewEnabled)
        {
            left = Math.Max(0.0, x - hostX);
            top = Math.Max(0.0, y - hostY);
            targetW = Math.Max(1.0, Math.Min(width, canvasW - left));
            targetH = Math.Max(1.0, Math.Min(height, canvasH - top));
        }
        else if (_gameSize is null)
        {
            left = 0.0;
            top = 0.0;
            targetW = canvasW;
            targetH = canvasH;
        }
        else
        {
            targetW = Math.Max(1.0, Math.Min(width, canvasW));
            targetH = Math.Max(1.0, Math.Min(height, canvasH));
            left = Math.Max(0.0, (canvasW - targetW) / 2.0);
            top = Math.Max(0.0, (canvasH - targetH) / 2.0);
        }

        if (!_isGridViewEnabled)
        {
            foreach (KeyValuePair<IntPtr, GameHost> kvp in _linuxViewports)
            {
                if (kvp.Key != childHwnd)
                    kvp.Value.IsVisible = false;
            }
            foreach (KeyValuePair<IntPtr, Border> kvp in _linuxHeadlessOverlays)
            {
                if (kvp.Key != childHwnd)
                    kvp.Value.IsVisible = false;
            }
        }

        bool isHeadless = PredictEffectiveHeadless(childHwnd, _isGridViewEnabled, true);

        Canvas.SetLeft(viewport, left);
        Canvas.SetTop(viewport, top);
        viewport.Width = targetW;
        viewport.Height = targetH;
        viewport.IsVisible = !isHeadless;

        if (_linuxHeadlessOverlays.TryGetValue(childHwnd, out Border? overlay))
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
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => PlatformHideChild(childHwnd));
            return;
        }

        if (_linuxViewports.TryGetValue(childHwnd, out GameHost? viewport))
            viewport.IsVisible = false;
        if (_linuxHeadlessOverlays.TryGetValue(childHwnd, out Border? overlay))
            overlay.IsVisible = false;
    }

    private void AttachRemotePlayerPid(int childPid, int mugetsuPid)
    {
        if (childPid <= 0 || mugetsuPid <= 0)
            return;

        TabInfo? info = _tabs.Values.FirstOrDefault(t => t.Process?.Id == childPid);
        if (info != null && _linuxViewports.TryGetValue(info.ChildHwnd, out GameHost? viewport))
        {
            viewport.AttachRemotePlayerPid(mugetsuPid);
            return;
        }

        GameHost.RegisterPendingRemotePlayerPid(childPid, mugetsuPid);
    }

    private void SuspendRemoteViewport(IntPtr childHwnd)
    {
        if (_linuxViewports.TryGetValue(childHwnd, out GameHost? viewport))
            viewport.SuspendCapture();
    }

    private void PlatformUnmapChild(IntPtr childHwnd)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => PlatformUnmapChild(childHwnd));
            return;
        }

        if (_linuxViewports.Remove(childHwnd, out GameHost? viewport))
        {
            viewport.IsVisible = false;
            _viewportCanvas?.Children.Remove(viewport);
            viewport.DisposeResources();
        }
        if (_linuxHeadlessOverlays.Remove(childHwnd, out Border? overlay))
        {
            overlay.IsVisible = false;
            _viewportCanvas?.Children.Remove(overlay);
        }
        _linuxManualHeadlessStates.Remove(childHwnd);
        _linuxAutoHeadlessStates.Remove(childHwnd);
        _linuxHeadlessSeq.Remove(childHwnd);
    }

    private void PlatformClearAllViewports()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(PlatformClearAllViewports);
            return;
        }

        if (_dockedTopNavBorder != null)
            _dockedTopNavBorder.IsVisible = false;
        if (LoadingIndicator != null)
            LoadingIndicator.IsVisible = false;
        if (DetachedPlaceholder != null)
            DetachedPlaceholder.IsVisible = false;

        foreach (GameHost viewport in _linuxViewports.Values)
        {
            viewport.IsVisible = false;
            _viewportCanvas?.Children.Remove(viewport);
            viewport.DisposeResources();
        }
        _linuxViewports.Clear();
        foreach (Border overlay in _linuxHeadlessOverlays.Values)
        {
            overlay.IsVisible = false;
            _viewportCanvas?.Children.Remove(overlay);
        }
        _linuxHeadlessOverlays.Clear();
        _linuxManualHeadlessStates.Clear();
        _linuxAutoHeadlessStates.Clear();
        _linuxHeadlessSeq.Clear();
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
    private (ToggleButton, Popup) BuildLinuxAutoControls()
    {
        _linuxAutoActiveDot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = Brush.Parse("#10B981"),
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false
        };
        StackPanel autoContent = CreateNavContent(MaterialIconKind.RobotOutline, "Auto");
        autoContent.Children.Add(_linuxAutoActiveDot);
        ToggleButton toggle = new()
        {
            Content = autoContent
        };
        toggle.Classes.Add("top-nav-item");
        ToolTip.SetTip(toggle, "Automated Combat & Hunting Controls");
        _linuxAutoToggle = toggle;

        _linuxAutoRunningBadge = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2),
            Background = Brush.Parse("#064E3B"),
            BorderBrush = Brush.Parse("#059669"),
            BorderThickness = new Thickness(1),
            IsVisible = false,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new MaterialIcon { Kind = MaterialIconKind.ProgressCheck, Width = 12, Height = 12, Foreground = Brush.Parse("#34D399") },
                    new TextBlock { Text = "RUNNING", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#34D399") }
                }
            }
        };
        Grid.SetColumn(_linuxAutoRunningBadge, 1);

        Grid headerGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), VerticalAlignment = VerticalAlignment.Center };
        headerGrid.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                CreateNavIcon(MaterialIconKind.RobotOutline, 16, "SkuaAccentBrush"),
                CreateNavTitle("Combat Automation")
            }
        });
        headerGrid.Children.Add(_linuxAutoRunningBadge);

        _linuxAutoAttackBtn = new Button { Height = 34, HorizontalAlignment = HorizontalAlignment.Stretch, Content = CreatePopupButtonContent(MaterialIconKind.PlayCircleOutline, "Auto Attack", true) };
        _linuxAutoAttackBtn.Classes.Add("skua-button");
        _linuxAutoAttackBtn.Classes.Add("skua-primary");
        _linuxAutoAttackBtn.Click += (_, _) =>
        {
            SendActiveChildCommand("AUTO_ATTACK");
            if (_linuxAutoToggle != null) _linuxAutoToggle.IsChecked = false;
        };

        _linuxAutoHuntBtn = new Button { Height = 34, HorizontalAlignment = HorizontalAlignment.Stretch, Content = CreatePopupButtonContent(MaterialIconKind.CrosshairsGps, "Auto Hunt", true) };
        _linuxAutoHuntBtn.Classes.Add("skua-button");
        _linuxAutoHuntBtn.Click += (_, _) =>
        {
            SendActiveChildCommand("AUTO_HUNT");
            if (_linuxAutoToggle != null) _linuxAutoToggle.IsChecked = false;
        };
        Grid.SetColumn(_linuxAutoHuntBtn, 1);

        _linuxAutoStopBtn = new Button { Height = 34, HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false, Content = CreatePopupButtonContent(MaterialIconKind.StopCircleOutline, "Stop Automation", true) };
        _linuxAutoStopBtn.Classes.Add("skua-button");
        _linuxAutoStopBtn.Classes.Add("skua-danger");
        Grid.SetColumnSpan(_linuxAutoStopBtn, 2);
        _linuxAutoStopBtn.Click += (_, _) =>
        {
            SendActiveChildCommand("AUTO_STOP");
            if (_linuxAutoToggle != null) _linuxAutoToggle.IsChecked = false;
        };

        Grid actionsGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        actionsGrid.Children.Add(_linuxAutoAttackBtn);
        actionsGrid.Children.Add(_linuxAutoHuntBtn);
        actionsGrid.Children.Add(_linuxAutoStopBtn);

        _linuxAutoClassBox = new ComboBox { Height = 34, MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Select Class..." };
        _linuxAutoClassBox.SelectionChanged += (_, _) =>
        {
            if (!_suppressLinuxPopupSync && _linuxAutoClassBox.SelectedItem is string cls)
                SendActiveChildCommand("AUTO_CLASS|" + cls);
        };

        _linuxAutoModeBox = new ComboBox { Height = 34, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Mode..." };
        Grid.SetColumn(_linuxAutoModeBox, 1);
        _linuxAutoModeBox.SelectionChanged += (_, _) =>
        {
            if (!_suppressLinuxPopupSync && _linuxAutoModeBox.SelectedItem is string mode)
                SendActiveChildCommand("AUTO_MODE|" + mode);
        };

        Grid classModeGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        classModeGrid.Children.Add(_linuxAutoClassBox);
        classModeGrid.Children.Add(_linuxAutoModeBox);

        _linuxAutoMapIdsBox = new TextBox { Height = 32, FontSize = 12, Watermark = "Manual MapIDs (optional, e.g. 1, 2)" };
        _linuxAutoMapIdsBox.LostFocus += (_, _) =>
        {
            if (!_suppressLinuxPopupSync)
                SendActiveChildCommand("AUTO_MAPS|" + (_linuxAutoMapIdsBox.Text ?? string.Empty));
        };

        Border sep = new() { Height = 1 };
        sep[!Border.BackgroundProperty] = new DynamicResourceExtension("SkuaBorderBrush");

        Border card = new()
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            MinWidth = 360,
            Width = 360,
            Child = new StackPanel
            {
                Spacing = 10,
                Children = { headerGrid, sep, actionsGrid, classModeGrid, _linuxAutoMapIdsBox }
            }
        };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("SkuaSurfaceBrush");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("SkuaBorderBrush");

        Popup popup = new()
        {
            Placement = PlacementMode.Bottom,
            IsLightDismissEnabled = true,
            PlacementTarget = toggle,
            Child = new TopbarPopupPanel { Content = card }
        };

        toggle.IsCheckedChanged += (_, _) =>
        {
            bool open = toggle.IsChecked == true;
            if (open && _linuxJumpToggle != null)
                _linuxJumpToggle.IsChecked = false;
            popup.IsOpen = open;
            if (open)
                SendActiveChildCommand("GET_AUTO_STATE");
        };
        popup.Closed += (_, _) => toggle.IsChecked = false;

        return (toggle, popup);
    }

    private (ToggleButton, Popup) BuildLinuxJumpControls()
    {
        ToggleButton toggle = new()
        {
            Content = CreateNavContent(MaterialIconKind.RunFast, "Jump")
        };
        toggle.Classes.Add("top-nav-item");
        ToolTip.SetTip(toggle, "Map & Cell Fast Jump Controls");
        _linuxJumpToggle = toggle;

        StackPanel header = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                CreateNavIcon(MaterialIconKind.RunFast, 16, "SkuaAccentBrush"),
                CreateNavTitle("Fast Map Jump")
            }
        };

        _linuxJumpCellBox = new ComboBox { Height = 34, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Select Cell..." };
        _linuxJumpPadBox = new ComboBox { Height = 34, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Select Pad..." };
        Grid.SetColumn(_linuxJumpPadBox, 1);

        _linuxJumpCellBox.SelectionChanged += (_, _) =>
        {
            if (_suppressLinuxPopupSync || _linuxJumpCellBox.SelectedItem is not string cell || string.IsNullOrEmpty(cell))
                return;
            if (cell == "Enter")
                _linuxJumpPadBox.SelectedItem = "Spawn";
            else if (_linuxJumpPadBox.SelectedItem is not string p || string.IsNullOrEmpty(p))
                _linuxJumpPadBox.SelectedItem = "Left";
            string pad = _linuxJumpPadBox.SelectedItem as string ?? "Spawn";
            SendActiveChildCommand($"JUMP_TO|{cell}|{pad}");
        };

        Grid selectGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        selectGrid.Children.Add(_linuxJumpCellBox);
        selectGrid.Children.Add(_linuxJumpPadBox);

        Button currentBtn = new() { Height = 34, HorizontalAlignment = HorizontalAlignment.Stretch, Content = CreatePopupButtonContent(MaterialIconKind.MapMarkerOutline, "Current", false) };
        currentBtn.Classes.Add("skua-button");
        currentBtn.Click += (_, _) => SendActiveChildCommand("JUMP_CURRENT");

        Button jumpBtn = new() { Height = 34, HorizontalAlignment = HorizontalAlignment.Stretch, Content = CreatePopupButtonContent(MaterialIconKind.RunFast, "Jump", true) };
        jumpBtn.Classes.Add("skua-button");
        jumpBtn.Classes.Add("skua-primary");
        Grid.SetColumn(jumpBtn, 1);
        jumpBtn.Click += (_, _) =>
        {
            string cell = _linuxJumpCellBox?.SelectedItem as string ?? string.Empty;
            string pad = _linuxJumpPadBox?.SelectedItem as string ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(cell) && !string.IsNullOrWhiteSpace(pad))
                SendActiveChildCommand($"JUMP_TO|{cell}|{pad}");
        };

        Grid actionsGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        actionsGrid.Children.Add(currentBtn);
        actionsGrid.Children.Add(jumpBtn);

        Border sep = new() { Height = 1 };
        sep[!Border.BackgroundProperty] = new DynamicResourceExtension("SkuaBorderBrush");

        Border card = new()
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            MinWidth = 320,
            Width = 320,
            Child = new StackPanel
            {
                Spacing = 10,
                Children = { header, sep, selectGrid, actionsGrid }
            }
        };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("SkuaSurfaceBrush");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("SkuaBorderBrush");

        Popup popup = new()
        {
            Placement = PlacementMode.Bottom,
            IsLightDismissEnabled = true,
            PlacementTarget = toggle,
            Child = new TopbarPopupPanel { Content = card }
        };

        toggle.IsCheckedChanged += (_, _) =>
        {
            bool open = toggle.IsChecked == true;
            if (open && _linuxAutoToggle != null)
                _linuxAutoToggle.IsChecked = false;
            popup.IsOpen = open;
            if (open)
                SendActiveChildCommand("GET_JUMP_STATE");
        };
        popup.Closed += (_, _) => toggle.IsChecked = false;

        return (toggle, popup);
    }

    private static StackPanel CreateNavContent(MaterialIconKind kind, string text) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 5,
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            CreateNavIcon(kind, 13, "SkuaAccentBrush"),
            new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }
        }
    };

    private static TextBlock CreateNavTitle(string text)
    {
        TextBlock tb = new() { Text = text, FontWeight = FontWeight.Bold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        tb[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SkuaTextBrush");
        return tb;
    }

    private static StackPanel CreatePopupButtonContent(MaterialIconKind kind, string text, bool bold) => new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Center,
        Spacing = 6,
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            new MaterialIcon { Kind = kind, Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = text, FontWeight = bold ? FontWeight.Bold : FontWeight.SemiBold, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }
        }
    };

    private void ApplyChildAutoState(int childPid, string json)
    {
        if (InstancesTabControl.SelectedItem is TabItem sel &&
            _tabs.TryGetValue(sel, out TabInfo? info) &&
            info.Process?.Id != childPid)
            return;

        try
        {
            JObject obj = JObject.Parse(json);
            _suppressLinuxPopupSync = true;
            bool isRunning = obj.Value<bool>("isRunning");
            if (_linuxAutoActiveDot != null) _linuxAutoActiveDot.IsVisible = isRunning;
            if (_linuxAutoRunningBadge != null) _linuxAutoRunningBadge.IsVisible = isRunning;
            if (_linuxAutoAttackBtn != null) _linuxAutoAttackBtn.IsVisible = !isRunning;
            if (_linuxAutoHuntBtn != null) _linuxAutoHuntBtn.IsVisible = !isRunning;
            if (_linuxAutoStopBtn != null) _linuxAutoStopBtn.IsVisible = isRunning;

            if (_linuxAutoClassBox != null)
            {
                _linuxAutoClassBox.ItemsSource = obj["classes"]?.ToObject<List<string>>() ?? new List<string>();
                _linuxAutoClassBox.SelectedItem = obj.Value<string?>("selectedClass");
            }
            if (_linuxAutoModeBox != null)
            {
                _linuxAutoModeBox.ItemsSource = obj["modes"]?.ToObject<List<string>>() ?? new List<string>();
                _linuxAutoModeBox.SelectedItem = obj.Value<string?>("selectedMode");
            }
            if (_linuxAutoMapIdsBox != null)
            {
                _linuxAutoMapIdsBox.Text = obj.Value<string?>("manualMapIds") ?? string.Empty;
            }
        }
        catch { }
        finally
        {
            _suppressLinuxPopupSync = false;
        }
    }

    private void ApplyChildJumpState(int childPid, string json)
    {
        if (InstancesTabControl.SelectedItem is TabItem sel &&
            _tabs.TryGetValue(sel, out TabInfo? info) &&
            info.Process?.Id != childPid)
            return;

        try
        {
            JObject obj = JObject.Parse(json);
            _suppressLinuxPopupSync = true;
            if (_linuxJumpCellBox != null)
            {
                _linuxJumpCellBox.ItemsSource = obj["cells"]?.ToObject<List<string>>() ?? new List<string>();
                _linuxJumpCellBox.SelectedItem = obj.Value<string?>("selectedCell");
            }
            if (_linuxJumpPadBox != null)
            {
                _linuxJumpPadBox.ItemsSource = obj["pads"]?.ToObject<List<string>>() ?? new List<string>();
                _linuxJumpPadBox.SelectedItem = obj.Value<string?>("selectedPad");
            }
        }
        catch { }
        finally
        {
            _suppressLinuxPopupSync = false;
        }
    }

    #endregion
}
