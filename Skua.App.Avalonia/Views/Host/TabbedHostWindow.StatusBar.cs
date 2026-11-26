using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.DependencyInjection;
using Material.Icons;
using Material.Icons.Avalonia;
using Skua.App.Avalonia.Services;
using Skua.Core.AppStartup;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Scripts;
using Skua.Shared.Avalonia.Theming;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Skua.App.Avalonia.Views;

public partial class TabbedHostWindow
{
    private readonly record struct ChildScriptState(string ScriptName, bool IsRunning, bool IsPaused);

    private readonly Dictionary<int, ChildScriptState> _childScriptStates = new();
    private readonly Dictionary<string, ScriptOption.RescueAccountInfo> _watchdogAccounts = new(StringComparer.OrdinalIgnoreCase);
    private bool _watchdogEnabled;
    private int _activeFollowGroupCount;
    private bool _activeTabAutoRunning;

    private void ApplyChildScriptState(int childPid, bool isRunning, bool isPaused, string scriptName)
    {
        _childScriptStates[childPid] = new ChildScriptState(scriptName, isRunning, isPaused);
        UpdateStatusBar();
    }

    private void UpdateTabScriptIndicators()
    {
        foreach (TabInfo info in _tabs.Values)
        {
            if (info.ScriptDot == null)
                continue;

            int pid = info.Process?.Id ?? 0;
            if (pid > 0 && _childScriptStates.TryGetValue(pid, out ChildScriptState st) && (st.IsRunning || st.IsPaused))
            {
                info.ScriptDot.IsVisible = true;
                UiTokens.Bind(
                    info.ScriptDot,
                    Border.BackgroundProperty,
                    st.IsPaused ? UiTokens.SkuaWarningBrush : UiTokens.SkuaSuccessBrush);
                string label = string.IsNullOrWhiteSpace(st.ScriptName) ? "Script" : st.ScriptName;
                ToolTip.SetTip(info.ScriptDot, st.IsPaused ? $"Paused: {label}" : $"Running: {label}");
            }
            else
            {
                info.ScriptDot.IsVisible = false;
                ToolTip.SetTip(info.ScriptDot, null);
            }
        }
    }

    private TabInfo? ResolveStatusTabInfo()
    {
        if (InstancesTabControl.SelectedItem is TabItem selected && _tabs.TryGetValue(selected, out TabInfo? selInfo))
            return selInfo;

        if (_lastSelectedTab != null && _tabs.TryGetValue(_lastSelectedTab, out TabInfo? lastInfo))
            return lastInfo;

        if (HotKeys.ActiveChildHwnd != IntPtr.Zero)
            return _tabs.Values.FirstOrDefault(t => t.ChildHwnd == HotKeys.ActiveChildHwnd);

        return _tabs.Values.FirstOrDefault();
    }

    private void UpdateStatusBar()
    {
        if (HostStatusBarBorder == null || _isClosing)
            return;

        UpdateTabScriptIndicators();

        TabInfo? activeInfo = ResolveStatusTabInfo();
        string charKey = !string.IsNullOrWhiteSpace(activeInfo?.LastCharacterName)
            ? activeInfo.LastCharacterName
            : (activeInfo?.AccountOrderKey ?? string.Empty);

        // Left (workspace-scoped)
        UpdateButlerStatusItem(activeInfo, charKey);
        UpdateWatchdogStatusItem(activeInfo, charKey);

        // Right (active-tab / contextual)
        UpdateLocationStatusItem(activeInfo);
        UpdateScriptStatusItem(activeInfo);

        StatusAutoButton.IsVisible = _activeTabAutoRunning;

        bool autoHeadless = false;
        try { autoHeadless = Ioc.Default.GetService<IScriptOption>()?.AutoHeadless == true; } catch { }

        bool isHeadless = activeInfo != null
            && activeInfo.ChildHwnd != IntPtr.Zero
            && PredictEffectiveHeadless(activeInfo.ChildHwnd, _isGridViewEnabled, isShownTile: true);
        UpdateHeadlessStatusItemCore(activeInfo != null, autoHeadless, isHeadless, StatusHeadlessButton, StatusHeadlessIcon, StatusHeadlessText);

        StatusGridLayoutButton.IsVisible = _isGridViewEnabled;
        StatusGridLayoutText.Text = _gridLayout switch
        {
            "vertical" => "Grid: Stack",
            "horizontal" => "Grid: Side",
            _ => "Grid: Auto"
        };

        StatusGameSizeButton.IsVisible = !_isGridViewEnabled;
        StatusGameSizeText.Text = _stretchGame ? "Stretch" : "Fit";
        ToolTip.SetTip(
            StatusGameSizeButton,
            _stretchGame
                ? "Viewport: Stretch (fills window) — click for Fit"
                : "Viewport: Fit (960:550) — click for Stretch");

        UpdateDetachedStatusBars(autoHeadless);
    }

    private Border BuildDetachedStatusBar(DetachedWindowContext ctx)
    {
        static (Button Btn, MaterialIcon Icon, TextBlock Text) CreateItem(
            MaterialIconKind kind,
            string initialText,
            string tooltip,
            double maxTextWidth = double.PositiveInfinity)
        {
            MaterialIcon icon = new()
            {
                Kind = kind,
                VerticalAlignment = VerticalAlignment.Center
            };
            UiTokens.Bind(icon, MaterialIcon.WidthProperty, UiTokens.UiIconSm);
            UiTokens.Bind(icon, MaterialIcon.HeightProperty, UiTokens.UiIconSm);
            UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, UiTokens.SkuaMutedTextBrush);

            TextBlock text = new()
            {
                Text = initialText,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = global::Avalonia.Media.TextTrimming.CharacterEllipsis,
                MaxWidth = maxTextWidth
            };
            UiTokens.Bind(text, TextBlock.ForegroundProperty, UiTokens.SkuaMutedTextBrush);

            StackPanel row = new()
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            UiTokens.Bind(row, StackPanel.SpacingProperty, UiTokens.UiGapXs);
            row.Children.Add(icon);
            row.Children.Add(text);

            Button btn = new() { Content = row };
            btn.Classes.Add("status-item");
            ToolTip.SetTip(btn, tooltip);
            return (btn, icon, text);
        }

        var (butlerBtn, butlerIcon, butlerText) = CreateItem(MaterialIconKind.AccountGroupOutline, "Butler", "ButlerV5 (click to open)", 140);
        butlerBtn.Click += (_, _) => TabIpc.SendCommand(ctx.ChildPid, "MENU|ButlerV5 Avalonia");
        ctx.ButlerButton = butlerBtn;
        ctx.ButlerIcon = butlerIcon;
        ctx.ButlerText = butlerText;

        var (watchdogBtn, watchdogIcon, watchdogText) = CreateItem(MaterialIconKind.ShieldCheckOutline, "Watchdog", "Watch Dog (click to open)", 130);
        watchdogBtn.Click += (_, _) => TabIpc.SendCommand(ctx.ChildPid, "MENU|Watch Dog");
        ctx.WatchdogButton = watchdogBtn;
        ctx.WatchdogIcon = watchdogIcon;
        ctx.WatchdogText = watchdogText;

        StackPanel leftPanel = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        UiTokens.Bind(leftPanel, StackPanel.SpacingProperty, UiTokens.UiGap2xs);
        leftPanel.Children.Add(butlerBtn);
        leftPanel.Children.Add(watchdogBtn);

        var (locBtn, locIcon, locText) = CreateItem(MaterialIconKind.MapMarkerOutline, "Client", "Active tab session (click to open Omniscope)", 200);
        locBtn.Click += (_, _) => TabIpc.SendCommand(ctx.ChildPid, "MENU|OMNISCOPE");
        ctx.LocationButton = locBtn;
        ctx.LocationIcon = locIcon;
        ctx.LocationText = locText;

        var (scriptBtn, scriptIcon, scriptText) = CreateItem(MaterialIconKind.ScriptTextOutline, "No script", "Script Loader (click to open)", 160);
        scriptBtn.Click += (_, _) => TabIpc.SendCommand(ctx.ChildPid, "OPEN_SCRIPT_LOADER");
        ctx.ScriptButton = scriptBtn;
        ctx.ScriptIcon = scriptIcon;
        ctx.ScriptText = scriptText;

        Ellipse autoDot = new()
        {
            Width = 6,
            Height = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        UiTokens.Bind(autoDot, Shape.FillProperty, UiTokens.SkuaSuccessBrush);
        TextBlock autoLabel = new() { Text = "Auto", VerticalAlignment = VerticalAlignment.Center };
        StackPanel autoRow = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        UiTokens.Bind(autoRow, StackPanel.SpacingProperty, UiTokens.UiGapXs);
        autoRow.Children.Add(autoDot);
        autoRow.Children.Add(autoLabel);
        Button autoBtn = new() { Content = autoRow, IsVisible = false };
        autoBtn.Classes.Add("status-item");
        ToolTip.SetTip(autoBtn, "Auto combat active (click to open Auto controls)");
        autoBtn.Click += (_, _) =>
        {
            if (ctx.QuickBar.AutoToggle != null)
                ctx.QuickBar.AutoToggle.IsChecked = !(ctx.QuickBar.AutoToggle.IsChecked == true);
        };
        ctx.AutoButton = autoBtn;

        var (headlessBtn, headlessIcon, headlessText) = CreateItem(MaterialIconKind.MonitorOff, "Auto Headless", "Toggle Auto Headless");
        headlessBtn.Click += (_, _) => ToggleAutoHeadless();
        ctx.HeadlessButton = headlessBtn;
        ctx.HeadlessIcon = headlessIcon;
        ctx.HeadlessText = headlessText;

        var (sizeBtn, _, sizeText) = CreateItem(MaterialIconKind.AspectRatio, ctx.StretchGame ? "Stretch" : "Fit", "Viewport: Stretch (fills window) — click for Fit");
        sizeBtn.Click += (_, _) =>
        {
            ctx.StretchGame = !ctx.StretchGame;
            ApplyDetachedViewportSizing(ctx);
            UpdateDetachedStatusBars(Ioc.Default.GetService<IScriptOption>()?.AutoHeadless == true);
        };
        ctx.GameSizeButton = sizeBtn;
        ctx.GameSizeText = sizeText;

        StackPanel rightPanel = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        UiTokens.Bind(rightPanel, StackPanel.SpacingProperty, UiTokens.UiGap2xs);
        rightPanel.Children.Add(locBtn);
        rightPanel.Children.Add(scriptBtn);
        rightPanel.Children.Add(autoBtn);
        rightPanel.Children.Add(headlessBtn);
        rightPanel.Children.Add(sizeBtn);

        Grid barGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(leftPanel, 0);
        Grid.SetColumn(rightPanel, 1);
        barGrid.Children.Add(leftPanel);
        barGrid.Children.Add(rightPanel);

        Border statusBar = new() { Child = barGrid };
        statusBar.Classes.Add("status-bar");
        ctx.StatusBarBorder = statusBar;
        return statusBar;
    }

    private void UpdateDetachedStatusBars(bool autoHeadless)
    {
        foreach (DetachedWindowContext ctx in _detachedContexts.Values)
        {
            TabInfo? tabInfo = _tabs.Values.FirstOrDefault(t => t.ChildHwnd == ctx.ChildHwnd);
            if (tabInfo == null)
                continue;

            string charKey = !string.IsNullOrWhiteSpace(tabInfo.LastCharacterName)
                ? tabInfo.LastCharacterName
                : (tabInfo.AccountOrderKey ?? string.Empty);

            if (ctx.ButlerButton != null && ctx.ButlerIcon != null && ctx.ButlerText != null)
                UpdateButlerStatusItemCore(tabInfo, charKey, ctx.ButlerButton, ctx.ButlerIcon, ctx.ButlerText);

            if (ctx.WatchdogButton != null && ctx.WatchdogIcon != null && ctx.WatchdogText != null)
                UpdateWatchdogStatusItemCore(tabInfo, charKey, ctx.WatchdogButton, ctx.WatchdogIcon, ctx.WatchdogText);

            if (ctx.LocationButton != null && ctx.LocationIcon != null && ctx.LocationText != null)
                UpdateLocationStatusItemCore(tabInfo, ctx.LocationButton, ctx.LocationIcon, ctx.LocationText);

            if (ctx.ScriptButton != null && ctx.ScriptIcon != null && ctx.ScriptText != null)
                UpdateScriptStatusItemCore(tabInfo, ctx.ScriptButton, ctx.ScriptIcon, ctx.ScriptText);

            if (ctx.AutoButton != null)
                ctx.AutoButton.IsVisible = ctx.QuickBar.AutoActiveDot?.IsVisible == true;

            if (ctx.HeadlessButton != null && ctx.HeadlessIcon != null && ctx.HeadlessText != null)
            {
                bool isHeadless = PredictEffectiveHeadless(ctx.ChildHwnd, false, isShownTile: true);
                UpdateHeadlessStatusItemCore(true, autoHeadless, isHeadless, ctx.HeadlessButton, ctx.HeadlessIcon, ctx.HeadlessText);
            }

            if (ctx.GameSizeButton != null && ctx.GameSizeText != null)
            {
                ctx.GameSizeText.Text = ctx.StretchGame ? "Stretch" : "Fit";
                ToolTip.SetTip(
                    ctx.GameSizeButton,
                    ctx.StretchGame
                        ? "Viewport: Stretch (fills window) — click for Fit"
                        : "Viewport: Fit (960:550) — click for Stretch");
            }
        }
    }

    private static void UpdateHeadlessStatusItemCore(
        bool isVisible,
        bool autoHeadless,
        bool isHeadless,
        Button btn,
        MaterialIcon icon,
        TextBlock text)
    {
        btn.IsVisible = isVisible;
        if (autoHeadless)
        {
            UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, UiTokens.SkuaWarningBrush);
            UiTokens.Bind(text, TextBlock.ForegroundProperty, UiTokens.SkuaTextBrush);
            text.Text = "Auto Headless";
            ToolTip.SetTip(btn, "Auto Headless enabled (follows AutoHeadless rules) — click to disable");
        }
        else if (isHeadless)
        {
            UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, UiTokens.SkuaWarningBrush);
            UiTokens.Bind(text, TextBlock.ForegroundProperty, UiTokens.SkuaTextBrush);
            text.Text = "Headless";
            ToolTip.SetTip(btn, "Headless active on current tab — click to enable Auto Headless");
        }
        else
        {
            UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
            UiTokens.Bind(text, TextBlock.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
            text.Text = "Auto Headless";
            ToolTip.SetTip(btn, "Auto Headless off — click to enable");
        }
    }

    private void UpdateLocationStatusItem(TabInfo? activeInfo)
        => UpdateLocationStatusItemCore(activeInfo, StatusLocationButton, StatusLocationIcon, StatusLocationText);

    private void UpdateLocationStatusItemCore(TabInfo? activeInfo, Button btn, MaterialIcon icon, TextBlock text)
    {
        if (activeInfo == null)
        {
            UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
            UiTokens.Bind(text, TextBlock.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
            text.Text = "No tab";
            ToolTip.SetTip(btn, "No active tab");
            return;
        }

        string title = activeInfo.TabItem != null ? GetTabTitle(activeInfo.TabItem) : string.Empty;
        if (string.IsNullOrWhiteSpace(title))
            title = $"Client {activeInfo.ClientNumber}";

        int pid = activeInfo.Process?.Id ?? 0;
        if (pid > 0 &&
            _armyJumpStore.LatestClientLocations.TryGetValue(pid, out ArmyClientLocation? loc) &&
            !string.IsNullOrWhiteSpace(loc.Map))
        {
            string mapDisplay = string.IsNullOrWhiteSpace(loc.Room)
                ? loc.Map
                : $"{loc.Map}-{loc.Room}";
            text.Text = $"{title} · {mapDisplay}";
            ToolTip.SetTip(btn, $"{title} in {mapDisplay} (click to open Omniscope)");
        }
        else
        {
            text.Text = title;
            ToolTip.SetTip(btn, $"{title} (click to open Omniscope)");
        }

        bool isStarting = !activeInfo.IsLoaded && string.IsNullOrWhiteSpace(activeInfo.LastCharacterName);
        UiTokens.Bind(
            icon,
            MaterialIcon.ForegroundProperty,
            isStarting ? UiTokens.SkuaWarningBrush : UiTokens.SkuaMutedTextBrush);
        UiTokens.Bind(
            text,
            TextBlock.ForegroundProperty,
            isStarting ? UiTokens.SkuaMutedTextBrush : UiTokens.SkuaTextBrush);
    }

    private void UpdateScriptStatusItem(TabInfo? activeInfo)
        => UpdateScriptStatusItemCore(activeInfo, StatusScriptButton, StatusScriptIcon, StatusScriptText);

    private void UpdateScriptStatusItemCore(TabInfo? activeInfo, Button btn, MaterialIcon icon, TextBlock text)
    {
        btn.IsVisible = activeInfo != null;
        int activePid = activeInfo?.Process?.Id ?? 0;
        if (activePid > 0 &&
            _childScriptStates.TryGetValue(activePid, out ChildScriptState script) &&
            !string.IsNullOrWhiteSpace(script.ScriptName))
        {
            if (script.IsPaused)
            {
                icon.Kind = MaterialIconKind.PauseCircleOutline;
                UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, UiTokens.SkuaWarningBrush);
                UiTokens.Bind(text, TextBlock.ForegroundProperty, UiTokens.SkuaTextBrush);
                text.Text = script.ScriptName;
                ToolTip.SetTip(btn, $"Paused: {script.ScriptName} (click to open Script Loader)");
            }
            else if (script.IsRunning)
            {
                icon.Kind = MaterialIconKind.PlayCircleOutline;
                UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, UiTokens.SkuaSuccessBrush);
                UiTokens.Bind(text, TextBlock.ForegroundProperty, UiTokens.SkuaTextBrush);
                text.Text = script.ScriptName;
                ToolTip.SetTip(btn, $"Running: {script.ScriptName} (click to open Script Loader)");
            }
            else
            {
                icon.Kind = MaterialIconKind.ScriptTextOutline;
                UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
                UiTokens.Bind(text, TextBlock.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
                text.Text = script.ScriptName;
                ToolTip.SetTip(btn, $"Loaded: {script.ScriptName} (click to open Script Loader)");
            }
        }
        else
        {
            icon.Kind = MaterialIconKind.ScriptTextOutline;
            UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
            UiTokens.Bind(text, TextBlock.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
            text.Text = "No script";
            ToolTip.SetTip(btn, "No script loaded (click to open Script Loader)");
        }
    }

    private void UpdateButlerStatusItem(TabInfo? activeInfo, string charKey)
        => UpdateButlerStatusItemCore(activeInfo, charKey, StatusButlerButton, StatusButlerIcon, StatusButlerText);

    private void UpdateButlerStatusItemCore(TabInfo? activeInfo, string charKey, Button btn, MaterialIcon icon, TextBlock text)
    {
        btn.IsVisible = activeInfo != null;
        if (activeInfo == null)
            return;

        string? master = null;
        if (!string.IsNullOrWhiteSpace(charKey))
            _follows.TryGetValue(charKey, out master);

        List<string> followers = string.IsNullOrWhiteSpace(charKey)
            ? new List<string>()
            : _follows
                .Where(kv => kv.Value.Equals(charKey, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToList();

        if (!string.IsNullOrWhiteSpace(master) && followers.Count > 0)
        {
            icon.Kind = MaterialIconKind.AccountArrowRightOutline;
            UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, UiTokens.SkuaTextBrush);
            UiTokens.Bind(text, TextBlock.ForegroundProperty, UiTokens.SkuaTextBrush);
            text.Text = $"Following {master} (+{followers.Count})";
            ToolTip.SetTip(btn, $"ButlerV5: Following {master} and leading {followers.Count} follower(s) — click to open");
        }
        else if (!string.IsNullOrWhiteSpace(master))
        {
            icon.Kind = MaterialIconKind.AccountArrowRightOutline;
            UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, UiTokens.SkuaTextBrush);
            UiTokens.Bind(text, TextBlock.ForegroundProperty, UiTokens.SkuaTextBrush);
            text.Text = $"Following {master}";
            ToolTip.SetTip(btn, $"ButlerV5: Following {master} — click to open");
        }
        else if (followers.Count > 0)
        {
            icon.Kind = MaterialIconKind.CrownOutline;
            UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, UiTokens.SkuaTextBrush);
            UiTokens.Bind(text, TextBlock.ForegroundProperty, UiTokens.SkuaTextBrush);
            text.Text = followers.Count == 1 ? "1 Follower" : $"{followers.Count} Followers";
            ToolTip.SetTip(btn, $"ButlerV5: Leading {followers.Count} follower(s): {string.Join(", ", followers)} — click to open");
        }
        else if (_follows.Count > 0)
        {
            icon.Kind = MaterialIconKind.AccountGroupOutline;
            UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
            UiTokens.Bind(text, TextBlock.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
            text.Text = $"{_follows.Count} Following";
            ToolTip.SetTip(btn, $"ButlerV5: {_follows.Count} active follow(s) across {_activeFollowGroupCount} group(s) — click to open");
        }
        else
        {
            icon.Kind = MaterialIconKind.AccountGroupOutline;
            UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
            UiTokens.Bind(text, TextBlock.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
            text.Text = "Butler";
            ToolTip.SetTip(btn, "ButlerV5: No active follows — click to open");
        }
    }

    private void UpdateWatchdogStatusItem(TabInfo? activeInfo, string charKey)
        => UpdateWatchdogStatusItemCore(activeInfo, charKey, StatusWatchdogButton, StatusWatchdogIcon, StatusWatchdogText);

    private void UpdateWatchdogStatusItemCore(TabInfo? activeInfo, string charKey, Button btn, MaterialIcon icon, TextBlock text)
    {
        btn.IsVisible = activeInfo != null;
        if (activeInfo == null)
            return;

        ScriptOption.RescueAccountInfo? activeDog = null;
        if (!string.IsNullOrWhiteSpace(charKey))
            _watchdogAccounts.TryGetValue(charKey, out activeDog);

        ScriptOption.RescueAccountInfo? callingAccount = _watchdogAccounts.Values
            .FirstOrDefault(a => string.Equals(a.Status, "Help requested", StringComparison.OrdinalIgnoreCase));

        if (callingAccount != null)
        {
            icon.Kind = MaterialIconKind.ShieldAlertOutline;
            UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, UiTokens.SkuaDangerBrush);
            UiTokens.Bind(text, TextBlock.ForegroundProperty, UiTokens.SkuaTextBrush);
            text.Text = "Help!";
            ToolTip.SetTip(btn, $"Watch Dog: {callingAccount.Account} requested help in {callingAccount.Map} — click to open");
        }
        else if (activeDog != null &&
                 !string.IsNullOrWhiteSpace(activeDog.Status) &&
                 !activeDog.Status.Equals("Rescue Allies is off", StringComparison.OrdinalIgnoreCase) &&
                 !activeDog.Status.Equals("Idle", StringComparison.OrdinalIgnoreCase) &&
                 !activeDog.Status.Equals("Waiting", StringComparison.OrdinalIgnoreCase) &&
                 !activeDog.Status.StartsWith("Ready", StringComparison.OrdinalIgnoreCase))
        {
            icon.Kind = MaterialIconKind.ShieldAlertOutline;
            UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, UiTokens.SkuaWarningBrush);
            UiTokens.Bind(text, TextBlock.ForegroundProperty, UiTokens.SkuaTextBrush);
            text.Text = activeDog.Status;
            ToolTip.SetTip(btn, $"Watch Dog ({activeDog.Account}): {activeDog.Status} — click to open");
        }
        else if (_watchdogEnabled)
        {
            icon.Kind = MaterialIconKind.ShieldCheckOutline;
            UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
            UiTokens.Bind(text, TextBlock.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
            text.Text = "Watchdog";
            ToolTip.SetTip(btn, activeDog != null
                ? $"Watch Dog: {activeDog.Status} ({_watchdogAccounts.Count} account(s)) — click to open"
                : "Watch Dog: Rescue Allies enabled — click to open");
        }
        else
        {
            icon.Kind = MaterialIconKind.ShieldOffOutline;
            UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
            UiTokens.Bind(text, TextBlock.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
            text.Text = "Watchdog";
            ToolTip.SetTip(btn, "Watch Dog: Rescue Allies is off — click to open");
        }
    }

    private void StatusLocationButton_Click(object? sender, RoutedEventArgs e)
        => SendActiveChildCommand("MENU|OMNISCOPE");

    private void StatusScriptButton_Click(object? sender, RoutedEventArgs e)
        => SendActiveChildCommand("OPEN_SCRIPT_LOADER");

    private void StatusButlerButton_Click(object? sender, RoutedEventArgs e)
        => SendActiveChildCommand("MENU|ButlerV5 Avalonia");

    private void StatusWatchdogButton_Click(object? sender, RoutedEventArgs e)
        => SendActiveChildCommand("MENU|Watch Dog");

    private void StatusAutoButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_mainQuickBar.AutoToggle != null)
            _mainQuickBar.AutoToggle.IsChecked = !(_mainQuickBar.AutoToggle.IsChecked == true);
    }

    private void StatusHeadlessButton_Click(object? sender, RoutedEventArgs e)
        => ToggleAutoHeadless();

    private void StatusGridLayoutButton_Click(object? sender, RoutedEventArgs e)
        => CycleGridLayout();

    private void StatusGameSizeButton_Click(object? sender, RoutedEventArgs e)
        => CycleGameSize();
}
