using Avalonia.Threading;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;

namespace Skua.Shared.Avalonia.Services;

/// <summary>
/// Broadcasts theme updates between BonxieMux Manager and BonxieMux host instances via IPC named pipes.
/// </summary>
public static class ManagerThemeBroadcaster
{
    private static int _managerListenerStarted;

    public static bool IsSyncEnabled(ISettingsService settingsService)
    {
        try
        {
            return settingsService.Get("syncTheme", false) || settingsService.GetShared().SyncManagerAndAppTheme;
        }
        catch
        {
            return settingsService.Get("syncTheme", false);
        }
    }

    public static void BroadcastIfEnabled(AvaloniaThemeService themeService, ISettingsService settingsService)
    {
        if (!IsSyncEnabled(settingsService) || themeService.IsApplyingExternalTheme)
            return;

        Broadcast(themeService);
    }

    public static void BroadcastIfEnabledToManager(AvaloniaThemeService themeService, ISettingsService settingsService)
    {
        if (!IsSyncEnabled(settingsService) || themeService.IsApplyingExternalTheme)
            return;

        BroadcastToManager(themeService);
    }

    public static void Broadcast(AvaloniaThemeService themeService)
    {
        try
        {
            string tabsDir = ClientFileSources.TabsDIR;
            HashSet<string> targetPipes = new(StringComparer.OrdinalIgnoreCase);

            string defaultHostPipe = Path.Combine(tabsDir, "host.pipe");
            targetPipes.Add(defaultHostPipe);

            if (Directory.Exists(tabsDir))
            {
                foreach (string pipePath in Directory.GetFiles(tabsDir, "host*.pipe"))
                {
                    targetPipes.Add(pipePath);
                }
            }

            string message = BuildThemeMessage(themeService);

            foreach (string pipePath in targetPipes)
            {
                SendPipeMessageAsync(pipePath, message);
            }
        }
        catch { }
    }

    public static void BroadcastToManager(AvaloniaThemeService themeService)
    {
        try
        {
            string managerPipe = Path.Combine(ClientFileSources.TabsDIR, "manager-theme.pipe");
            SendPipeMessageAsync(managerPipe, BuildThemeMessage(themeService));
        }
        catch { }
    }

    public static void StartManagerThemeListener(Action<ThemeItem, bool> onThemeReceived)
    {
        if (System.Threading.Interlocked.Exchange(ref _managerListenerStarted, 1) != 0)
            return;

        string pipePath = Path.Combine(ClientFileSources.TabsDIR, "manager-theme.pipe");
        _ = Task.Run(async () =>
        {
            while (true)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    Directory.CreateDirectory(ClientFileSources.TabsDIR);
                    server = new NamedPipeServerStream(
                        pipePath,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync();
                    using StreamReader reader = new(server);
                    string? line = await reader.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line) && line.StartsWith("THEME|", StringComparison.Ordinal))
                    {
                        string themePayload = line.Substring("THEME|".Length);
                        bool useAccentOnMainTitleBar = true;
                        string themeCsv = themePayload;
                        int separatorIndex = themePayload.IndexOf('|');
                        if (separatorIndex >= 0 &&
                            bool.TryParse(themePayload[..separatorIndex], out bool parsedAccent))
                        {
                            useAccentOnMainTitleBar = parsedAccent;
                            themeCsv = themePayload[(separatorIndex + 1)..];
                        }

                        ThemeItem? themeItem = ThemeItem.FromString(themeCsv);
                        if (themeItem is not null)
                        {
                            Dispatcher.UIThread.Post(() => onThemeReceived(themeItem, useAccentOnMainTitleBar));
                        }
                    }
                }
                catch
                {
                    await Task.Delay(250);
                }
                finally
                {
                    server?.Dispose();
                }
            }
        });
    }

    private static string BuildThemeMessage(AvaloniaThemeService themeService)
    {
        ThemeItem snapshot = new()
        {
            Name = "ManagerSync",
            IsDarkTheme = themeService.IsDarkTheme,
            PrimaryColor = themeService.PrimaryColor,
            SecondaryColor = themeService.SecondaryColor,
            PrimaryForegroundColor = themeService.PrimaryForegroundColor,
            SecondaryForegroundColor = themeService.SecondaryForegroundColor,
            UseColorAdjustment = false,
            DesiredContrastRatio = themeService.DesiredContrastRatio,
            ContrastValue = themeService.ContrastValue?.ToString() ?? "Medium",
            ColorSelectionValue = themeService.ColorSelectionValue?.ToString() ?? "All"
        };

        bool useAccentOnMainTitleBar = themeService.UseAccentOnMainTitleBar;
        return $"THEME|{useAccentOnMainTitleBar}|{snapshot.ConvertToString()}";
    }

    private static void SendPipeMessageAsync(string pipePath, string message)
    {
        _ = Task.Run(() =>
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipePath, PipeDirection.Out);
                client.Connect(150);
                using StreamWriter sw = new(client);
                sw.WriteLine(message);
                sw.Flush();
            }
            catch { }
        });
    }
}
