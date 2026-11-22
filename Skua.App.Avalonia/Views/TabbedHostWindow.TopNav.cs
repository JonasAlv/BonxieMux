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

        var (autoToggle, autoPopup) = BuildAutoControls();
        var (jumpToggle, jumpPopup) = BuildJumpControls();

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

    private (ToggleButton, Popup) BuildAutoControls()
    {
        _autoActiveDot = new Border
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
        autoContent.Children.Add(_autoActiveDot);
        ToggleButton toggle = CreateNavToggle(autoContent, "Automated Combat & Hunting Controls");
        _autoToggle = toggle;

        _autoRunningBadge = new Border
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
        Grid.SetColumn(_autoRunningBadge, 1);

        Grid headerGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), VerticalAlignment = VerticalAlignment.Center };
        headerGrid.Children.Add(CreatePopupHeader(MaterialIconKind.RobotOutline, "Combat Automation"));
        headerGrid.Children.Add(_autoRunningBadge);

        _autoAttackBtn = CreatePopupButton(MaterialIconKind.PlayCircleOutline, "Auto Attack", true, "skua-primary", () =>
        {
            SendActiveChildCommand("AUTO_ATTACK");
            if (_autoToggle != null) _autoToggle.IsChecked = false;
        });

        _autoHuntBtn = CreatePopupButton(MaterialIconKind.CrosshairsGps, "Auto Hunt", true, null, () =>
        {
            SendActiveChildCommand("AUTO_HUNT");
            if (_autoToggle != null) _autoToggle.IsChecked = false;
        });
        Grid.SetColumn(_autoHuntBtn, 1);

        _autoStopBtn = CreatePopupButton(MaterialIconKind.StopCircleOutline, "Stop Automation", true, "skua-danger", () =>
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
        });

        BindPopupToggle(toggle, popup, () => _jumpToggle, "GET_AUTO_STATE");
        return (toggle, popup);
    }

    private (ToggleButton, Popup) BuildJumpControls()
    {
        ToggleButton toggle = CreateNavToggle(CreateNavContent(MaterialIconKind.RunFast, "Jump"), "Map & Cell Fast Jump Controls");
        _jumpToggle = toggle;

        StackPanel header = CreatePopupHeader(MaterialIconKind.RunFast, "Fast Map Jump");

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

        Button currentBtn = CreatePopupButton(MaterialIconKind.MapMarkerOutline, "Current", false, null, () => SendActiveChildCommand("JUMP_CURRENT"));
        Button jumpBtn = CreatePopupButton(MaterialIconKind.RunFast, "Jump", true, "skua-primary", () =>
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
        });

        BindPopupToggle(toggle, popup, () => _autoToggle, "GET_JUMP_STATE");
        return (toggle, popup);
    }

    private static ToggleButton CreateNavToggle(object content, string tooltip)
    {
        ToggleButton toggle = new() { Content = content };
        toggle.Classes.Add("top-nav-item");
        ToolTip.SetTip(toggle, tooltip);
        return toggle;
    }

    private static StackPanel CreatePopupHeader(MaterialIconKind kind, string title) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            CreateNavIcon(kind, 16, "SkuaAccentBrush"),
            CreateNavTitle(title)
        }
    };

    private static Button CreatePopupButton(MaterialIconKind kind, string text, bool bold, string? accentClass, Action onClick)
    {
        Button btn = new()
        {
            Height = 34,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = CreatePopupButtonContent(kind, text, bold)
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

    private static Popup CreatePopupCard(Control target, double width, Control body)
    {
        Border card = new()
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            MinWidth = width,
            Width = width,
            Child = body
        };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("SkuaSurfaceBrush");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("SkuaBorderBrush");

        return new Popup
        {
            Placement = PlacementMode.Bottom,
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
