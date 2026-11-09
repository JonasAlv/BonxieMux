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
/// Broadcasts theme updates from BonxieMux Manager to any running BonxieMux host instances via IPC named pipes.
/// </summary>
public static class ManagerThemeBroadcaster
{
    public static void BroadcastIfEnabled(AvaloniaThemeService themeService, ISettingsService settingsService)
    {
        if (!settingsService.Get("syncTheme", false))
            return;

        Broadcast(themeService);
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
            string message = $"THEME|{useAccentOnMainTitleBar}|{snapshot.ConvertToString()}";

            foreach (string pipePath in targetPipes)
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
        catch { }
    }
}
