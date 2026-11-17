using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Skua.App.Avalonia.ViewModels;
using Skua.Core.AppStartup;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;
using Skua.Core.Messaging;
using Skua.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Skua.App.Avalonia.Views;

public partial class MainWindow : Window
{
    private const int WM_SKUA_JUMP_MAP = 0x0400 + 449;
    private const int WM_SKUA_SET_OPTION = 0x0400 + 450;
    private const int WM_SKUA_LOAD_SCRIPT = 0x0400 + 453;
    private const int WM_SKUA_ARMY_SCHEDULER = 0x0400 + 454;
    private const int WM_SKUA_ARMY_SCHEDULER_STOP = 0x0400 + 455;
    private const int WM_SKUA_CHECK_LOGIN = 0x0400 + 456;
    private const int WM_SKUA_JUMP_PLAYER = 0x0400 + 457;
    private const int WM_SKUA_JUMP_HOUSE = 0x0400 + 465;
    private const int WM_SKUA_LOGIN = 0x0400 + 447;
    private const int WM_SKUA_GRIDVIEW = 0x0400 + 444;
    private const int WM_SKUA_AUTO_ATTACK = 0x0400 + 459;
    private const int WM_SKUA_OPEN_BANK = 0x0400 + 460;
    private const int WM_SKUA_CHANGE_SERVER = 0x0400 + 467;
    private const int WM_SKUA_DETACH = 0x0400 + 470;
    private const int WM_SKUA_DOCK = 0x0400 + 471;

    private bool _isInGridView = false;
    private int _tabVisibility;
    private bool _isWindowFocused = true;

    private void UpdateQualityFocusState()
    {
        try
        {
            var options = Ioc.Default.GetRequiredService<IScriptOption>();
            bool isStandalone = string.IsNullOrEmpty(App.HostPipeName) || HotKeys.IsDetachedMode;
            bool isVisibleWindow = WindowState != WindowState.Minimized && (!isStandalone || IsVisible);
            bool isShown = isVisibleWindow && (isStandalone || _tabVisibility is 0 or 2 or 3);
            bool isFocusedTab = isShown && _isWindowFocused && (isStandalone || _tabVisibility is 0 or 2);
            bool isFocusedGridTile = isShown && _isWindowFocused && !isStandalone && _tabVisibility is 2 or 3;

            options.SetAutoAntiLagContext(isFocusedTab, isFocusedGridTile);
            options.SetForegroundFocus(isShown && _isWindowFocused);
        }
        catch { }
    }

    private readonly HashSet<Key> _pressedKeys = new();

    private void CloseDropdowns()
    {
        AutoToggle?.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        JumpToggle?.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        MainMenuControl?.Close();
    }

    private void InitializeLinuxInput()
    {
        AddHandler(InputElement.KeyDownEvent, OnLinuxKeyDown, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyUpEvent, OnLinuxKeyUp, RoutingStrategies.Tunnel);
        Skua.App.Avalonia.Controls.GameHost.GameClicked -= OnLinuxGameClicked;
        Skua.App.Avalonia.Controls.GameHost.GameClicked += OnLinuxGameClicked;

        Activated += (s, e) =>
        {
            if (string.IsNullOrEmpty(App.HostPipeName) || HotKeys.IsDetachedMode)
            {
                _isWindowFocused = true;
                EvaluateAutoHeadless();
                UpdateQualityFocusState();
            }
        };

        Deactivated += (s, e) =>
        {
            if (string.IsNullOrEmpty(App.HostPipeName) || HotKeys.IsDetachedMode)
            {
                _isWindowFocused = false;
                EvaluateAutoHeadless();
                UpdateQualityFocusState();
            }
        };

        PropertyChanged += (s, e) =>
        {
            if (e.Property != WindowStateProperty && e.Property != IsVisibleProperty)
                return;

            if ((e.Property == WindowStateProperty && WindowState == WindowState.Minimized) ||
                (e.Property == IsVisibleProperty && !IsVisible))
                CloseDropdowns();

            if (string.IsNullOrEmpty(App.HostPipeName) || HotKeys.IsDetachedMode)
            {
                EvaluateAutoHeadless();
                UpdateQualityFocusState();
            }
        };

        AddHandler(PointerPressedEvent, (s, e) =>
        {
            UpdateBackgroundThrottleState(false);
        }, RoutingStrategies.Tunnel);

        if (!string.IsNullOrEmpty(App.HostPipeName))
        {
            try
            {
                var autoVm = Ioc.Default.GetRequiredService<ViewModels.AutoViewModel>();
                if (autoVm.Auto is System.ComponentModel.INotifyPropertyChanged autoNpc)
                {
                    autoNpc.PropertyChanged += (_, args) =>
                    {
                        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(autoVm.Auto.IsRunning))
                            Dispatcher.UIThread.Post(SendAutoStateToHost, DispatcherPriority.Background);
                    };
                }
            }
            catch { }
        }
    }

    private void OnLinuxGameClicked()
    {
        if (AutoToggle != null)
            AutoToggle.IsChecked = false;
        if (JumpToggle != null)
            JumpToggle.IsChecked = false;
        MainMenuControl?.Close();
        Activate();
        if (FlashHost != null && FlashHost.IsVisible)
            FlashHost.Focus();
        else
            Focus();

        UpdateBackgroundThrottleState(false);
    }

    private void UpdateBackgroundThrottleState(bool throttle)
    {
        try
        {
            var options = Ioc.Default.GetRequiredService<IScriptOption>();
            options.SetBackgroundThrottle(throttle && !_isInGridView);
        }
        catch { }
    }

    private void ScheduleScaleModeShowAll()
    {
        _ = Task.Run(() =>
        {
            try
            {
                _flash.TryCall<object?>("setGameObject", TimeSpan.FromMilliseconds(1500), System.Threading.CancellationToken.None, out _, "stage.scaleMode", "showAll");
            }
            catch { }
        });
    }

    private void EnsureTopNavVisible()
    {
        if (HotKeys.IsDetachedMode || _isInGridView || _gameOnly)
            return;

        if (TopNavBorder is { } topNav && !topNav.IsVisible)
        {
            topNav.IsVisible = true;
            topNav.InvalidateMeasure();
            topNav.InvalidateVisual();
        }

        // Ensure an opaque repaint whenever returning visible on X11.
        this.InvalidateVisual();
    }

    private void ReleaseAllPressedKeys()
    {
        foreach (var key in _pressedKeys)
        {
            string keyStr = key.ToString();
            string? symbol = TryMapSpecialKeyToSymbol(key);
            string logical = !string.IsNullOrEmpty(symbol) ? symbol : (keyStr.Length == 1 && char.IsLetter(keyStr[0]) ? keyStr.ToLowerInvariant() : keyStr);
            Skua.App.Avalonia.Controls.GameHost.ForwardKey(false, keyStr, logical, false, false, false);
        }
        _pressedKeys.Clear();
        Skua.App.Avalonia.Controls.GameHost.ForwardClearKeys();
    }

    private void OnLinuxKeyDown(object? sender, global::Avalonia.Input.KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;

        if (FocusManager?.GetFocusedElement() is Button)
            FlashHost?.Focus();

        if (e.Key == Key.Escape && HotKeys.IsDetachedMode && _gameOnly)
        {
            ToggleDetachedGameOnly();
            e.Handled = true;
            return;
        }

        _pressedKeys.Add(e.Key);
        ForwardLinuxKeyEvent(true, e);
    }

    private void OnLinuxKeyUp(object? sender, global::Avalonia.Input.KeyEventArgs e)
    {
        _pressedKeys.Remove(e.Key);

        if (FocusManager?.GetFocusedElement() is TextBox)
            return;

        ForwardLinuxKeyEvent(false, e);
    }

    public static void ForwardLinuxKeyEvent(bool down, global::Avalonia.Input.KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(global::Avalonia.Input.KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(global::Avalonia.Input.KeyModifiers.Shift);
        bool alt = e.KeyModifiers.HasFlag(global::Avalonia.Input.KeyModifiers.Alt);

        string physical = e.PhysicalKey != global::Avalonia.Input.PhysicalKey.None
            ? e.PhysicalKey.ToString()
            : e.Key.ToString();

        bool isNumPadDigit = e.Key is >= global::Avalonia.Input.Key.NumPad0 and <= global::Avalonia.Input.Key.NumPad9;
        string? symbol = (!string.IsNullOrEmpty(e.KeySymbol) && e.KeySymbol.Length == 1 && !char.IsControl(e.KeySymbol[0]))
            ? e.KeySymbol
            : TryMapSpecialKeyToSymbol(e.Key);

        string logical;
        if (!string.IsNullOrEmpty(symbol))
        {
            logical = (shift && !isNumPadDigit) ? ApplyShiftToSymbol(symbol) : symbol;
        }
        else
        {
            string keyStr = e.Key.ToString();
            logical = (keyStr.Length == 1 && char.IsLetter(keyStr[0]))
                ? (shift ? keyStr.ToUpperInvariant() : keyStr.ToLowerInvariant())
                : keyStr;
        }

        Skua.App.Avalonia.Controls.GameHost.ForwardKey(down, physical, logical, ctrl, shift, alt);
    }

    private static string? TryMapSpecialKeyToSymbol(global::Avalonia.Input.Key key) => key switch
    {
        global::Avalonia.Input.Key.D0 or global::Avalonia.Input.Key.NumPad0 => "0",
        global::Avalonia.Input.Key.D1 or global::Avalonia.Input.Key.NumPad1 => "1",
        global::Avalonia.Input.Key.D2 or global::Avalonia.Input.Key.NumPad2 => "2",
        global::Avalonia.Input.Key.D3 or global::Avalonia.Input.Key.NumPad3 => "3",
        global::Avalonia.Input.Key.D4 or global::Avalonia.Input.Key.NumPad4 => "4",
        global::Avalonia.Input.Key.D5 or global::Avalonia.Input.Key.NumPad5 => "5",
        global::Avalonia.Input.Key.D6 or global::Avalonia.Input.Key.NumPad6 => "6",
        global::Avalonia.Input.Key.D7 or global::Avalonia.Input.Key.NumPad7 => "7",
        global::Avalonia.Input.Key.D8 or global::Avalonia.Input.Key.NumPad8 => "8",
        global::Avalonia.Input.Key.D9 or global::Avalonia.Input.Key.NumPad9 => "9",
        global::Avalonia.Input.Key.Multiply => "*",
        global::Avalonia.Input.Key.Add => "+",
        global::Avalonia.Input.Key.Subtract => "-",
        global::Avalonia.Input.Key.Divide => "/",
        global::Avalonia.Input.Key.Decimal => ".",
        global::Avalonia.Input.Key.Space => " ",
        global::Avalonia.Input.Key.OemMinus => "-",
        global::Avalonia.Input.Key.OemPlus => "=",
        global::Avalonia.Input.Key.OemOpenBrackets => "[",
        global::Avalonia.Input.Key.OemCloseBrackets => "]",
        global::Avalonia.Input.Key.OemPipe or global::Avalonia.Input.Key.OemBackslash => "\\",
        global::Avalonia.Input.Key.OemSemicolon => ";",
        global::Avalonia.Input.Key.OemQuotes => "'",
        global::Avalonia.Input.Key.OemComma => ",",
        global::Avalonia.Input.Key.OemPeriod => ".",
        global::Avalonia.Input.Key.OemQuestion => "/",
        global::Avalonia.Input.Key.OemTilde => "`",
        _ => null
    };

    private static string ApplyShiftToSymbol(string symbol)
    {
        if (symbol.Length == 1 && char.IsLetter(symbol[0]))
            return symbol.ToUpperInvariant();

        return symbol switch
        {
            "1" => "!",
            "2" => "@",
            "3" => "#",
            "4" => "$",
            "5" => "%",
            "6" => "^",
            "7" => "&",
            "8" => "*",
            "9" => "(",
            "0" => ")",
            "-" => "_",
            "=" => "+",
            "[" => "{",
            "]" => "}",
            "\\" => "|",
            ";" => ":",
            "'" => "\"",
            "," => "<",
            "." => ">",
            "/" => "?",
            "`" => "~",
            _ => symbol
        };
    }

    private void OnFlashCall(string function, params object[] args)
    {
        if (function == "loaded" && !_isFlashLoaded)
        {
            _isFlashLoaded = true;
            try
            {
                string dir = ClientFileSources.TabsDIR;
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"loaded_{Environment.ProcessId}.txt"), "1");
            }
            catch { }

            Dispatcher.UIThread.Post(() =>
            {
                // Hiding alone leaves the indeterminate animation running at the monitor's refresh rate.
                LoadingBar.IsIndeterminate = false;
                LoadingBar.IsVisible = false;
                bool isHeadless = _options.IsHeadlessNow();
                FlashHost.IsVisible = !isHeadless;
                HeadlessOverlay.IsVisible = isHeadless;
                if (FlashHost != null && !isHeadless && FocusManager?.GetFocusedElement() is not TextBox)
                    FlashHost.Focus();
                try
                {
                    FlashHost?.SetScaleMode(false);
                }
                catch { }
                ScheduleScaleModeShowAll();
                try
                {
                    _options.Reset();
                    _options.EnforceRenderState(flushGamePref: false);
                }
                catch { }
                SyncHostedVisibility();
            });
        }
    }

    public void ForwardFlashInputJson(string json)
    {
        if (_flash is Skua.App.Avalonia.Flash.FlashUtil flashUtil)
        {
            try
            {
                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                flashUtil.SendInput(obj);
            }
            catch { }
        }
    }

    public void HandleHostCommand(string cmd)
    {
        try
        {
            switch (cmd)
            {
                case "BOT_WINDOW":
                    _mainMenuVm.ShowBotWindow();
                    return;
                case "BANK":
                    Ioc.Default.GetRequiredService<IScriptBank>().Open();
                    return;
                case "AUTO_ATTACK":
                    StrongReferenceMessenger.Default.Send<StartAutoAttackMessage>();
                    Dispatcher.UIThread.Post(SendAutoStateToHost, DispatcherPriority.Background);
                    return;
                case "AUTO_HUNT":
                    StrongReferenceMessenger.Default.Send<StartAutoHuntMessage>();
                    Dispatcher.UIThread.Post(SendAutoStateToHost, DispatcherPriority.Background);
                    return;
                case "AUTO_STOP":
                    StrongReferenceMessenger.Default.Send<StopAutoMessage>();
                    Dispatcher.UIThread.Post(SendAutoStateToHost, DispatcherPriority.Background);
                    return;
                case "GET_AUTO_STATE":
                    SendAutoStateToHost();
                    return;
                case "GET_JUMP_STATE":
                    SendJumpStateToHost();
                    return;
                case "JUMP_CURRENT":
                    {
                        var jumpVm = Ioc.Default.GetRequiredService<ViewModels.JumpViewModel>();
                        jumpVm.GetCurrentCommand.Execute(null);
                        SendJumpStateToHost();
                        return;
                    }
                case "JUMP_WINDOW":
                    Ioc.Default.GetRequiredService<IWindowService>().ShowManagedWindow("Fast Travel");
                    return;
            }

            if (cmd.StartsWith("AUTO_CLASS|", StringComparison.Ordinal))
            {
                string cls = cmd["AUTO_CLASS|".Length..];
                var autoVm = Ioc.Default.GetRequiredService<ViewModels.AutoViewModel>();
                autoVm.SelectedClass = string.IsNullOrEmpty(cls) ? null : cls;
                Dispatcher.UIThread.Post(SendAutoStateToHost, DispatcherPriority.Background);
                return;
            }
            if (cmd.StartsWith("AUTO_MODE|", StringComparison.Ordinal))
            {
                string mode = cmd["AUTO_MODE|".Length..];
                var autoVm = Ioc.Default.GetRequiredService<ViewModels.AutoViewModel>();
                autoVm.SelectedClassModeString = string.IsNullOrEmpty(mode) ? null : mode;
                return;
            }
            if (cmd.StartsWith("AUTO_MAPS|", StringComparison.Ordinal))
            {
                string maps = cmd["AUTO_MAPS|".Length..];
                var autoVm = Ioc.Default.GetRequiredService<ViewModels.AutoViewModel>();
                autoVm.ManualMapIDs = maps;
                return;
            }
            if (cmd.StartsWith("JUMP_TO|", StringComparison.Ordinal))
            {
                string[] parts = cmd["JUMP_TO|".Length..].Split('|', 2);
                if (parts.Length == 2)
                {
                    var jumpVm = Ioc.Default.GetRequiredService<ViewModels.JumpViewModel>();
                    jumpVm.SelectedPad = parts[1];
                    jumpVm.SelectedCell = parts[0];
                    if (jumpVm.JumpToCommand.CanExecute(null))
                        jumpVm.JumpToCommand.Execute(null);
                }
                return;
            }

            if (cmd.StartsWith("MENU|", StringComparison.Ordinal))
            {
                string header = cmd["MENU|".Length..];
                string normalizedHeader = NormalizePluginMenuHeader(header);

                IPluginManager? pluginManager = Ioc.Default.GetService<IPluginManager>();
                if (pluginManager != null && pluginManager.Containers.Count == 0 && !string.Equals(normalizedHeader, "View Plugins", StringComparison.OrdinalIgnoreCase))
                {
                    pluginManager.Initialize();
                }

                IMainMenuItemViewModel? item = FindMenuItem(_mainMenuVm.MainMenuItems.Cast<IMainMenuItemViewModel>(), header, normalizedHeader)
                    ?? FindMenuItem(_mainMenuVm.Plugins, header, normalizedHeader)
                    ?? FindMenuItem(Ioc.Default.GetService<IPluginHelper>()?.MenuButtons ?? Array.Empty<IMainMenuItemViewModel>(), header, normalizedHeader);

                if (item?.Command != null && item.Command.CanExecute(null))
                {
                    item.Command.Execute(null);
                    return;
                }

                Ioc.Default.GetRequiredService<IWindowService>().ShowManagedWindow(normalizedHeader);
            }
        }
        catch { }
    }

    private static string NormalizePluginMenuHeader(string header) => header switch
    {
        "Butler" => "ButlerV5 Avalonia",
        "Daily & Weekly Tracker" => "Daily Tracker",
        "Cosmetics & Outfits" => "Cosmetics",
        _ => header
    };

    private static IMainMenuItemViewModel? FindMenuItem(IEnumerable<IMainMenuItemViewModel> items, string header, string normalizedHeader)
    {
        foreach (IMainMenuItemViewModel item in items)
        {
            if (string.Equals(item.Header, header, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Header, normalizedHeader, StringComparison.OrdinalIgnoreCase))
                return item;
            if (item.SubItems != null)
            {
                IMainMenuItemViewModel? found = FindMenuItem(item.SubItems, header, normalizedHeader);
                if (found != null)
                    return found;
            }
        }

        foreach (IMainMenuItemViewModel item in items)
        {
            if (!string.IsNullOrEmpty(item.Header) &&
                (item.Header.StartsWith(normalizedHeader, StringComparison.OrdinalIgnoreCase) ||
                 item.Header.StartsWith(header, StringComparison.OrdinalIgnoreCase)))
                return item;
        }
        return null;
    }

    public IntPtr HandleIpcMessage(uint uMsg, IntPtr wParam, IntPtr lParam)
    {
        if (uMsg == HotKeys.WM_SKUA_GAMEONLY)
        {
            if (HotKeys.IsDetachedMode)
                return IntPtr.Zero;
            bool on = wParam.ToInt32() == 1;
            Dispatcher.UIThread.Post(() => ApplyGameOnly(on));
            return IntPtr.Zero;
        }

        if (uMsg == WM_SKUA_GRIDVIEW)
        {
            bool isGrid = wParam.ToInt32() == 1;
            _isInGridView = isGrid;

            void ApplyGridViewState()
            {
                if (!HotKeys.IsDetachedMode && !_gameOnly && TopNavBorder is { } topNav)
                    topNav.IsVisible = !isGrid;

                var titleBar = this.FindControl<global::Avalonia.Controls.Control>("TitleBar");
                if (titleBar != null && !_isTitleBarPermanentlyHidden)
                    titleBar.IsVisible = !isGrid;

                this.MinWidth = 0;
                this.MinHeight = 0;

                FlashHost?.SetScaleMode(false);
                ScheduleScaleModeShowAll();

                if (isGrid)
                {
                    // In Grid View, all windows are foreground visible windows. Restore full FPS and quality.
                    UpdateBackgroundThrottleState(false);
                    EvaluateAutoHeadless();
                    UpdateQualityFocusState();
                    SyncHostedVisibility();
                    SendHeadlessStateToHost();
                }
                else
                {
                    EnsureTopNavVisible();
                    EvaluateAutoHeadless();
                    UpdateQualityFocusState();
                    SyncHostedVisibility();
                    SendHeadlessStateToHost();
                    if (!_options.IsHeadlessNow())
                        FlashHost?.RefreshLayout();
                }
            }

            if (Dispatcher.UIThread.CheckAccess())
                ApplyGridViewState();
            else
                Dispatcher.UIThread.Post(ApplyGridViewState);
            return IntPtr.Zero;
        }
        else if (uMsg == HotKeys.WM_SKUA_VISIBILITY)
        {
            _tabVisibility = wParam.ToInt32();
            void ApplyVisibilityState()
            {
                if (_tabVisibility is not 0 and not 2 and not 3)
                    CloseDropdowns();
                if (_tabVisibility is 0 or 2 or 3)
                {
                    EnsureTopNavVisible();
                    UpdateBackgroundThrottleState(false);
                }
                EvaluateAutoHeadless();
                UpdateQualityFocusState();
                SyncHostedVisibility();
                SendHeadlessStateToHost();
                if ((_tabVisibility is 0 or 2 or 3) && !_options.IsHeadlessNow() && FlashHost is { IsVisible: true })
                    FlashHost.RefreshLayout();
            }

            if (Dispatcher.UIThread.CheckAccess())
                ApplyVisibilityState();
            else
                Dispatcher.UIThread.Post(ApplyVisibilityState);
            return IntPtr.Zero;
        }
        else if (uMsg == WM_SKUA_LOGIN)
        {
            Task.Run(() => 
            {
                var options = Ioc.Default.GetRequiredService<IScriptOption>();
                var servers = Ioc.Default.GetRequiredService<IScriptServers>();
                string targetServer = string.IsNullOrWhiteSpace(options.ReloginServer) ? "Twilly" : options.ReloginServer;
                servers.Relogin(targetServer);
            });
            return IntPtr.Zero;
        }
        else if (uMsg == 0x0400 + 448) // WM_SKUA_LOGOUT
        {
            Ioc.Default.GetRequiredService<IScriptServers>().Logout();
            return IntPtr.Zero;
        }
        else if (uMsg == WM_SKUA_JUMP_MAP)
        {
            Task.Run(() => 
            {
                try 
                {
                    string tempFile = System.IO.Path.Combine(ClientFileSources.TabsDIR, "skua_global_jump.txt");
                    if (System.IO.File.Exists(tempFile))
                    {
                        string[] lines = System.IO.File.ReadAllLines(tempFile);
                        string map = lines.Length > 0 ? lines[0].Trim() : "";
                        string cell = lines.Length > 1 && !string.IsNullOrWhiteSpace(lines[1]) ? lines[1].Trim() : "Enter";
                        string pad = lines.Length > 2 && !string.IsNullOrWhiteSpace(lines[2]) ? lines[2].Trim() : "Spawn";
                        
                        if (cell.Contains(','))
                        {
                            var cellParts = cell.Split(',');
                            cell = cellParts[0].Trim();
                            if (cellParts.Length > 1) pad = cellParts[1].Trim();
                        }
                        
                        var mapService = Ioc.Default.GetRequiredService<IScriptMap>();
                        if (string.IsNullOrWhiteSpace(map) || mapService.Name.Equals(map.Split('-')[0], StringComparison.OrdinalIgnoreCase))
                            mapService.Jump(cell, pad, false);
                        else
                            mapService.Join(map, cell, pad, true, false);
                    }
                } catch { }
            });
            return IntPtr.Zero;
        }
        else if (uMsg == WM_SKUA_JUMP_PLAYER)
        {
            Task.Run(() => 
            {
                try 
                {
                    string tempFile = System.IO.Path.Combine(ClientFileSources.TabsDIR, "skua_global_jump_player.txt");
                    if (System.IO.File.Exists(tempFile))
                    {
                        string targetPlayer = System.IO.File.ReadAllText(tempFile)?.Trim() ?? "";
                        if (!string.IsNullOrWhiteSpace(targetPlayer))
                            Ioc.Default.GetRequiredService<IScriptPlayer>().Goto(targetPlayer);
                    }
                } catch { }
            });
            return IntPtr.Zero;
        }
        else if (uMsg == WM_SKUA_CHANGE_SERVER)
        {
            Task.Run(() =>
            {
                try
                {
                    string tempFile = System.IO.Path.Combine(ClientFileSources.TabsDIR, "skua_global_change_server.txt");
                    if (!System.IO.File.Exists(tempFile))
                        return;
                    string targetServer = System.IO.File.ReadAllText(tempFile).Trim();
                    if (!string.IsNullOrWhiteSpace(targetServer))
                        Ioc.Default.GetRequiredService<IScriptServers>().Relogin(targetServer);
                }
                catch { }
            });
            return IntPtr.Zero;
        }
        else if (uMsg == WM_SKUA_JUMP_HOUSE)
        {
            Task.Run(() =>
            {
                try
                {
                    var player = Ioc.Default.GetRequiredService<IScriptPlayer>();
                    if (player.LoggedIn && !string.IsNullOrWhiteSpace(player.Username))
                        Ioc.Default.GetRequiredService<IScriptSend>().Packet($"%xt%zm%house%1%{player.Username}%");
                }
                catch { }
            });
            return IntPtr.Zero;
        }
        else if (uMsg == WM_SKUA_LOAD_SCRIPT)
        {
            Task.Run(() => 
            {
                try 
                {
                    string tempFile = System.IO.Path.Combine(ClientFileSources.TabsDIR, "skua_global_script.txt");
                    if (System.IO.File.Exists(tempFile))
                    {
                        string targetScript = System.IO.File.ReadAllText(tempFile);
                        if (!string.IsNullOrWhiteSpace(targetScript) && System.IO.File.Exists(targetScript))
                        {
                            var sm = Ioc.Default.GetRequiredService<Skua.App.Avalonia.ViewModels.ScriptLoaderViewModel>();
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (sm.LoadScriptCommand.CanExecute(targetScript))
                                    sm.LoadScriptCommand.Execute(targetScript);
                            });
                        }
                    }
                } catch { }
            });
            return IntPtr.Zero;
        }
        else if (uMsg == WM_SKUA_SET_OPTION)
        {
            int optionId = wParam.ToInt32();
            bool value = lParam.ToInt32() == 1;
            var options = Ioc.Default.GetRequiredService<IScriptOption>();
            switch (optionId)
            {
                case 1: options.LagKiller = value; break;
                case 2: options.HeadlessMode = value; break;
                case 3: options.HidePlayers = value; break;
                case 4: options.DisableFX = value; break;
                case 5: options.InfiniteRange = value; break;
                case 6: options.Magnetise = value; break;
                case 7: options.SkipCutscenes = value; break;
                case 8: options.UseFunctionBasedSkills = value; break;
                case 9: options.StreamerMode = value; break;
                case 10: options.AggroMonsters = value; break;
                case 11:
                    Ioc.Default.GetRequiredService<ISettingsService>().Set("ShowCharNameInDetachedTitle", value);
                    StrongReferenceMessenger.Default.Send(new Skua.App.Avalonia.AppStartup.AppSettingChangedMessage("ShowCharNameInDetachedTitle", value));
                    break;
                case 99:
                    var sm = Ioc.Default.GetRequiredService<Skua.App.Avalonia.ViewModels.ScriptLoaderViewModel>();
                    var scheduler = Ioc.Default.GetRequiredService<Skua.Shared.Avalonia.ViewModels.ScriptSchedulerViewModel>();
                    if (value)
                    {
                        if (!sm.ScriptManager.ScriptRunning)
                        {
                            if (sm.ToggleScriptCommand.CanExecute(null))
                                sm.ToggleScriptCommand.Execute(null);
                        }
                    }
                    else
                    {
                        if (scheduler.IsRunningQueue && scheduler.StopQueueCommand.CanExecute(null))
                            scheduler.StopQueueCommand.Execute(null);
                        else if (sm.ScriptManager.ScriptRunning && sm.ToggleScriptCommand.CanExecute(null))
                            sm.ToggleScriptCommand.Execute(null);
                    }
                    break;
            }
            return IntPtr.Zero;
        }
        else if (uMsg == WM_SKUA_ARMY_SCHEDULER)
        {
            Task.Run(() => 
            {
                try 
                {
                    string tempFile = System.IO.Path.Combine(ClientFileSources.TabsDIR, "skua_global_playlist.json");
                    if (System.IO.File.Exists(tempFile))
                    {
                        var data = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<Skua.Shared.Avalonia.ViewModels.ScriptSchedulerViewModel.SavedScriptItem>>(System.IO.File.ReadAllText(tempFile));
                        var scheduler = Ioc.Default.GetRequiredService<Skua.Shared.Avalonia.ViewModels.ScriptSchedulerViewModel>();
                        Dispatcher.UIThread.Post(() =>
                        {
                            scheduler.ScriptQueue.Clear();
                            if (data != null)
                            {
                                foreach (var item in data)
                                {
                                    if (System.IO.File.Exists(item.Path))
                                    {
                                        var vm = new Skua.Shared.Avalonia.ViewModels.ScriptItemViewModel(item.Path) { Id = item.Id };
                                        if (!string.IsNullOrEmpty(item.Name)) vm.Name = item.Name;
                                        scheduler.ScriptQueue.Add(vm);
                                    }
                                }
                            }
                            if (scheduler.ScriptQueue.Count > 0 && !scheduler.IsRunningQueue)
                            {
                                if (scheduler.StartQueueCommand.CanExecute(null))
                                    scheduler.StartQueueCommand.Execute(null);
                            }
                        });
                    }
                } catch { }
            });
            return IntPtr.Zero;
        }
        else if (uMsg == WM_SKUA_ARMY_SCHEDULER_STOP)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var scheduler = Ioc.Default.GetRequiredService<Skua.Shared.Avalonia.ViewModels.ScriptSchedulerViewModel>();
                if (scheduler.StopQueueCommand.CanExecute(null))
                    scheduler.StopQueueCommand.Execute(null);
            });
            return IntPtr.Zero;
        }
        else if (uMsg == WM_SKUA_CHECK_LOGIN)
        {
            var bot = Ioc.Default.GetRequiredService<IScriptInterface>();
            return new IntPtr(bot.Player.LoggedIn ? 1 : 0);
        }
        else if (uMsg == 0x0400 + 458) // WM_SKUA_ACCEPT_QUEST
        {
            Task.Run(() => 
            {
                try 
                {
                    string tempFile = System.IO.Path.Combine(ClientFileSources.TabsDIR, "skua_global_accept_quest.txt");
                    if (System.IO.File.Exists(tempFile))
                    {
                        string[] lines = System.IO.File.ReadAllLines(tempFile);
                        string questIdRaw = lines.Length > 0 ? lines[0].Trim() : "";
                        string rewardIdRaw = lines.Length > 1 ? lines[1].Trim() : "-1";
                        int rewardId = int.TryParse(rewardIdRaw, out int rId) ? rId : -1;
                        if (int.TryParse(questIdRaw, out int questId) && questId > 0)
                        {
                            var quest = Ioc.Default.GetRequiredService<IScriptQuest>();
                            quest.RegisterQuests((questId, rewardId));
                        }
                    }
                } catch { }
            });
            return IntPtr.Zero;
        }
        else if (uMsg == WM_SKUA_AUTO_ATTACK)
        {
            bool start = wParam.ToInt32() == 1;
            Dispatcher.UIThread.Post(() =>
            {
                var autoVm = Ioc.Default.GetRequiredService<AutoViewModel>();
                if (start)
                    StrongReferenceMessenger.Default.Send<StartAutoAttackMessage>();
                else
                    StrongReferenceMessenger.Default.Send<StopAutoMessage>();
            });
            return IntPtr.Zero;
        }
        else if (uMsg == 0x0400 + 452) // WM_SKUA_THROTTLE
        {
            bool throttle = wParam.ToInt32() == 1;
            UpdateBackgroundThrottleState(throttle);
            return IntPtr.Zero;
        }
        else if (uMsg == WM_SKUA_OPEN_BANK)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    Ioc.Default.GetRequiredService<IScriptBank>().Open();
                }
                catch { }
            });
            return IntPtr.Zero;
        }
        else if (uMsg == HotKeys.WM_SKUA_EXECUTE_HOTKEY)
        {
            int actionId = wParam.ToInt32();
            string? actionName = HotKeys.GetActionNameById(actionId);
            if (!string.IsNullOrEmpty(actionName))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (actionName == "OpenBank")
                            Ioc.Default.GetRequiredService<IScriptBank>().Open();
                        else
                            HotKeys.ExecuteHotkeyActionDirect(actionName);
                    }
                    catch { }
                });
            }
            return IntPtr.Zero;
        }
        else if (uMsg == HotKeys.WM_SKUA_FOCUS)
        {
            bool focused = wParam != IntPtr.Zero;
            _isWindowFocused = focused;
            ReleaseAllPressedKeys();
            Skua.App.Avalonia.Controls.GameHost.ForwardFocus(focused);
            Dispatcher.UIThread.Post(() =>
            {
                if (focused)
                    UpdateBackgroundThrottleState(false);
                EvaluateAutoHeadless();
                UpdateQualityFocusState();
                SyncHostedVisibility();
                SendHeadlessStateToHost();
            });
            return IntPtr.Zero;
        }
        else if (uMsg == HotKeys.WM_SKUA_RELOAD_HOTKEYS)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    Ioc.Default.GetRequiredService<IHotKeyService>().Reload();
                }
                catch { }
            });
            return IntPtr.Zero;
        }
        else if (uMsg == WM_SKUA_DETACH)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _tabVisibility = 0;
                HotKeys.IsDetachedMode = true;
                UpdateBackgroundThrottleState(false);
                EvaluateAutoHeadless();
                UpdateQualityFocusState();
                _isTitleBarPermanentlyHidden = false;
                var titleBar = this.FindControl<global::Avalonia.Controls.Control>("TitleBar");
                if (titleBar is Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar wtb)
                {
                    wtb.IsVisible = true;
                    wtb.CanDock = true;
                }
                else if (titleBar != null)
                {
                    titleBar.IsVisible = true;
                }

                var topNav = this.FindControl<global::Avalonia.Controls.Control>("TopNavBorder");
                if (topNav != null)
                    topNav.IsVisible = true;

                this.ExtendClientAreaToDecorationsHint = true;
                this.ExtendClientAreaTitleBarHeightHint = 32;
                this.WindowState = WindowState.Normal;
                this.ShowInTaskbar = true;
                this.Opacity = 1;
                this.Width = 948;
                this.Height = 600;
                this.Show();
                this.Activate();
                if (FlashHost != null)
                    FlashHost.IsVisible = !_options.IsHeadlessNow();
                SyncHostedVisibility();
                Dispatcher.UIThread.Post(() => FlashHost?.ForceRefreshFrame(), DispatcherPriority.Render);
                DispatcherTimer.RunOnce(() =>
                {
                    if (HotKeys.IsDetachedMode)
                        FlashHost?.ForceRefreshFrame();
                }, TimeSpan.FromMilliseconds(100));
            });
            return IntPtr.Zero;
        }
        else if (uMsg == WM_SKUA_DOCK)
        {
            Dispatcher.UIThread.Post(() =>
            {
                HotKeys.IsDetachedMode = false;
                if (_gameOnly)
                {
                    ApplyGameOnly(false);
                    if (_gameOnlySetFullScreen && WindowState == WindowState.FullScreen)
                        WindowState = WindowState.Normal;
                    _gameOnlySetFullScreen = false;
                }
                HideTitleBar();
                var titleBar = this.FindControl<global::Avalonia.Controls.Control>("TitleBar");
                if (titleBar is Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar wtb)
                {
                    wtb.CanDock = false;
                }
                this.ShowInTaskbar = false;
                this.Hide();
                SyncHostedVisibility();
            });
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void SyncHostedVisibility()
    {
        bool shouldRender = (HotKeys.IsDetachedMode || _tabVisibility is 0 or 2 or 3) && !_options.IsHeadlessNow();
        FlashHost?.SetHostedVisible(shouldRender);
    }

    private static long _headlessStateSeq;
    private static readonly System.Collections.Concurrent.BlockingCollection<string> _hostPipeQueue = new();
    private static int _hostPipeWorkerStarted;

    internal static void BroadcastMugetsuSetting(string name, string value)
    {
        if (!string.IsNullOrEmpty(App.HostPipeName))
        {
            SendPipeToHost($"MUGETSU_SETTING|{name}|{value}");
        }
        else if (Ioc.Default.GetService<IFlashUtil>() is Flash.FlashUtil player)
        {
            player.SendInput(new { input = "mugetsu_setting", name, value });
        }
    }

    private void SendHeadlessStateToHost()
    {
        if (string.IsNullOrEmpty(App.HostPipeName))
            return;
        bool manual = _options.HeadlessMode;
        bool auto = _options.IsHeadlessNow() && !manual;
        long seq = System.Threading.Interlocked.Increment(ref _headlessStateSeq);
        SendPipeToHost($"HEADLESS_STATE|{Environment.ProcessId}|{(manual ? 1 : 0)}|{(auto ? 1 : 0)}|{seq}");
    }

    private void EvaluateAutoHeadless()
    {
        IScriptOption options = Ioc.Default.GetRequiredService<IScriptOption>();
        bool want = false;
        try
        {
            if (options.AutoHeadless)
            {
                bool isStandalone = string.IsNullOrEmpty(App.HostPipeName) || HotKeys.IsDetachedMode;
                bool isVisibleWindow = WindowState != WindowState.Minimized && (!isStandalone || IsVisible);
                int effectiveVisibility = isVisibleWindow ? (isStandalone ? 0 : _tabVisibility) : 4;
                bool isFocusedTab = _isWindowFocused && effectiveVisibility is 0 or 2;
                bool isFocusedGridTile = _isWindowFocused && !isStandalone && effectiveVisibility is 2 or 3;

                bool excluded = (options.AutoHeadlessExcludeFocused && isFocusedTab)
                    || (options.AutoHeadlessExcludeGridView && isFocusedGridTile);
                want = !excluded;
            }
        }
        catch { }
        options.SetAutoHeadless(want);
    }

    private static void SendPipeToHost(string message)
    {
        if (string.IsNullOrEmpty(App.HostPipeName))
            return;

        if (System.Threading.Interlocked.CompareExchange(ref _hostPipeWorkerStarted, 1, 0) == 0)
        {
            _ = Task.Factory.StartNew(() =>
            {
                foreach (string msg in _hostPipeQueue.GetConsumingEnumerable())
                {
                    try
                    {
                        using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", App.HostPipeName, System.IO.Pipes.PipeDirection.Out);
                        pipe.Connect(1000);
                        using var writer = new System.IO.StreamWriter(pipe) { AutoFlush = true };
                        writer.WriteLine(msg);
                    }
                    catch { }
                }
            }, TaskCreationOptions.LongRunning);
        }

        _hostPipeQueue.Add(message);
    }

    private void SendAutoStateToHost()
    {
        try
        {
            var vm = Ioc.Default.GetRequiredService<ViewModels.AutoViewModel>();
            var payload = new
            {
                isRunning = vm.Auto.IsRunning,
                selectedClass = vm.SelectedClass,
                selectedMode = vm.SelectedClassModeString,
                manualMapIds = vm.ManualMapIDs,
                classes = vm.PlayerClasses ?? new List<string>(),
                modes = vm.CurrentClassModeStrings ?? new List<string>()
            };
            SendPipeToHost($"AUTO_STATE|{Environment.ProcessId}|{Newtonsoft.Json.JsonConvert.SerializeObject(payload)}");
        }
        catch { }
    }

    private void SendJumpStateToHost()
    {
        try
        {
            var vm = Ioc.Default.GetRequiredService<ViewModels.JumpViewModel>();
            vm.UpdateCells();
            var payload = new
            {
                cells = vm.Cells.ToList(),
                pads = vm.Pads.ToList(),
                selectedCell = vm.SelectedCell,
                selectedPad = vm.SelectedPad
            };
            SendPipeToHost($"JUMP_STATE|{Environment.ProcessId}|{Newtonsoft.Json.JsonConvert.SerializeObject(payload)}");
        }
        catch { }
    }

}
