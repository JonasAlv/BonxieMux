using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.DependencyInjection;
using Material.Icons;
using Material.Icons.Avalonia;
using Skua.App.Avalonia.Services;
using Skua.Backend.Ipc;
using Skua.Core.AppStartup;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Shared.Avalonia.Theming;
using Skua.Shared.Avalonia.ViewModels.Dialogs;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Skua.App.Avalonia.Views;

public partial class TabbedHostWindow
{
    private static List<int> CollectDescendantPids(int rootPid)
    {
        List<int> result = new();
        HashSet<int> visited = new() { rootPid };
        Queue<int> queue = new();
        queue.Enqueue(rootPid);

        while (queue.Count > 0)
        {
            int pid = queue.Dequeue();
            string taskDir = $"/proc/{pid}/task";
            if (!Directory.Exists(taskDir))
                continue;

            try
            {
                foreach (string tidDir in Directory.EnumerateDirectories(taskDir))
                {
                    string childrenFile = Path.Combine(tidDir, "children");
                    if (!File.Exists(childrenFile))
                        continue;

                    string content = File.ReadAllText(childrenFile);
                    foreach (string token in content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(token, out int childPid) && childPid > 0 && visited.Add(childPid))
                        {
                            result.Add(childPid);
                            queue.Enqueue(childPid);
                        }
                    }
                }
            }
            catch { }
        }

        return result;
    }

    private static void KillProcessTree(Process? process)
    {
        if (process == null) return;
        int rootPid = 0;
        try { rootPid = process.Id; } catch { }
        if (rootPid <= 0) return;

        List<int> descendants = CollectDescendantPids(rootPid);

        try
        {
            try
            {
                using var pkill = Process.Start(new ProcessStartInfo
                {
                    FileName = "pkill",
                    Arguments = $"-9 -P {rootPid}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                pkill?.WaitForExit(100);
            }
            catch { }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(200);
            }
        }
        catch { }

        for (int i = descendants.Count - 1; i >= 0; i--)
        {
            int descPid = descendants[i];
            if (!Directory.Exists($"/proc/{descPid}"))
                continue;

            try
            {
                using Process descProc = Process.GetProcessById(descPid);
                if (!descProc.HasExited)
                {
                    descProc.Kill(entireProcessTree: true);
                    descProc.WaitForExit(100);
                }
            }
            catch { }
        }
    }

    public void DetachTab(TabItem tab, PixelPoint? targetScreenPos = null)
    {
        if (!_tabs.TryGetValue(tab, out TabInfo? info) || info.IsDetached)
            return;

        info.IsDetached = true;
        info.TabItem = tab;

        int clientWidth = Math.Max(400, (int)Math.Round(Bounds.Width > 0 ? Bounds.Width : 948));
        int clientHeight = Math.Max(300, (int)Math.Round(Bounds.Height > 0 ? Bounds.Height : 600));
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        int screenX = targetScreenPos?.X ?? (screen != null
            ? screen.WorkingArea.X + Math.Max(0, (screen.WorkingArea.Width - clientWidth) / 2)
            : this.Position.X);
        int screenY = targetScreenPos?.Y ?? (screen != null
            ? screen.WorkingArea.Y + Math.Max(0, (screen.WorkingArea.Height - clientHeight) / 2)
            : this.Position.Y);

        info.DetachedScreenX = screenX;
        info.DetachedScreenY = screenY;

        if (info.DetachedIcon != null)
        {
            info.DetachedIcon.IsVisible = true;
        }
        if (!tab.Classes.Contains("tab-detached"))
        {
            tab.Classes.Add("tab-detached");
        }

        InstancesTabControl.SelectedItem = tab;
        UpdateTabScrollButtons();

        if (info.ChildHwnd != IntPtr.Zero)
        {
            PlatformDetachChild(info.ChildHwnd, screenX, screenY, clientWidth, clientHeight);
        }

        DoReposition();
    }

    public void DockChildInstance(int childPid)
    {
        var entry = _tabs.FirstOrDefault(kvp => kvp.Value.Process != null && kvp.Value.Process.Id == childPid);
        if (entry.Key == null || entry.Value == null)
            return;

        TabItem tab = entry.Key;
        TabInfo info = entry.Value;

        if (!info.IsDetached)
            return;

        info.IsDetached = false;
        info.WasDetachedBeforeGrid = false;

        if (info.DetachedIcon != null)
        {
            info.DetachedIcon.IsVisible = false;
        }
        tab.Classes.Remove("tab-detached");

        if (info.ChildHwnd != IntPtr.Zero)
        {
            PlatformReattachChild(info.ChildHwnd);
        }

        InstancesTabControl.SelectedItem = tab;
        UpdateTabScrollButtons();
        DoReposition();
    }

    private void FocusDetachedButton_Click(object? sender, RoutedEventArgs e)
    {
        if (InstancesTabControl.SelectedItem is TabItem selTab && _tabs.TryGetValue(selTab, out var info))
        {
            if (info.IsDetached && info.ChildHwnd != IntPtr.Zero)
            {
                PlatformRaiseChild(info.ChildHwnd);
            }
        }
    }

    private void DockDetachedButton_Click(object? sender, RoutedEventArgs e)
    {
        if (InstancesTabControl.SelectedItem is TabItem selTab && _tabs.TryGetValue(selTab, out var info))
        {
            if (info.IsDetached && info.Process != null)
            {
                DockChildInstance(info.Process.Id);
            }
        }
    }

    public void DockAllInstances()
    {
        var detached = _tabs.Where(kvp => kvp.Value.IsDetached).ToList();
        foreach (var kvp in detached)
        {
            DockChildInstance(kvp.Value.Process.Id);
        }
    }

    private void MenuItem_DockAllWindows_Click(object? sender, RoutedEventArgs e)
    {
        DockAllInstances();
    }

    private void CloseTab(TabItem tab)
    {
        if (!_tabs.ContainsKey(tab) && !InstancesTabControl.Items.Contains(tab))
            return;

        if (_tabs.TryGetValue(tab, out TabInfo? info))
        {
            _tabs.Remove(tab);

            int childPid = 0;
            try { childPid = info.Process?.Id ?? 0; } catch { }
            if (childPid <= 0 && info.ChildHwnd != IntPtr.Zero)
                childPid = (int)info.ChildHwnd.ToInt64();

            IntPtr unmapHandle = info.ChildHwnd != IntPtr.Zero
                ? info.ChildHwnd
                : (childPid > 0 ? new IntPtr(childPid) : IntPtr.Zero);
            if (unmapHandle != IntPtr.Zero)
                PlatformUnmapChild(unmapHandle);

            if (childPid > 0)
            {
                _armyJumpStore.RemoveLocation(childPid);
            }

            Process? procToKill = info.Process;
            Task.Run(() =>
            {
                try
                {
                    KillProcessTree(procToKill);
                    if (childPid > 0)
                        CleanUpChildResources(childPid);
                }
                catch { }
            });
        }
        InstancesTabControl.Items.Remove(tab);
        UpdateTabScrollButtons();

        if (_tabs.Count == 0 || !InstancesTabControl.Items.OfType<TabItem>().Any())
        {
            if (_initialArgs.Contains("--exit-on-script-stop", StringComparer.OrdinalIgnoreCase))
            {
                Close();
                return;
            }

            _lastSelectedTab = null;
            _lastActiveHwnd = IntPtr.Zero;
            _isGridViewEnabled = false;
            PlatformClearAllViewports();

            if (!IsVisible)
                Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            DoReposition();
            return;
        }

        if (_isGridViewEnabled)
        {
            if (_tabs.Count <= 1)
            {
                ExitGridView();
                return;
            }

            DoReposition();
            return;
        }

        var remaining = InstancesTabControl.Items.OfType<TabItem>().FirstOrDefault();
        if (remaining != null && InstancesTabControl.SelectedItem == null)
            InstancesTabControl.SelectedItem = remaining;
        else
            DoReposition();
    }

    private readonly Dictionary<string, List<DateTime>> _relaunches = new();

    private bool AllowRelaunch(string[] args)
    {
        string key = AccountKey(args);
        if (key.Length == 0)
            return false;
        if (!_relaunches.TryGetValue(key, out List<DateTime>? times))
            _relaunches[key] = times = new();
        times.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(30));
        if (times.Count >= 5)
        {
            HostLog(args, " the client keeps crashing (5 times in 30 minutes); not starting it again.");
            return false;
        }
        times.Add(DateTime.UtcNow);
        return true;
    }

    internal static string[] WithLastScript(string[] args, int pid)
    {
        try
        {
            string file = Path.Combine(ClientFileSources.TabsDIR, $"{pid}.state");
            if (!File.Exists(file))
                return args;
            string[] lines = File.ReadAllLines(file);
            File.Delete(file);
            string script = lines.Length > 0 ? lines[0].Trim() : "";
            bool running = lines.Length > 1 && lines[1].Trim() == "1";
            if (script.Length == 0 || !File.Exists(script))
                return args;
            List<string> result = new();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] is "--run-script" or "--load-script")
                {
                    i++;
                    continue;
                }
                if (args[i] == "--unattended")
                    continue;
                result.Add(args[i]);
            }
            result.Add(running ? "--run-script" : "--load-script");
            result.Add(script);
            if (running)
                result.Add("--unattended");
            return result.ToArray();
        }
        catch
        {
            return args;
        }
    }

    private static void HostLog(string[] args, string message)
    {
        try
        {
            string who = AccountKey(args);
            string path = Path.Combine(ClientFileSources.SkuaDIR, "client-recovery.log");
            FileInfo info = new(path);
            if (info.Exists && info.Length > 200_000)
                info.Delete();
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{(who.Length > 0 ? who : "?")}] (tab host){message}{Environment.NewLine}");
        }
        catch { }
    }

    public void AddNewInstance(string rawArgs)
    {
        AddNewInstance(ParseRawCommandLine(rawArgs));
    }

    public void AddNewInstance(string[]? clientArgs = null)
    {
        clientArgs ??= Array.Empty<string>();

        int availableId = 1;
        var usedIds = _tabs.Values.Select(t => t.ClientNumber).ToHashSet();
        while (usedIds.Contains(availableId))
            availableId++;

        string userArg = string.Empty;
        for (int i = 0; i < clientArgs.Length; i++)
        {
            if ((clientArgs[i] == "-u" || clientArgs[i] == "--user") && i + 1 < clientArgs.Length)
            {
                if (!string.IsNullOrWhiteSpace(clientArgs[i + 1]))
                    userArg = clientArgs[i + 1];
                break;
            }
        }

        string tabName = ShouldShowCharNameOnTabs() && !string.IsNullOrWhiteSpace(userArg)
            ? userArg
            : $"Tab {availableId}";

        #region Tab Header UI
        StackPanel headerPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
        
        Border focusDot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            IsVisible = false
        };
        UiTokens.Bind(focusDot, Border.BackgroundProperty, UiTokens.SkuaAccentBrush);

        TextBox editTitle = new TextBox
        {
            Text = tabName,
            IsVisible = false,
            MinWidth = 60,
            MaxWidth = 180,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(0, 0, 6, 0)
        };
        Skua.Shared.Avalonia.Theming.UiTokens.Bind(editTitle, Layoutable.HeightProperty, Skua.Shared.Avalonia.Theming.UiTokens.UiSmallControlHeight);
        Skua.Shared.Avalonia.Theming.UiTokens.Bind(editTitle, TextBox.BorderThicknessProperty, Skua.Shared.Avalonia.Theming.UiTokens.UiBorderThin);
        Skua.Shared.Avalonia.Theming.UiTokens.Bind(editTitle, TextBox.CornerRadiusProperty, Skua.Shared.Avalonia.Theming.UiTokens.UiRadiusControl);
        Skua.Shared.Avalonia.Theming.UiTokens.Bind(editTitle, TextBox.BackgroundProperty, Skua.Shared.Avalonia.Theming.UiTokens.SkuaSurfaceAltBrush);
        Skua.Shared.Avalonia.Theming.UiTokens.Bind(editTitle, TextBox.ForegroundProperty, Skua.Shared.Avalonia.Theming.UiTokens.SkuaTextBrush);
        Skua.Shared.Avalonia.Theming.UiTokens.Bind(editTitle, TextBox.CaretBrushProperty, Skua.Shared.Avalonia.Theming.UiTokens.SkuaTextBrush);
        Skua.Shared.Avalonia.Theming.UiTokens.Bind(editTitle, TextBox.BorderBrushProperty, Skua.Shared.Avalonia.Theming.UiTokens.SkuaBorderBrush);

        TextBlock title = new TextBlock
        {
            Text = tabName,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            FontWeight = FontWeight.SemiBold
        };
        Skua.Shared.Avalonia.Theming.UiTokens.Bind(title, TextBlock.FontSizeProperty, Skua.Shared.Avalonia.Theming.UiTokens.UiFontBody);
        Skua.Shared.Avalonia.Theming.UiTokens.Bind(title, TextBlock.ForegroundProperty, Skua.Shared.Avalonia.Theming.UiTokens.SkuaTextBrush);

        Border closeBtn = new Border
        {
            Background = Brushes.Transparent,
            Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            VerticalAlignment = VerticalAlignment.Center
        };
        TextBlock closeTxt = new TextBlock
        {
            Text = "✕",
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        UiTokens.Bind(closeTxt, TextBlock.FontSizeProperty, UiTokens.UiFontMicro);
        UiTokens.Bind(closeTxt, TextBlock.ForegroundProperty, UiTokens.SkuaMutedTextBrush);
        closeBtn.Child = closeTxt;

        MaterialIcon crown = new MaterialIcon
        {
            Kind = MaterialIconKind.Crown,
            Width = 10,
            Height = 10,
            IsVisible = false,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -8, 6, 0)
        };
        UiTokens.Bind(crown, MaterialIcon.ForegroundProperty, UiTokens.SkuaWarningBrush);

        MaterialIcon detachedIcon = new MaterialIcon
        {
            Kind = MaterialIconKind.DockWindow,
            Width = 12,
            Height = 12,
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };
        UiTokens.Bind(detachedIcon, MaterialIcon.ForegroundProperty, UiTokens.SkuaAccentBrush);
        ToolTip.SetTip(detachedIcon, "Window Mode: click to focus, double-click to dock into tabs");

        Border scriptDot = new Border
        {
            Width = 5,
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            IsHitTestVisible = false
        };
        UiTokens.Bind(scriptDot, Border.BackgroundProperty, UiTokens.SkuaSuccessBrush);

        Grid iconHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
        iconHost.Children.Add(focusDot);
        iconHost.Children.Add(crown);
        headerPanel.Children.Add(iconHost);
        headerPanel.Children.Add(detachedIcon);
        headerPanel.Children.Add(scriptDot);
        headerPanel.Children.Add(editTitle);
        headerPanel.Children.Add(title);
        headerPanel.Children.Add(closeBtn);

        closeBtn.PointerEntered += (s, ev) =>
        {
            Skua.Shared.Avalonia.Theming.UiTokens.Bind(closeTxt, TextBlock.ForegroundProperty, Skua.Shared.Avalonia.Theming.UiTokens.SkuaDangerForegroundBrush);
            Skua.Shared.Avalonia.Theming.UiTokens.Bind(closeBtn, Border.BackgroundProperty, Skua.Shared.Avalonia.Theming.UiTokens.SkuaDangerBrush);
        };
        closeBtn.PointerExited += (s, ev) =>
        {
            Skua.Shared.Avalonia.Theming.UiTokens.Bind(closeTxt, TextBlock.ForegroundProperty, Skua.Shared.Avalonia.Theming.UiTokens.SkuaMutedTextBrush);
            closeBtn.Background = Brushes.Transparent;
        };
        
        TabInfo info = new TabInfo
        {
            ClientNumber = availableId,
            LastCharacterName = userArg,
            AccountOrderKey = AccountKey(clientArgs ?? Array.Empty<string>()),
            HeaderPanel = headerPanel,
            Crown = crown,
            DetachedIcon = detachedIcon,
            FocusDot = focusDot,
            ScriptDot = scriptDot
        };
        
        Action closeEditMode = () =>
        {
            if (!editTitle.IsVisible) return;
            string newName = string.IsNullOrWhiteSpace(editTitle.Text) ? title.Text : editTitle.Text;
            if (title.Text != newName)
            {
                title.Text = newName;
                info.IsCustomNamed = true;
            }
            title.IsVisible = true;
            editTitle.IsVisible = false;
        };

        title.DoubleTapped += (s, ev) =>
        {
            editTitle.Text = title.Text;
            title.IsVisible = false;
            editTitle.IsVisible = true;
            
            Dispatcher.UIThread.Post(() => {
                editTitle.Focus();
                editTitle.SelectAll();
            }, DispatcherPriority.Input);
            
            ev.Handled = true;
        };
        
        editTitle.LostFocus += (s, ev) => closeEditMode();
        
        editTitle.KeyDown += (s, ev) =>
        {
            if (ev.Key == Key.Enter)
            {
                closeEditMode();
                ev.Handled = true;
            }
            else if (ev.Key == Key.Escape)
            {
                editTitle.Text = title.Text;
                closeEditMode();
                ev.Handled = true;
            }
        };
        #endregion

        TabItem newTab = new TabItem { Header = headerPanel };
        info.TabItem = newTab;

        headerPanel.DoubleTapped += (s, ev) =>
        {
            if (_tabs.TryGetValue(newTab, out var tInfo) && tInfo.IsDetached && tInfo.Process != null)
            {
                DockChildInstance(tInfo.Process.Id);
                ev.Handled = true;
            }
        };
        DragDrop.SetAllowDrop(newTab, true);

        _tabs[newTab] = info;
        InstancesTabControl.Items.Add(newTab);
        if (InstancesTabControl.SelectedItem == null)
        {
            InstancesTabControl.SelectedItem = newTab;
        }
        UpdateTabFocusDots();
        Dispatcher.UIThread.Post(() =>
        {
            TabScrollViewer.Offset = new Vector(Math.Max(0, TabScrollViewer.Extent.Width - TabScrollViewer.Viewport.Width), 0);
            UpdateTabScrollButtons();
        }, DispatcherPriority.Background);

        RequestGridViewForBatch(clientArgs);

        EnqueueSpawn(() =>
        {
            if (_isClosing || !_tabs.ContainsKey(newTab))
            {
                _isSpawning = false;
                ProcessNextSpawn();
                return;
            }
            
            Process p = new Process();
            string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            p.StartInfo.FileName = exe;
            if (System.IO.Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                p.StartInfo.ArgumentList.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "BonxieMux.dll"));
            }
            p.StartInfo.ArgumentList.Add("--embed");
            p.StartInfo.ArgumentList.Add(_hostHwnd.ToInt64().ToString());
            p.StartInfo.ArgumentList.Add("--host-pid");
            p.StartInfo.ArgumentList.Add(Process.GetCurrentProcess().Id.ToString());

            string? embeddedUsername = null;
            string? embeddedPassword = null;
            for (int i = 0; i < clientArgs.Length; i++)
            {
                string arg = clientArgs[i];
                if ((string.Equals(arg, "-u", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--user", StringComparison.OrdinalIgnoreCase)) && i + 1 < clientArgs.Length)
                {
                    embeddedUsername = clientArgs[i + 1];
                    i++;
                    continue;
                }
                if ((string.Equals(arg, "-p", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--password", StringComparison.OrdinalIgnoreCase)) && i + 1 < clientArgs.Length)
                {
                    embeddedPassword = clientArgs[i + 1];
                    i++;
                    continue;
                }

                if (!string.IsNullOrEmpty(arg))
                    p.StartInfo.ArgumentList.Add(arg);
            }

            p.StartInfo.EnvironmentVariables.Remove("BONXIEMUX_USERNAME");
            p.StartInfo.EnvironmentVariables.Remove("BONXIEMUX_PASSWORD");

            if (!string.IsNullOrWhiteSpace(embeddedUsername))
            {
                p.StartInfo.EnvironmentVariables["BONXIEMUX_USERNAME"] = embeddedUsername;
            }
            if (!string.IsNullOrWhiteSpace(embeddedPassword))
            {
                p.StartInfo.EnvironmentVariables["BONXIEMUX_PASSWORD"] = embeddedPassword;
            }

            p.EnableRaisingEvents = true;
            p.Exited += (s, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_isClosing)
                    {
                        int code = 0;
                        try { code = p.ExitCode; } catch { }
                        if (_tabs.ContainsKey(newTab) && code != 0 && AllowRelaunch(clientArgs))
                        {
                            string[] relaunchArgs = WithLastScript(clientArgs, p.Id);
                            HostLog(clientArgs, $" the client closed unexpectedly (exit code {code}); starting it again.");
                            AddNewInstance(relaunchArgs);
                        }
                        CloseTab(newTab);
                    }
                });
            };

            var (wlHandle, wlSocket) = Skua.App.Avalonia.Controls.GameHost.CreateWaylandHostForChild(1280, 720);
            if (!string.IsNullOrEmpty(wlSocket))
                p.StartInfo.EnvironmentVariables["BONXIE_WL_DISPLAY"] = wlSocket;
            else
                p.StartInfo.EnvironmentVariables.Remove("BONXIE_WL_DISPLAY");

            try
            {
                p.Start();
            }
            catch (Exception ex)
            {
                Skua.App.Avalonia.Controls.GameHost.ReleasePendingWaylandHost(wlHandle);
                p.Dispose();
                _isSpawning = false;
                Trace.WriteLine($"BonxieMux failed to start child process for tab '{tabName}': {ex}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (_tabs.ContainsKey(newTab))
                        CloseTab(newTab);
                    ProcessNextSpawn();
                }, DispatcherPriority.Background);
                return;
            }
            Skua.App.Avalonia.Controls.GameHost.RegisterPendingWaylandHost(p.Id, wlHandle, wlSocket);
            info.Process = p;
            HotKeys.RegisterSkuaPid((uint)p.Id);

            int pid = p.Id;
            Task.Run(async () =>
            {
                bool attached = false;
                string readyFile = Path.Combine(ClientFileSources.TabsDIR, $"ready_{pid}.txt");
                for (int attempt = 0; attempt < 300 && !p.HasExited && info.ChildHwnd == IntPtr.Zero && !_isClosing; attempt++)
                {
                    await Task.Delay(50);
                    if (info.ChildHwnd != IntPtr.Zero) break;
                    IntPtr childHwnd = IntPtr.Zero;
                    if (File.Exists(readyFile))
                    {
                        try
                        {
                            string text = File.ReadAllText(readyFile).Trim();
                            if (long.TryParse(text, out long handleVal) && handleVal != 0)
                                childHwnd = (IntPtr)handleVal;
                        }
                        catch { }
                    }

                    if (childHwnd != IntPtr.Zero)
                    {
                        attached = true;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!_isClosing && info.ChildHwnd == IntPtr.Zero)
                            {
                                AttachChildWindow(info, childHwnd);
                            }
                        });
                        break;
                    }
                }

                if (!attached && info.ChildHwnd == IntPtr.Zero)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _isSpawning = false;
                        ProcessNextSpawn();
                    });
                }
            });
        });

        #region Tab Interactions
        closeBtn.PointerPressed += (s, ev) => 
        {
            CloseTab(newTab);
            ev.Handled = true;
        };
        
        headerPanel.PointerReleased += (s, ev) =>
        {
            if (ev.InitialPressMouseButton == MouseButton.Middle)
                CloseTab(newTab);
        };

        ContextMenu ctx = new ContextMenu();
        MenuItem closeItem = new MenuItem { Header = "Close Tab" };
        closeItem.Click += (s, ev) => CloseTab(newTab);
        MenuItem closeOthersItem = new MenuItem { Header = "Close Other Tabs" };
        closeOthersItem.Click += (s, ev) =>
        {
            var toRemove = InstancesTabControl.Items.OfType<TabItem>().Where(t => t != newTab && t.Header is StackPanel).ToList();
            foreach (var tab in toRemove) CloseTab(tab);
        };
        MenuItem closeRightItem = new MenuItem { Header = "Close Tabs to the Right" };
        closeRightItem.Click += (s, ev) =>
        {
            int idx = InstancesTabControl.Items.IndexOf(newTab);
            var toRemove = InstancesTabControl.Items.OfType<TabItem>().Where((t, i) => i > idx && t.Header is StackPanel).ToList();
            foreach (var tab in toRemove) CloseTab(tab);
        };
        ctx.Items.Add(closeItem);
        ctx.Items.Add(closeOthersItem);
        ctx.Items.Add(closeRightItem);
        ctx.Items.Add(new Separator());

        MenuItem toggleWindowItem = new MenuItem
        {
            Header = "Detach to Window"
        };
        toggleWindowItem.Click += (s, ev) =>
        {
            if (_tabs.TryGetValue(newTab, out var tInfo))
            {
                if (tInfo.IsDetached && tInfo.Process != null)
                    DockChildInstance(tInfo.Process.Id);
                else
                    DetachTab(newTab);
            }
        };
        ctx.Opened += (s, ev) =>
        {
            bool isDet = _tabs.TryGetValue(newTab, out var tInfo) && tInfo.IsDetached;
            toggleWindowItem.Header = isDet ? "Dock to Tabs" : "Detach to Window";
        };
        ctx.Items.Add(toggleWindowItem);
        ctx.Items.Add(new Separator());

        MenuItem addToGroupItem = new MenuItem
        {
            Header = "Add to Group"
        };
        ctx.Items.Add(addToGroupItem);

        void UpdateGroupSubMenu()
        {
            PopulateAddToGroupMenu(addToGroupItem, newTab, info, title.Text);
        }

        UpdateGroupSubMenu();
        newTab.ContextRequested += (_, ev) =>
        {
            UpdateGroupSubMenu();
            if (!ctx.IsOpen)
                ctx.Open(newTab);
            ev.Handled = true;
        };

        newTab.AddHandler(InputElement.PointerPressedEvent, (s, ev) =>
        {
            var prop = ev.GetCurrentPoint(newTab).Properties;
            if (prop.IsRightButtonPressed)
            {
                ev.Handled = true;
                UpdateGroupSubMenu();
                Dispatcher.UIThread.Post(() =>
                {
                    if (!ctx.IsOpen)
                        ctx.Open(newTab);
                }, DispatcherPriority.Input);
                return;
            }

            if (!prop.IsLeftButtonPressed)
                return;

            if (ev.Source is Visual hit)
            {
                Visual? cur = hit;
                while (cur != null && cur != newTab)
                {
                    if (cur == closeBtn || cur == editTitle)
                        return;
                    cur = cur.GetVisualParent();
                }
            }

            _draggedTab = newTab;
            _dragStartPos = ev.GetPosition(this);
            _isDraggingTab = false;
            ev.Pointer.Capture(this);
            InstancesTabControl.SelectedItem = newTab;

            if (_isGridViewEnabled)
            {
                if (info.IsDetached)
                {
                    if (info.ChildHwnd != IntPtr.Zero)
                    {
                        PlatformRaiseChild(info.ChildHwnd);
                    }
                    return;
                }

                _isGridViewEnabled = false;
                UpdateGridViewBorderColor();
                foreach (var tInfo in _tabs.Values)
                {
                    if (tInfo.ChildHwnd != IntPtr.Zero && !tInfo.IsDetached)
                    {
                        PostChildMessage(tInfo.ChildHwnd, SkuaIpcMessages.GridView, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                DoReposition();
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        #endregion
    }

    #region Group Assignment Helper
    private string? GetTabLoggedInUsername(TabItem tab, TabInfo? info, string currentTitle)
    {
        if (info?.Process != null && !info.Process.HasExited)
        {
            try
            {
                string file = Path.Combine(ClientFileSources.TabsDIR, $"{info.Process.Id}.txt");
                if (File.Exists(file))
                {
                    string name = File.ReadAllText(file).Trim();
                    if (!string.IsNullOrWhiteSpace(name) &&
                        !name.StartsWith("User ", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith("Skua ", StringComparison.OrdinalIgnoreCase))
                    {
                        return name;
                    }
                }
            }
            catch { }
        }

        string title = currentTitle?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(title) &&
            !title.StartsWith("User ", StringComparison.OrdinalIgnoreCase) &&
            !title.StartsWith("Skua ", StringComparison.OrdinalIgnoreCase))
        {
            return title;
        }

        return null;
    }

    private void PopulateAddToGroupMenu(MenuItem parentItem, TabItem tab, TabInfo? info, string currentTitle)
    {
        parentItem.Items.Clear();

        string? loggedInUsername = GetTabLoggedInUsername(tab, info, currentTitle);
        if (string.IsNullOrWhiteSpace(loggedInUsername))
        {
            MenuItem notLoggedInItem = new MenuItem
            {
                Header = "Please log in first"
            };
            notLoggedInItem.Click += (s, ev) =>
            {
                Ioc.Default.GetService<IDialogService>()?.ShowMessageBox(
                    "Please log in to your game account first before adding it to an account group.",
                    "Account Not Logged In");
            };
            parentItem.Items.Add(notLoggedInItem);
            return;
        }

        string accountName = loggedInUsername;
        ISettingsService? settings = Ioc.Default.GetService<ISettingsService>();
        settings?.ReloadSettings();
        List<GroupData> groups = settings?.Get<List<GroupData>>("AccountGroups") ?? new List<GroupData>();

        if (groups.Count > 0)
        {
            foreach (var group in groups)
            {
                bool alreadyInGroup = group.Accounts.Contains(accountName, StringComparer.OrdinalIgnoreCase);
                MenuItem groupItem = new MenuItem
                {
                    Header = group.Name,
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked = alreadyInGroup
                };
                groupItem.Click += (s, ev) =>
                {
                    AddAccountToGroupAndManaged(accountName, group, settings);
                };
                parentItem.Items.Add(groupItem);
            }
            parentItem.Items.Add(new Separator());
        }
        else
        {
            MenuItem emptyItem = new MenuItem { Header = "No groups created", IsEnabled = false };
            parentItem.Items.Add(emptyItem);
            parentItem.Items.Add(new Separator());
        }

        MenuItem newGroupItem = new MenuItem
        {
            Header = "+ New Group..."
        };
        newGroupItem.Click += (s, ev) =>
        {
            var dialogService = Ioc.Default.GetService<IDialogService>();
            if (dialogService == null) return;

            var inputVm = new InputDialogViewModel("New Group", "Enter group name:", "Group Name", false);
            if (dialogService.ShowDialog(inputVm) == true && !string.IsNullOrWhiteSpace(inputVm.DialogTextInput))
            {
                string newGroupName = inputVm.DialogTextInput.Trim();
                var existing = groups.FirstOrDefault(g => g.Name.Equals(newGroupName, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = new GroupData { Name = newGroupName };
                    groups.Add(existing);
                }
                AddAccountToGroupAndManaged(accountName, existing, settings);
            }
        };
        parentItem.Items.Add(newGroupItem);
    }

    private void AddAccountToGroupAndManaged(string accountName, GroupData group, ISettingsService? settings)
    {
        if (settings == null) return;
        var dialogService = Ioc.Default.GetService<IDialogService>();

        settings.ReloadSettings();

        var accs = settings.Get<Dictionary<string, AccountData>>("ManagedAccounts") ?? new();
        if (!accs.ContainsKey(accountName))
        {
            accs[accountName] = new AccountData
            {
                DisplayName = accountName,
                Password = string.Empty,
                Tags = new List<string>(),
                UseCheck = false
            };
            settings.Set("ManagedAccounts", accs);
        }

        var currentGroups = settings.Get<List<GroupData>>("AccountGroups") ?? new List<GroupData>();
        var targetGroup = currentGroups.FirstOrDefault(g => g.Name.Equals(group.Name, StringComparison.OrdinalIgnoreCase));
        if (targetGroup == null)
        {
            targetGroup = new GroupData { Name = group.Name };
            currentGroups.Add(targetGroup);
        }

        if (!targetGroup.Accounts.Contains(accountName, StringComparer.OrdinalIgnoreCase))
        {
            targetGroup.Accounts.Add(accountName);
            settings.Set("AccountGroups", currentGroups);
            dialogService?.ShowMessageBox($"Added '{accountName}' to group '{targetGroup.Name}'.", "Add to Group");
        }
        else
        {
            dialogService?.ShowMessageBox($"'{accountName}' is already in group '{targetGroup.Name}'.", "Add to Group");
        }
    }
    #endregion
}
