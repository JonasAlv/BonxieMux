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
        MainWindow.ForwardKeyEvent(true, e);
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
        if (_headlessSeq.TryGetValue(childHwnd, out long prevSeq) && seq < prevSeq)
            return;

        _headlessSeq[childHwnd] = seq;
        _manualHeadlessStates[childHwnd] = manual;
        _autoHeadlessStates[childHwnd] = auto;

        if (tab.IsDetached)
            return;

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

    private void RefreshHeadlessOverlays()
    {
        foreach (KeyValuePair<IntPtr, GameHost> kvp in _viewports)
        {
            IntPtr childHwnd = kvp.Key;
            GameHost viewport = kvp.Value;
            if (!_headlessOverlays.TryGetValue(childHwnd, out Border? overlay))
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
        if (_viewports.TryGetValue(childHwnd, out GameHost? viewport))
            viewport.IsVisible = false;
        if (_headlessOverlays.TryGetValue(childHwnd, out Border? overlay))
            overlay.IsVisible = false;
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

        if (_isGridViewEnabled)
        {
            left = Math.Max(0.0, x - hostX);
            top = Math.Max(0.0, y - hostY);
            targetW = Math.Max(1.0, Math.Min(width, canvasW - left));
            targetH = Math.Max(1.0, Math.Min(height, canvasH - top));
        }
        else if (_gameSize is null)
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
        else
        {
            targetW = Math.Max(1.0, Math.Min(width, canvasW));
            targetH = Math.Max(1.0, Math.Min(height, canvasH));
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

        Canvas.SetLeft(viewport, left);
        Canvas.SetTop(viewport, top);
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
