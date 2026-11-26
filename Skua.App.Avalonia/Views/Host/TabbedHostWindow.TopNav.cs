using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using CommunityToolkit.Mvvm.DependencyInjection;
using Material.Icons;
using Material.Icons.Avalonia;
using Newtonsoft.Json.Linq;
using Skua.App.Avalonia.Services;
using Skua.Backend.Ipc;
using Skua.App.Avalonia.Views.Popups;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;
using Skua.Shared.Avalonia.Controls.Shell;
using Skua.Shared.Avalonia.Theming;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Skua.App.Avalonia.Views;

public partial class TabbedHostWindow
{
    private sealed class QuickBarControls
    {
        public ToggleButton? AutoToggle;
        public ToggleButton? JumpToggle;
        public Border? AutoActiveDot;
        public AutoQuickPopupView? AutoView;
        public JumpQuickPopupView? JumpView;
    }

    private readonly QuickBarControls _mainQuickBar = new();
    private readonly Dictionary<int, QuickBarControls> _detachedQuickBars = new();
    private Menu? _topMenuBar;
    private MenuItem? _overflowMenuItem;
    private readonly List<(TopMenuSpec Spec, MenuItem Item, double Width)> _topMenuEntries = new();
    private int _lastOverflowShownCount = -2;
    private bool _overflowHooked;

    private sealed record TopMenuSpec(string Header, Action<int>? DirectClick, string[] SubLabels, bool IsPlugins = false);

    private Menu BuildTopMenuBar()
    {
        Menu menu = new() { VerticalAlignment = VerticalAlignment.Center };
        menu.Classes.Add("top-toolbar");
        _topMenuBar = menu;
        _topMenuEntries.Clear();
        _lastOverflowShownCount = -2;

        TopMenuSpec[] specs =
        [
            new("Scripts", null, ["Script Loader", "Scheduler"]),
            new("Options", null, ["Game", "Watch Dog", "Application", "CoreBots", "Application Themes", "HotKeys"]),
            new("Tools & Helpers", null, ["Loadouts Manager", "Fast Travel", "Loader", "Grabber", "Current Drops", "Junk Items", "Ultra Wiki", "Calendar"]),
            new("Combat", null, ["Skills", "Stats", "Runtime"]),
            new("Bank", pid => { if (pid > 0) TabIpc.SendCommand(pid, "BANK"); else SendActiveChildCommand("BANK"); }, []),
            new("Diagnostics", null, ["Logs", "Console", "Spammer", "Logger", "Interceptor"]),
            new("Plugins", null, ["View Plugins", "ButlerV5 Avalonia", "Daily Tracker", "Cosmetics", "Perf Recorder"], IsPlugins: true)
        ];

        foreach (TopMenuSpec spec in specs)
        {
            MenuItem item = BuildTopMenuItemFromSpec(spec);
            double estW = Math.Max(48.0, spec.Header.Length * 7.2 + 20.0);
            int idx = _topMenuEntries.Count;
            _topMenuEntries.Add((spec, item, estW));
            item.SizeChanged += (_, e) =>
            {
                if (e.NewSize.Width > 1 && idx < _topMenuEntries.Count)
                {
                    var cur = _topMenuEntries[idx];
                    if (Math.Abs(cur.Width - e.NewSize.Width) > 0.5)
                    {
                        _topMenuEntries[idx] = (cur.Spec, cur.Item, e.NewSize.Width);
                        _lastOverflowShownCount = -2;
                        UpdateTopMenuOverflow();
                    }
                }
            };
            menu.Items.Add(item);
        }

        _overflowMenuItem = new MenuItem
        {
            Header = CreateNavIcon(MaterialIconKind.DotsHorizontal, UiTokens.UiIconMd, UiTokens.SkuaTextBrush),
            Padding = new Thickness(7, 0),
            IsVisible = false
        };
        _overflowMenuItem.Classes.Add("top-nav-item");
        menu.Items.Add(_overflowMenuItem);

        if (!_overflowHooked)
        {
            _overflowHooked = true;
            MainTitleBar.TitleLayoutChanged += (_, _) => UpdateTopMenuOverflow();
        }
        global::Avalonia.Threading.Dispatcher.UIThread.Post(
            UpdateTopMenuOverflow,
            global::Avalonia.Threading.DispatcherPriority.Render);

        return menu;
    }

    private MenuItem BuildTopMenuItemFromSpec(TopMenuSpec spec, int targetPid = 0)
    {
        if (spec.IsPlugins)
            return BuildPluginsMenuItem(targetPid);

        if (spec.DirectClick is { } direct)
        {
            MenuItem item = CreateTopMenuItem(spec.Header);
            item.Click += (_, _) => direct(targetPid);
            return item;
        }

        return CreateMenuGroup(spec.Header, targetPid, spec.SubLabels);
    }

    private void UpdateTopMenuOverflow()
    {
        if (_topMenuBar is null || _overflowMenuItem is null || _topMenuEntries.Count == 0)
            return;

        double titleBarW = MainTitleBar.Bounds.Width;
        if (titleBarW <= 1)
            return;

        // Reserve space for the 36px left app icon and centered title so menus never collide with it.
        double titleLeft = MainTitleBar.CenteredTitleLeftEdge;
        double rightControlsEdge = Math.Max(36.0, titleBarW - 140.0);
        double available = Math.Max(0.0, Math.Min(titleLeft, rightControlsEdge) - 36.0 - 16.0);

        const double overflowBtnWidth = 34.0;
        double currentSize = 0.0;
        int numShown = 0;

        foreach (var (_, _, w) in _topMenuEntries)
        {
            if (currentSize + w <= available)
            {
                currentSize += w;
                numShown++;
            }
            else
            {
                break;
            }
        }

        if (numShown < _topMenuEntries.Count)
        {
            while (numShown > 0 && currentSize + overflowBtnWidth > available)
            {
                numShown--;
                currentSize -= _topMenuEntries[numShown].Width;
            }
        }

        if (numShown == _lastOverflowShownCount)
            return;
        _lastOverflowShownCount = numShown;

        for (int i = 0; i < _topMenuEntries.Count; i++)
            _topMenuEntries[i].Item.IsVisible = i < numShown;

        if (numShown == _topMenuEntries.Count)
        {
            _overflowMenuItem.IsVisible = false;
            _overflowMenuItem.Items.Clear();
            return;
        }

        bool compactHamburger = numShown == 0;
        _overflowMenuItem.Header = CreateNavIcon(
            compactHamburger ? MaterialIconKind.Menu : MaterialIconKind.DotsHorizontal,
            UiTokens.UiIconMd,
            UiTokens.SkuaTextBrush);
        ToolTip.SetTip(_overflowMenuItem, compactHamburger ? "Application Menu" : "More");

        _overflowMenuItem.Items.Clear();
        for (int i = numShown; i < _topMenuEntries.Count; i++)
        {
            TopMenuSpec spec = _topMenuEntries[i].Spec;
            MenuItem sub = BuildTopMenuItemFromSpec(spec);
            sub.Classes.Remove("top-nav-item");
            _overflowMenuItem.Items.Add(sub);
        }
        _overflowMenuItem.IsVisible = true;
    }

    private void BuildActivityBarControls()
    {
        var (autoToggle, autoPopup) = BuildAutoControls(_mainQuickBar, PlacementMode.RightEdgeAlignedTop);
        var (jumpToggle, jumpPopup) = BuildJumpControls(_mainQuickBar, PlacementMode.RightEdgeAlignedTop);

        ActivityAutoHost.Content = new Panel { Children = { autoToggle, autoPopup } };
        ActivityJumpHost.Content = new Panel { Children = { jumpToggle, jumpPopup } };

        Button botWinBtn = new()
        {
            Content = CreateNavIcon(MaterialIconKind.Apps, UiTokens.UiIconMd, UiTokens.SkuaMutedTextBrush)
        };
        botWinBtn.Classes.Add("activity-item");
        ToolTip.SetTip(botWinBtn, "Show All Bot Windows & Tools");
        botWinBtn.Click += (_, _) => SendActiveChildCommand("BOT_WINDOW");
        ActivityBotWinHost.Content = botWinBtn;

        Button searchScriptsBtn = new()
        {
            Content = CreateNavIcon(MaterialIconKind.SourceBranch, UiTokens.UiIconMd, UiTokens.SkuaMutedTextBrush)
        };
        searchScriptsBtn.Classes.Add("activity-item");
        ToolTip.SetTip(searchScriptsBtn, "Search Scripts");
        searchScriptsBtn.Click += (_, _) => SendActiveChildCommand("MENU|Script Repo");
        ActivitySearchScriptsHost.Content = searchScriptsBtn;

        Button settingsBtn = new()
        {
            Content = CreateNavIcon(MaterialIconKind.CogOutline, UiTokens.UiIconMd, UiTokens.SkuaMutedTextBrush)
        };
        settingsBtn.Classes.Add("activity-item");
        ToolTip.SetTip(settingsBtn, "Application Themes & Appearance");
        settingsBtn.Click += (_, _) => SendActiveChildCommand("MENU|Application Themes");
        ActivitySettingsHost.Content = settingsBtn;
    }

    private static MaterialIcon CreateNavIcon(MaterialIconKind kind, string sizeToken, string brushKey)
    {
        MaterialIcon icon = new()
        {
            Kind = kind,
            VerticalAlignment = VerticalAlignment.Center
        };
        UiTokens.Bind(icon, MaterialIcon.WidthProperty, sizeToken);
        UiTokens.Bind(icon, MaterialIcon.HeightProperty, sizeToken);
        UiTokens.Bind(icon, MaterialIcon.ForegroundProperty, brushKey);
        return icon;
    }

    private static MenuItem CreateTopMenuItem(string header)
    {
        MenuItem item = new()
        {
            Header = header
        };
        item.Classes.Add("top-nav-item");
        return item;
    }

    private MenuItem BuildPluginsMenuItem(int targetPid = 0)
    {
        MenuItem parent = CreateMenuGroup(
            "Plugins",
            targetPid,
            "View Plugins",
            "ButlerV5 Avalonia",
            "Daily Tracker",
            "Cosmetics",
            "Perf Recorder");

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
                    Header = targetHeader
                };
                extra.Click += (_, _) =>
                {
                    if (targetPid > 0)
                        TabIpc.SendCommand(targetPid, "MENU|" + targetHeader);
                    else
                        SendActiveChildCommand("MENU|" + targetHeader);
                };
                parent.Items.Add(extra);
            }
        };

        return parent;
    }

    private MenuItem CreateMenuGroup(string header, params string[] subItems)
        => CreateMenuGroup(header, 0, subItems);

    private MenuItem CreateMenuGroup(string header, int targetPid, params string[] subItems)
    {
        MenuItem parent = CreateTopMenuItem(header);
        foreach (string label in subItems)
        {
            MenuItem sub = new()
            {
                Header = label
            };
            sub.Click += (_, _) =>
            {
                if (targetPid > 0)
                    TabIpc.SendCommand(targetPid, "MENU|" + label);
                else
                    SendActiveChildCommand("MENU|" + label);
            };
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

    private (ToggleButton, Popup) BuildAutoControls(QuickBarControls target, PlacementMode placement = PlacementMode.Bottom, int targetPid = 0)
    {
        Action<string> send = cmd =>
        {
            if (targetPid > 0)
                TabIpc.SendCommand(targetPid, cmd);
            else
                SendActiveChildCommand(cmd);
        };

        target.AutoActiveDot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 4, 0),
            IsVisible = false
        };
        UiTokens.Bind(target.AutoActiveDot, Border.BackgroundProperty, UiTokens.SkuaSuccessBrush);
        Panel autoContent = new()
        {
            Children =
            {
                CreateNavIcon(MaterialIconKind.RobotOutline, UiTokens.UiIconMd, UiTokens.SkuaMutedTextBrush),
                target.AutoActiveDot
            }
        };
        UiTokens.Bind(autoContent, Layoutable.WidthProperty, UiTokens.UiControlHeight);
        UiTokens.Bind(autoContent, Layoutable.HeightProperty, UiTokens.UiControlHeight);
        ToggleButton toggle = new()
        {
            Content = autoContent
        };
        toggle.Classes.Add("activity-item");
        ToolTip.SetTip(toggle, "Automated Combat & Hunting Controls");
        target.AutoToggle = toggle;

        AutoQuickPopupView view = new();
        view.AutoAttackClicked += () => { send("AUTO_ATTACK"); if (target.AutoToggle != null) target.AutoToggle.IsChecked = false; };
        view.AutoHuntClicked += () => { send("AUTO_HUNT"); if (target.AutoToggle != null) target.AutoToggle.IsChecked = false; };
        view.AutoStopClicked += () => { send("AUTO_STOP"); if (target.AutoToggle != null) target.AutoToggle.IsChecked = false; };
        view.ClassSelected += cls => send("AUTO_CLASS|" + cls);
        view.ModeSelected += mode => send("AUTO_MODE|" + mode);
        view.ManualMapIdsCommitted += maps => send("AUTO_MAPS|" + maps);
        target.AutoView = view;

        Popup popup = new()
        {
            Placement = placement,
            IsLightDismissEnabled = true,
            PlacementTarget = toggle,
            Child = new TopbarPopupPanel { Content = view }
        };

        BindPopupToggle(toggle, popup, () => target.JumpToggle, "GET_AUTO_STATE", send);
        return (toggle, popup);
    }

    private (ToggleButton, Popup) BuildJumpControls(QuickBarControls target, PlacementMode placement = PlacementMode.Bottom, int targetPid = 0)
    {
        Action<string> send = cmd =>
        {
            if (targetPid > 0)
                TabIpc.SendCommand(targetPid, cmd);
            else
                SendActiveChildCommand(cmd);
        };

        ToggleButton toggle = new()
        {
            Content = CreateNavIcon(MaterialIconKind.RunFast, UiTokens.UiIconMd, UiTokens.SkuaMutedTextBrush)
        };
        toggle.Classes.Add("activity-item");
        ToolTip.SetTip(toggle, "Map & Cell Fast Jump Controls");
        target.JumpToggle = toggle;

        JumpQuickPopupView view = new();
        view.CurrentClicked += () => send("JUMP_CURRENT");
        view.JumpRequested += (cell, pad) => send($"JUMP_TO|{cell}|{pad}");
        target.JumpView = view;

        Popup popup = new()
        {
            Placement = placement,
            IsLightDismissEnabled = true,
            PlacementTarget = toggle,
            Child = new TopbarPopupPanel { Content = view }
        };

        BindPopupToggle(toggle, popup, () => target.AutoToggle, "GET_JUMP_STATE", send);
        return (toggle, popup);
    }

    private static void BindPopupToggle(ToggleButton toggle, Popup popup, Func<ToggleButton?> otherToggle, string stateCommand, Action<string> send)
    {
        toggle.IsCheckedChanged += (_, _) =>
        {
            bool open = toggle.IsChecked == true;
            if (open && otherToggle() is { } other)
                other.IsChecked = false;
            popup.IsOpen = open;
            if (open)
                send(stateCommand);
        };
        popup.Closed += (_, _) => toggle.IsChecked = false;
    }

    private void ApplyChildAutoState(int childPid, string json)
    {
        bool isSelected = InstancesTabControl.SelectedItem is TabItem sel &&
            _tabs.TryGetValue(sel, out TabInfo? info) &&
            info.Process?.Id == childPid;

        _detachedQuickBars.TryGetValue(childPid, out QuickBarControls? detachedBar);
        if (!isSelected && detachedBar == null)
            return;

        try
        {
            JObject obj = JObject.Parse(json);
            bool isRunning = obj.Value<bool>("isRunning");
            List<string> classes = obj["classes"]?.ToObject<List<string>>() ?? new List<string>();
            string? selectedClass = obj.Value<string?>("selectedClass");
            List<string> modes = obj["modes"]?.ToObject<List<string>>() ?? new List<string>();
            string? selectedMode = obj.Value<string?>("selectedMode");
            string manualMapIds = obj.Value<string?>("manualMapIds") ?? string.Empty;

            void ApplyTo(QuickBarControls target)
            {
                if (target.AutoActiveDot != null) target.AutoActiveDot.IsVisible = isRunning;
                target.AutoView?.ApplyState(isRunning, classes, selectedClass, modes, selectedMode, manualMapIds);
            }

            if (isSelected)
            {
                ApplyTo(_mainQuickBar);
                _activeTabAutoRunning = isRunning;
                UpdateStatusBar();
            }
            if (detachedBar != null)
            {
                ApplyTo(detachedBar);
                UpdateStatusBar();
            }
        }
        catch { }
    }

    private void ApplyChildJumpState(int childPid, string json)
    {
        bool isSelected = InstancesTabControl.SelectedItem is TabItem sel &&
            _tabs.TryGetValue(sel, out TabInfo? info) &&
            info.Process?.Id == childPid;

        _detachedQuickBars.TryGetValue(childPid, out QuickBarControls? detachedBar);
        if (!isSelected && detachedBar == null)
            return;

        try
        {
            JObject obj = JObject.Parse(json);
            List<string> cells = obj["cells"]?.ToObject<List<string>>() ?? new List<string>();
            string? selectedCell = obj.Value<string?>("selectedCell");
            List<string> pads = obj["pads"]?.ToObject<List<string>>() ?? new List<string>();
            string? selectedPad = obj.Value<string?>("selectedPad");

            if (isSelected)
                _mainQuickBar.JumpView?.ApplyState(cells, selectedCell, pads, selectedPad);
            if (detachedBar != null)
                detachedBar.JumpView?.ApplyState(cells, selectedCell, pads, selectedPad);
        }
        catch { }
    }
}
