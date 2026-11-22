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
    private ToggleButton? _autoToggle;
    private ToggleButton? _jumpToggle;
    private Border? _autoActiveDot;
    private Border? _autoRunningBadge;
    private Button? _autoAttackBtn;
    private Button? _autoHuntBtn;
    private Button? _autoStopBtn;
    private ComboBox? _autoClassBox;
    private ComboBox? _autoModeBox;
    private TextBox? _autoMapIdsBox;
    private ComboBox? _jumpCellBox;
    private ComboBox? _jumpPadBox;
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

    private MenuItem BuildTopMenuItemFromSpec(TopMenuSpec spec)
    {
        if (spec.IsPlugins)
            return BuildPluginsMenuItem();

        if (spec.DirectClick is { } direct)
        {
            MenuItem item = CreateTopMenuItem(spec.Header);
            item.Click += (_, _) => direct();
            return item;
        }

        return CreateMenuGroup(spec.Header, spec.SubLabels);
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
        var (autoToggle, autoPopup) = BuildAutoControls(PlacementMode.RightEdgeAlignedTop);
        var (jumpToggle, jumpPopup) = BuildJumpControls(PlacementMode.RightEdgeAlignedTop);

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

    private MenuItem BuildPluginsMenuItem()
    {
        MenuItem parent = CreateMenuGroup(
            "Plugins",
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
                extra.Click += (_, _) => SendActiveChildCommand("MENU|" + targetHeader);
                parent.Items.Add(extra);
            }
        };

        return parent;
    }

    private MenuItem CreateMenuGroup(string header, params string[] subItems)
    {
        MenuItem parent = CreateTopMenuItem(header);
        foreach (string label in subItems)
        {
            MenuItem sub = new()
            {
                Header = label
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

    private (ToggleButton, Popup) BuildAutoControls(PlacementMode placement = PlacementMode.Bottom)
    {
        _autoActiveDot = new Border
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
                _autoActiveDot
            }
        };
        ToggleButton toggle = new()
        {
            Content = autoContent
        };
        toggle.Classes.Add("activity-item");
        ToolTip.SetTip(toggle, "Automated Combat & Hunting Controls");
        _autoToggle = toggle;

        _autoRunningBadge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2),
            Background = Brush.Parse("#064E3B"),
            BorderBrush = Brush.Parse("#059669"),
            BorderThickness = new Thickness(1),
            IsVisible = false,
            Child = new TextBlock { Text = "RUNNING", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#34D399") }
        };
        Grid.SetColumn(_autoRunningBadge, 1);

        Grid headerGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), VerticalAlignment = VerticalAlignment.Center };
        headerGrid.Children.Add(CreatePopupHeader("Combat Automation"));
        headerGrid.Children.Add(_autoRunningBadge);

        _autoAttackBtn = CreatePopupButton("Auto Attack", true, "skua-primary", () =>
        {
            SendActiveChildCommand("AUTO_ATTACK");
            if (_autoToggle != null) _autoToggle.IsChecked = false;
        });

        _autoHuntBtn = CreatePopupButton("Auto Hunt", true, null, () =>
        {
            SendActiveChildCommand("AUTO_HUNT");
            if (_autoToggle != null) _autoToggle.IsChecked = false;
        });
        Grid.SetColumn(_autoHuntBtn, 1);

        _autoStopBtn = CreatePopupButton("Stop Automation", true, "skua-danger", () =>
        {
            SendActiveChildCommand("AUTO_STOP");
            if (_autoToggle != null) _autoToggle.IsChecked = false;
        });
        _autoStopBtn.IsVisible = false;
        Grid.SetColumnSpan(_autoStopBtn, 2);

        Grid actionsGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        actionsGrid.Children.Add(_autoAttackBtn);
        actionsGrid.Children.Add(_autoHuntBtn);
        actionsGrid.Children.Add(_autoStopBtn);

        _autoClassBox = new ComboBox { Height = 34, MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Select Class..." };
        _autoClassBox.SelectionChanged += (_, _) =>
        {
            if (!_suppressPopupSync && _autoClassBox.SelectedItem is string cls)
                SendActiveChildCommand("AUTO_CLASS|" + cls);
        };

        _autoModeBox = new ComboBox { Height = 34, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Mode..." };
        Grid.SetColumn(_autoModeBox, 1);
        _autoModeBox.SelectionChanged += (_, _) =>
        {
            if (!_suppressPopupSync && _autoModeBox.SelectedItem is string mode)
                SendActiveChildCommand("AUTO_MODE|" + mode);
        };

        Grid classModeGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        classModeGrid.Children.Add(_autoClassBox);
        classModeGrid.Children.Add(_autoModeBox);

        _autoMapIdsBox = new TextBox { Height = 32, FontSize = 12, Watermark = "Manual MapIDs (optional, e.g. 1, 2)" };
        _autoMapIdsBox.LostFocus += (_, _) =>
        {
            if (!_suppressPopupSync)
                SendActiveChildCommand("AUTO_MAPS|" + (_autoMapIdsBox.Text ?? string.Empty));
        };

        Popup popup = CreatePopupCard(toggle, 360, new StackPanel
        {
            Spacing = 10,
            Children = { headerGrid, CreatePopupSeparator(), actionsGrid, classModeGrid, _autoMapIdsBox }
        }, placement);

        BindPopupToggle(toggle, popup, () => _jumpToggle, "GET_AUTO_STATE");
        return (toggle, popup);
    }

    private (ToggleButton, Popup) BuildJumpControls(PlacementMode placement = PlacementMode.Bottom)
    {
        ToggleButton toggle = new()
        {
            Content = CreateNavIcon(MaterialIconKind.RunFast, 16, "SkuaMutedTextBrush")
        };
        toggle.Classes.Add("activity-item");
        ToolTip.SetTip(toggle, "Map & Cell Fast Jump Controls");
        _jumpToggle = toggle;

        StackPanel header = CreatePopupHeader("Fast Map Jump");

        _jumpCellBox = new ComboBox { Height = 34, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Select Cell..." };
        _jumpPadBox = new ComboBox { Height = 34, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Select Pad..." };
        Grid.SetColumn(_jumpPadBox, 1);

        _jumpCellBox.SelectionChanged += (_, _) =>
        {
            if (_suppressPopupSync || _jumpCellBox.SelectedItem is not string cell || string.IsNullOrEmpty(cell))
                return;
            if (cell == "Enter")
                _jumpPadBox.SelectedItem = "Spawn";
            else if (_jumpPadBox.SelectedItem is not string p || string.IsNullOrEmpty(p))
                _jumpPadBox.SelectedItem = "Left";
            string pad = _jumpPadBox.SelectedItem as string ?? "Spawn";
            SendActiveChildCommand($"JUMP_TO|{cell}|{pad}");
        };

        Grid selectGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        selectGrid.Children.Add(_jumpCellBox);
        selectGrid.Children.Add(_jumpPadBox);

        Button currentBtn = CreatePopupButton("Current", false, null, () => SendActiveChildCommand("JUMP_CURRENT"));
        Button jumpBtn = CreatePopupButton("Jump", true, "skua-primary", () =>
        {
            string cell = _jumpCellBox?.SelectedItem as string ?? string.Empty;
            string pad = _jumpPadBox?.SelectedItem as string ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(cell) && !string.IsNullOrWhiteSpace(pad))
                SendActiveChildCommand($"JUMP_TO|{cell}|{pad}");
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

        BindPopupToggle(toggle, popup, () => _autoToggle, "GET_JUMP_STATE");
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

    private void BindPopupToggle(ToggleButton toggle, Popup popup, Func<ToggleButton?> otherToggle, string stateCommand)
    {
        toggle.IsCheckedChanged += (_, _) =>
        {
            bool open = toggle.IsChecked == true;
            if (open && otherToggle() is { } other)
                other.IsChecked = false;
            popup.IsOpen = open;
            if (open)
                SendActiveChildCommand(stateCommand);
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
        if (InstancesTabControl.SelectedItem is TabItem sel &&
            _tabs.TryGetValue(sel, out TabInfo? info) &&
            info.Process?.Id != childPid)
            return;

        try
        {
            JObject obj = JObject.Parse(json);
            _suppressPopupSync = true;
            bool isRunning = obj.Value<bool>("isRunning");
            if (_autoActiveDot != null) _autoActiveDot.IsVisible = isRunning;
            if (_autoRunningBadge != null) _autoRunningBadge.IsVisible = isRunning;
            if (_autoAttackBtn != null) _autoAttackBtn.IsVisible = !isRunning;
            if (_autoHuntBtn != null) _autoHuntBtn.IsVisible = !isRunning;
            if (_autoStopBtn != null) _autoStopBtn.IsVisible = isRunning;

            if (_autoClassBox != null)
            {
                _autoClassBox.ItemsSource = obj["classes"]?.ToObject<List<string>>() ?? new List<string>();
                _autoClassBox.SelectedItem = obj.Value<string?>("selectedClass");
            }
            if (_autoModeBox != null)
            {
                _autoModeBox.ItemsSource = obj["modes"]?.ToObject<List<string>>() ?? new List<string>();
                _autoModeBox.SelectedItem = obj.Value<string?>("selectedMode");
            }
            if (_autoMapIdsBox != null)
            {
                _autoMapIdsBox.Text = obj.Value<string?>("manualMapIds") ?? string.Empty;
            }
        }
        catch { }
        finally
        {
            _suppressPopupSync = false;
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
            _suppressPopupSync = true;
            if (_jumpCellBox != null)
            {
                _jumpCellBox.ItemsSource = obj["cells"]?.ToObject<List<string>>() ?? new List<string>();
                _jumpCellBox.SelectedItem = obj.Value<string?>("selectedCell");
            }
            if (_jumpPadBox != null)
            {
                _jumpPadBox.ItemsSource = obj["pads"]?.ToObject<List<string>>() ?? new List<string>();
                _jumpPadBox.SelectedItem = obj.Value<string?>("selectedPad");
            }
        }
        catch { }
        finally
        {
            _suppressPopupSync = false;
        }
    }
}
