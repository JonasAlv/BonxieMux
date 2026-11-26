using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.App.Avalonia.Services;
using Skua.Backend.Ipc;
using Skua.Core.Interfaces;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Tasks;

namespace Skua.App.Avalonia.Views;

public partial class TabbedHostWindow
{
    private HostIpcServer? _ipcServer;

    private void StartPipeServer()
    {
        _ipcServer ??= new HostIpcServer(App.HostPipeName, HandlePipeMessage);
        _ipcServer.Start();
    }

    private void HandlePipeMessage(string line)
    {
        if (string.Equals(line, "FULLSCREEN", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (Skua.Core.AppStartup.HotKeys.ActiveChildHwnd != IntPtr.Zero &&
                    _detachedContexts.TryGetValue(Skua.Core.AppStartup.HotKeys.ActiveChildHwnd, out DetachedWindowContext? detCtx))
                {
                    ToggleDetachedFullScreen(detCtx);
                    return;
                }
                ToggleFullScreen();
            });
            return;
        }

        if (string.Equals(line, "GAMEONLY", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (Skua.Core.AppStartup.HotKeys.ActiveChildHwnd != IntPtr.Zero &&
                    _detachedContexts.TryGetValue(Skua.Core.AppStartup.HotKeys.ActiveChildHwnd, out DetachedWindowContext? detCtx))
                {
                    ToggleDetachedGameOnly(detCtx);
                    return;
                }
                ToggleGameOnly();
            });
            return;
        }

        if (string.Equals(line, "QUICKLAUNCH", StringComparison.OrdinalIgnoreCase))
        {
            OpenQuickLaunchFromHotkey();
            return;
        }

        if (line.StartsWith("ARTWORK|", StringComparison.Ordinal))
        {
            string encodedPath = line["ARTWORK|".Length..];
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    string artworkPath = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedPath));
                    Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar.SetArtworkPath(
                        string.IsNullOrWhiteSpace(artworkPath) ? null : artworkPath);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"BonxieMux artwork sync failed: {ex.GetType().Name}: {ex.Message}");
                }
            });
            return;
        }

        if (line.StartsWith("THEME|", StringComparison.Ordinal))
        {
            string[] parts = line["THEME|".Length..].Split('|');
            if (parts.Length >= 3 &&
                bool.TryParse(parts[0], out bool isDark) &&
                Color.TryParse(parts[1], out Color primary) &&
                Color.TryParse(parts[2], out Color foreground))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (Ioc.Default.GetService<IThemeService>() is Skua.Shared.Avalonia.Services.AvaloniaThemeService themeService)
                        {
                            string? activeThemeName = parts.Length >= 4 ? parts[3] : null;
                            bool? isComfortableDensity = parts.Length >= 5 && bool.TryParse(parts[4], out bool cd) ? cd : null;
                            themeService.ApplyExternalThemeSnapshot(isDark, primary, foreground, activeThemeName, isComfortableDensity);
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"BonxieMux theme sync failed: {ex.GetType().Name}: {ex.Message}");
                    }
                });
            }
            return;
        }

        if (line.StartsWith("DOCK|", StringComparison.Ordinal))
        {
            if (int.TryParse(line["DOCK|".Length..], out int childPid))
                Dispatcher.UIThread.Post(() => DockChildInstance(childPid));
            return;
        }

        if (line.StartsWith("AUTO_STATE|", StringComparison.Ordinal))
        {
            string rest = line["AUTO_STATE|".Length..];
            int sep = rest.IndexOf('|');
            if (sep > 0 && int.TryParse(rest[..sep], out int childPid))
            {
                string json = rest[(sep + 1)..];
                Dispatcher.UIThread.Post(() => ApplyChildAutoState(childPid, json));
            }
            return;
        }

        if (line.StartsWith("JUMP_STATE|", StringComparison.Ordinal))
        {
            string rest = line["JUMP_STATE|".Length..];
            int sep = rest.IndexOf('|');
            if (sep > 0 && int.TryParse(rest[..sep], out int childPid))
            {
                string json = rest[(sep + 1)..];
                Dispatcher.UIThread.Post(() => ApplyChildJumpState(childPid, json));
            }
            return;
        }

        if (line.StartsWith("MUGETSU_SETTING|", StringComparison.Ordinal))
        {
            string[] parts = line.Split('|', 3);
            if (parts.Length == 3)
            {
                if (parts[1] == "uncapped")
                    Controls.GameHost.SetAllUncapped(parts[2] == "true");
                else if (parts[1] == "interpolation")
                    Controls.GameHost.SetAllInterpolation(parts[2] == "true");

                var payload = new { input = "mugetsu_setting", name = parts[1], value = parts[2] };
                foreach (TabInfo tab in _tabs.Values)
                {
                    if (tab.Process is { HasExited: false } proc)
                        TabIpc.SendInput(proc.Id, payload);
                }
            }
            return;
        }

        if (line.StartsWith("HEADLESS_STATE|", StringComparison.Ordinal))
        {
            string[] parts = line.Split('|');
            if (parts.Length >= 5 &&
                int.TryParse(parts[1], out int childPid) &&
                long.TryParse(parts[4], out long seq))
            {
                bool manual = parts[2] == "1";
                bool auto = parts[3] == "1";
                Dispatcher.UIThread.Post(() => ApplyChildHeadlessState(childPid, manual, auto, seq));
            }
            return;
        }

        if (line.StartsWith("SCRIPT_STATE|", StringComparison.Ordinal))
        {
            string[] parts = line.Split('|', 5);
            if (parts.Length >= 5 && int.TryParse(parts[1], out int childPid))
            {
                bool isRunning = parts[2] == "1";
                bool isPaused = parts[3] == "1";
                string scriptName = parts[4];
                Dispatcher.UIThread.Post(() => ApplyChildScriptState(childPid, isRunning, isPaused, scriptName));
            }
            return;
        }

        if (line.StartsWith("GLOBAL_OPT|", StringComparison.Ordinal))
        {
            string rest = line["GLOBAL_OPT|".Length..];
            int sep = rest.IndexOf('|');
            if (sep > 0)
            {
                string name = rest[..sep];
                string val = rest[(sep + 1)..];
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        var hostOpts = Ioc.Default.GetRequiredService<IScriptOption>();
                        Skua.Core.Services.OptionSharing.Apply(hostOpts, new Skua.Core.Services.SharedOptionCommand("set", name, val));
                        if (name.StartsWith("AutoHeadless", StringComparison.Ordinal))
                            RefreshHeadlessOverlays();
                    }
                    catch { }
                });
            }
            return;
        }

        string[]? parsedArgs = null;
        try
        {
            if (line.TrimStart().StartsWith("["))
                parsedArgs = JsonSerializer.Deserialize<string[]>(line);
        }
        catch { }

        parsedArgs ??= ParseRawCommandLine(line);
        Dispatcher.UIThread.Post(() => AddNewInstance(parsedArgs));
    }
}
