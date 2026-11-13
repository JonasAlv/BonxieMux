using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
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
    private Grid? _linuxHostGrid;
    private Border? _dockedTopNavBorder;
    private Canvas? _viewportCanvas;
    private bool _linuxHostInitialized;

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

        AddHandler(InputElement.KeyDownEvent, OnHostKeyDown, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyUpEvent, OnHostKeyUp, RoutingStrategies.Tunnel);

        Task.Run(SweepStaleProcessFiles);
    }

    private void OnHostKeyDown(object? sender, KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;
        if (e.Key == Key.Escape && (_gameOnly || WindowState == WindowState.FullScreen))
            return;
        MainWindow.ForwardLinuxKeyEvent(true, e);
    }

    private void OnHostKeyUp(object? sender, KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;
        MainWindow.ForwardLinuxKeyEvent(false, e);
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

        Button autoBtn = CreateNavButton(MaterialIconKind.RobotOutline, "Auto", "Automated Combat & Hunting Controls");
        MenuFlyout autoFlyout = new();
        autoFlyout.Items.Add(CreateFlyoutItem("Auto Attack", () => SendActiveChildCommand("AUTO_ATTACK")));
        autoFlyout.Items.Add(CreateFlyoutItem("Auto Hunt", () => SendActiveChildCommand("AUTO_HUNT")));
        autoFlyout.Items.Add(CreateFlyoutItem("Stop Automation", () => SendActiveChildCommand("AUTO_STOP")));
        autoFlyout.Items.Add(new Separator());
        autoFlyout.Items.Add(CreateFlyoutItem("Class / Skills", () => SendActiveChildCommand("MENU|Skills")));
        autoBtn.Flyout = autoFlyout;

        Button jumpBtn = CreateNavButton(MaterialIconKind.RunFast, "Jump", "Map & Cell Fast Jump Controls");
        jumpBtn.Click += (_, _) => SendActiveChildCommand("JUMP_WINDOW");

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
            Content = CreateNavIcon(MaterialIconKind.Plus, 15, "SkuaTextBrush")
        };
        botWinBtn.Classes.Add("top-nav-item");
        ToolTip.SetTip(botWinBtn, "Show All Bot Windows & Tools");
        botWinBtn.Click += (_, _) => SendActiveChildCommand("BOT_WINDOW");

        rightTools.Children.Add(autoBtn);
        rightTools.Children.Add(jumpBtn);
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
                    CreateNavIcon(kind, 13, "SkuaTextBrush"),
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
                    CreateNavIcon(kind, 14, "SkuaTextBrush"),
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
            ("Cosmetics", MaterialIconKind.PaletteOutline));

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
                    Icon = CreateNavIcon(MaterialIconKind.PuzzleOutline, 14, "SkuaMutedTextBrush")
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
                Icon = CreateNavIcon(subKind, 14, "SkuaMutedTextBrush")
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
        _linuxViewports[childHwnd] = viewport;
        _viewportCanvas.Children.Add(viewport);
    }

    private void PlatformDetachChild(IntPtr childHwnd, int screenX, int screenY, int width, int height)
    {
        if (_linuxViewports.TryGetValue(childHwnd, out GameHost? viewport))
            viewport.IsVisible = false;

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
        }

        Canvas.SetLeft(viewport, left);
        Canvas.SetTop(viewport, top);
        viewport.Width = targetW;
        viewport.Height = targetH;
        viewport.IsVisible = true;
        if (focus)
            viewport.SetActiveInstance();
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
        HotKeys.ActiveChildHwnd = IntPtr.Zero;
    }

    private static void CleanUpChildResources(int pid)
    {
        if (pid <= 0)
            return;

        TabIpc.CleanUp(pid);

        try
        {
            string shmPath = GameHost.GetShmPathForPid(pid);
            if (File.Exists(shmPath))
                File.Delete(shmPath);
        }
        catch { }

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
            string shmDir = Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath();
            foreach (string path in Directory.EnumerateFiles(shmDir, "skua-shm-*.bin"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (name.StartsWith("skua-shm-", StringComparison.Ordinal) &&
                    int.TryParse(name["skua-shm-".Length..], out int pid) &&
                    !IsProcessAlive(pid))
                {
                    try { File.Delete(path); } catch { }
                }
            }
        }
        catch { }

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
