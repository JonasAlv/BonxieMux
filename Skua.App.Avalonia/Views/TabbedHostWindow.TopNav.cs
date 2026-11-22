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
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;
using Skua.Shared.Avalonia.Controls.Shell;
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
        public Border? AutoRunningBadge;
        public Button? AutoAttackBtn;
        public Button? AutoHuntBtn;
        public Button? AutoStopBtn;
        public ComboBox? AutoClassBox;
        public ComboBox? AutoModeBox;
        public TextBox? AutoMapIdsBox;
        public ComboBox? JumpCellBox;
        public ComboBox? JumpPadBox;
    }

    private readonly QuickBarControls _mainQuickBar = new();
    private readonly Dictionary<int, QuickBarControls> _detachedQuickBars = new();
    private bool _suppressPopupSync;
    private Menu? _topMenuBar;
    private MenuItem? _overflowMenuItem;
    private readonly List<(TopMenuSpec Spec, MenuItem Item, double Width)> _topMenuEntries = new();
    private int _lastOverflowShownCount = -2;
    private bool _overflowHooked;

    private sealed record TopMenuSpec(string Header, Action? DirectClick, string[] SubLabels, bool IsPlugins = false);

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
            new("Bank", () => SendActiveChildCommand("BANK"), []),
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
            Header = CreateNavIcon(MaterialIconKind.DotsHorizontal, 16, "SkuaTextBrush"),
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
            item.Click += (_, _) => direct();
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
            16,
            "SkuaTextBrush");
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
            Content = CreateNavIcon(MaterialIconKind.Apps, 16, "SkuaMutedTextBrush")
        };
        botWinBtn.Classes.Add("activity-item");
        ToolTip.SetTip(botWinBtn, "Show All Bot Windows & Tools");
        botWinBtn.Click += (_, _) => SendActiveChildCommand("BOT_WINDOW");
        ActivityBotWinHost.Content = botWinBtn;

        Button settingsBtn = new()
        {
            Content = CreateNavIcon(MaterialIconKind.CogOutline, 16, "SkuaMutedTextBrush")
        };
        settingsBtn.Classes.Add("activity-item");
        ToolTip.SetTip(settingsBtn, "Application Themes & Appearance");
        settingsBtn.Click += (_, _) => SendActiveChildCommand("MENU|Application Themes");
        ActivitySettingsHost.Content = settingsBtn;
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
                        Skua.App.Avalonia.Services.TabIpc.SendCommand(targetPid, "MENU|" + targetHeader);
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
                    Skua.App.Avalonia.Services.TabIpc.SendCommand(targetPid, "MENU|" + label);
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
            Background = Brush.Parse("#10B981"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 4, 0),
            IsVisible = false
        };
        Panel autoContent = new()
        {
            Width = 28,
            Height = 28,
            Children =
            {
                CreateNavIcon(MaterialIconKind.RobotOutline, 16, "SkuaMutedTextBrush"),
                target.AutoActiveDot
            }
        };
        ToggleButton toggle = new()
        {
            Content = autoContent
        };
        toggle.Classes.Add("activity-item");
        ToolTip.SetTip(toggle, "Automated Combat & Hunting Controls");
        target.AutoToggle = toggle;

        target.AutoRunningBadge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2),
            Background = Brush.Parse("#064E3B"),
            BorderBrush = Brush.Parse("#059669"),
            BorderThickness = new Thickness(1),
            IsVisible = false,
            Child = new TextBlock { Text = "RUNNING", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#34D399") }
        };
        Grid.SetColumn(target.AutoRunningBadge, 1);

        Grid headerGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), VerticalAlignment = VerticalAlignment.Center };
        headerGrid.Children.Add(CreatePopupHeader("Combat Automation"));
        headerGrid.Children.Add(target.AutoRunningBadge);

        target.AutoAttackBtn = CreatePopupButton("Auto Attack", true, "skua-primary", () =>
        {
            send("AUTO_ATTACK");
            if (target.AutoToggle != null) target.AutoToggle.IsChecked = false;
        });

        target.AutoHuntBtn = CreatePopupButton("Auto Hunt", true, null, () =>
        {
            send("AUTO_HUNT");
            if (target.AutoToggle != null) target.AutoToggle.IsChecked = false;
        });
        Grid.SetColumn(target.AutoHuntBtn, 1);

        target.AutoStopBtn = CreatePopupButton("Stop Automation", true, "skua-danger", () =>
        {
            send("AUTO_STOP");
            if (target.AutoToggle != null) target.AutoToggle.IsChecked = false;
        });
        target.AutoStopBtn.IsVisible = false;
        Grid.SetColumnSpan(target.AutoStopBtn, 2);

        Grid actionsGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        actionsGrid.Children.Add(target.AutoAttackBtn);
        actionsGrid.Children.Add(target.AutoHuntBtn);
        actionsGrid.Children.Add(target.AutoStopBtn);

        target.AutoClassBox = new ComboBox { Height = 34, MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Select Class..." };
        target.AutoClassBox.SelectionChanged += (_, _) =>
        {
            if (!_suppressPopupSync && target.AutoClassBox.SelectedItem is string cls)
                send("AUTO_CLASS|" + cls);
        };

        target.AutoModeBox = new ComboBox { Height = 34, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Mode..." };
        Grid.SetColumn(target.AutoModeBox, 1);
        target.AutoModeBox.SelectionChanged += (_, _) =>
        {
            if (!_suppressPopupSync && target.AutoModeBox.SelectedItem is string mode)
                send("AUTO_MODE|" + mode);
        };

        Grid classModeGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        classModeGrid.Children.Add(target.AutoClassBox);
        classModeGrid.Children.Add(target.AutoModeBox);

        target.AutoMapIdsBox = new TextBox { Height = 32, FontSize = 12, Watermark = "Manual MapIDs (optional, e.g. 1, 2)" };
        target.AutoMapIdsBox.LostFocus += (_, _) =>
        {
            if (!_suppressPopupSync)
                send("AUTO_MAPS|" + (target.AutoMapIdsBox.Text ?? string.Empty));
        };

        Popup popup = CreatePopupCard(toggle, 360, new StackPanel
        {
            Spacing = 10,
            Children = { headerGrid, CreatePopupSeparator(), actionsGrid, classModeGrid, target.AutoMapIdsBox }
        }, placement);

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
            Content = CreateNavIcon(MaterialIconKind.RunFast, 16, "SkuaMutedTextBrush")
        };
        toggle.Classes.Add("activity-item");
        ToolTip.SetTip(toggle, "Map & Cell Fast Jump Controls");
        target.JumpToggle = toggle;

        StackPanel header = CreatePopupHeader("Fast Map Jump");

        target.JumpCellBox = new ComboBox { Height = 34, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Select Cell..." };
        target.JumpPadBox = new ComboBox { Height = 34, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Select Pad..." };
        Grid.SetColumn(target.JumpPadBox, 1);

        target.JumpCellBox.SelectionChanged += (_, _) =>
        {
            if (_suppressPopupSync || target.JumpCellBox.SelectedItem is not string cell || string.IsNullOrEmpty(cell))
                return;
            if (cell == "Enter")
                target.JumpPadBox.SelectedItem = "Spawn";
            else if (target.JumpPadBox.SelectedItem is not string p || string.IsNullOrEmpty(p))
                target.JumpPadBox.SelectedItem = "Left";
            string pad = target.JumpPadBox.SelectedItem as string ?? "Spawn";
            send($"JUMP_TO|{cell}|{pad}");
        };

        Grid selectGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        selectGrid.Children.Add(target.JumpCellBox);
        selectGrid.Children.Add(target.JumpPadBox);

        Button currentBtn = CreatePopupButton("Current", false, null, () => send("JUMP_CURRENT"));
        Button jumpBtn = CreatePopupButton("Jump", true, "skua-primary", () =>
        {
            string cell = target.JumpCellBox?.SelectedItem as string ?? string.Empty;
            string pad = target.JumpPadBox?.SelectedItem as string ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(cell) && !string.IsNullOrWhiteSpace(pad))
                send($"JUMP_TO|{cell}|{pad}");
        });
        Grid.SetColumn(jumpBtn, 1);

        Grid actionsGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        actionsGrid.Children.Add(currentBtn);
        actionsGrid.Children.Add(jumpBtn);

        Popup popup = CreatePopupCard(toggle, 320, new StackPanel
        {
            Spacing = 10,
            Children = { header, CreatePopupSeparator(), selectGrid, actionsGrid }
        }, placement);

        BindPopupToggle(toggle, popup, () => target.AutoToggle, "GET_JUMP_STATE", send);
        return (toggle, popup);
    }

    private static StackPanel CreatePopupHeader(string title) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            CreateNavTitle(title)
        }
    };

    private static Button CreatePopupButton(string text, bool bold, string? accentClass, Action onClick)
    {
        Button btn = new()
        {
            Height = 34,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = CreatePopupButtonContent(text, bold)
        };
        btn.Classes.Add("skua-button");
        if (!string.IsNullOrEmpty(accentClass))
            btn.Classes.Add(accentClass);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static Border CreatePopupSeparator()
    {
        Border sep = new() { Height = 1 };
        sep[!Border.BackgroundProperty] = new DynamicResourceExtension("SkuaBorderBrush");
        return sep;
    }

    private static Popup CreatePopupCard(Control target, double width, Control body, PlacementMode placement = PlacementMode.Bottom)
    {
        Border card = new()
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            MinWidth = width,
            Width = width,
            Child = body
        };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("SkuaSurfaceBrush");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("SkuaBorderBrush");

        return new Popup
        {
            Placement = placement,
            IsLightDismissEnabled = true,
            PlacementTarget = target,
            Child = new TopbarPopupPanel { Content = card }
        };
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

    private static TextBlock CreateNavTitle(string text)
    {
        TextBlock tb = new() { Text = text, FontWeight = FontWeight.Bold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        tb[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SkuaTextBrush");
        return tb;
    }

    private static TextBlock CreatePopupButtonContent(string text, bool bold) => new()
    {
        Text = text,
        FontWeight = bold ? FontWeight.Bold : FontWeight.SemiBold,
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };

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
            _suppressPopupSync = true;
            bool isRunning = obj.Value<bool>("isRunning");
            List<string> classes = obj["classes"]?.ToObject<List<string>>() ?? new List<string>();
            string? selectedClass = obj.Value<string?>("selectedClass");
            List<string> modes = obj["modes"]?.ToObject<List<string>>() ?? new List<string>();
            string? selectedMode = obj.Value<string?>("selectedMode");
            string manualMapIds = obj.Value<string?>("manualMapIds") ?? string.Empty;

            void ApplyTo(QuickBarControls target)
            {
                if (target.AutoActiveDot != null) target.AutoActiveDot.IsVisible = isRunning;
                if (target.AutoRunningBadge != null) target.AutoRunningBadge.IsVisible = isRunning;
                if (target.AutoAttackBtn != null) target.AutoAttackBtn.IsVisible = !isRunning;
                if (target.AutoHuntBtn != null) target.AutoHuntBtn.IsVisible = !isRunning;
                if (target.AutoStopBtn != null) target.AutoStopBtn.IsVisible = isRunning;

                if (target.AutoClassBox != null)
                {
                    target.AutoClassBox.ItemsSource = classes;
                    target.AutoClassBox.SelectedItem = selectedClass;
                }
                if (target.AutoModeBox != null)
                {
                    target.AutoModeBox.ItemsSource = modes;
                    target.AutoModeBox.SelectedItem = selectedMode;
                }
                if (target.AutoMapIdsBox != null)
                {
                    target.AutoMapIdsBox.Text = manualMapIds;
                }
            }

            if (isSelected)
                ApplyTo(_mainQuickBar);
            if (detachedBar != null)
                ApplyTo(detachedBar);
        }
        catch { }
        finally
        {
            _suppressPopupSync = false;
        }
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
            _suppressPopupSync = true;
            List<string> cells = obj["cells"]?.ToObject<List<string>>() ?? new List<string>();
            string? selectedCell = obj.Value<string?>("selectedCell");
            List<string> pads = obj["pads"]?.ToObject<List<string>>() ?? new List<string>();
            string? selectedPad = obj.Value<string?>("selectedPad");

            void ApplyTo(QuickBarControls target)
            {
                if (target.JumpCellBox != null)
                {
                    target.JumpCellBox.ItemsSource = cells;
                    target.JumpCellBox.SelectedItem = selectedCell;
                }
                if (target.JumpPadBox != null)
                {
                    target.JumpPadBox.ItemsSource = pads;
                    target.JumpPadBox.SelectedItem = selectedPad;
                }
            }

            if (isSelected)
                ApplyTo(_mainQuickBar);
            if (detachedBar != null)
                ApplyTo(detachedBar);
        }
        catch { }
        finally
        {
            _suppressPopupSync = false;
        }
    }
}
