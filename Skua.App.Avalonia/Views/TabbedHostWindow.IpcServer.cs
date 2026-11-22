using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.App.Avalonia.Services;
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
    private void StartPipeServer()
    {
        Task.Run(async () =>
        {
            while (!_isClosing && !_pipeCts.Token.IsCancellationRequested)
            {
                NamedPipeServerStream? pipeServer = null;
                try
                {
                    pipeServer = new NamedPipeServerStream(
                        App.HostPipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    using var reg = _pipeCts.Token.Register(() =>
                    {
                        try { pipeServer?.Dispose(); } catch { }
                    });

                    await pipeServer.WaitForConnectionAsync(_pipeCts.Token);

                    using (pipeServer)
                    using (StreamReader sr = new(pipeServer))
                    {
                        string? line = await sr.ReadLineAsync();
                        if (!string.IsNullOrWhiteSpace(line))
                            HandlePipeMessage(line);
                    }
                }
                catch (OperationCanceledException)
                {
                    try { pipeServer?.Dispose(); } catch { }
                    break;
                }
                catch
                {
                    try { pipeServer?.Dispose(); } catch { }
                    if (_isClosing || _pipeCts.Token.IsCancellationRequested)
                        break;
                    await Task.Delay(100);
                }
            }

            if (Path.IsPathRooted(App.HostPipeName))
            {
                try
                {
                    if (File.Exists(App.HostPipeName))
                        File.Delete(App.HostPipeName);
                }
                catch { }
            }
        });
    }

    private void HandlePipeMessage(string line)
    {
        if (string.Equals(line, "FULLSCREEN", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.UIThread.Post(() => ToggleFullScreen());
            return;
        }

        if (string.Equals(line, "GAMEONLY", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.UIThread.Post(ToggleGameOnly);
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
            if (parts.Length >= 4 &&
                bool.TryParse(parts[0], out bool useAccentOnMainTitleBar) &&
                bool.TryParse(parts[1], out bool isDark) &&
                Color.TryParse(parts[2], out Color primary) &&
                Color.TryParse(parts[3], out Color foreground))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (Ioc.Default.GetService<IThemeService>() is Skua.Shared.Avalonia.Services.AvaloniaThemeService themeService)
                        {
                            themeService.ApplyExternalThemeSnapshot(isDark, primary, foreground, useAccentOnMainTitleBar);
                            MainTitleBar.UseAccentBackground = useAccentOnMainTitleBar;
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
