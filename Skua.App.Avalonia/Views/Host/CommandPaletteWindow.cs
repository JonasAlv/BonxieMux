using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;
using Skua.Shared.Avalonia.Controls.Shell;
using Skua.Shared.Avalonia.Services;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

namespace Skua.App.Avalonia.Views;

public sealed class CommandPaletteWindow : QuickPickWindow
{
    public CommandPaletteWindow(IEnumerable<IMainMenuItemViewModel> menu, IEnumerable<IMainMenuItemViewModel>? plugins = null)
        : base(BuildItems(menu, plugins), "Command palette", "Type a command, plugin, or preference…")
    {
    }

    internal static Dictionary<string, string> LoadHotKeyMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Ioc.Default.GetService<ISettingsService>()?.Get<StringCollection>("HotKeys") is not { } hotkeys)
            return map;

        foreach (string? entry in hotkeys)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            int sep = entry.IndexOf('|');
            if (sep <= 0 || sep + 1 >= entry.Length)
                continue;
            string action = entry[..sep].Trim();
            string gesture = entry[(sep + 1)..].Trim();
            if (action.Length > 0 && gesture.Length > 0)
                map[action] = gesture;
        }
        return map;
    }

    private static List<QuickPickItem> BuildItems(
        IEnumerable<IMainMenuItemViewModel> menu,
        IEnumerable<IMainMenuItemViewModel>? plugins)
    {
        var items = new List<QuickPickItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> hotkeys = LoadHotKeyMap();

        foreach (IMainMenuItemViewModel top in menu)
            Collect(top, string.Empty, items, seen, hotkeys);

        if (plugins is not null)
        {
            foreach (IMainMenuItemViewModel plugin in plugins)
                Collect(plugin, "Plugins", items, seen, hotkeys);
        }

        if (Ioc.Default.GetService<IPluginHelper>() is { } pluginHelper)
        {
            foreach (IMainMenuItemViewModel btn in pluginHelper.MenuButtons)
                Collect(btn, "Plugins", items, seen, hotkeys);
        }

        if (Ioc.Default.GetService<IThemeService>() is AvaloniaThemeService themeService)
            AddThemePreferenceItems(items, themeService);

        return items;
    }

    private static string? ResolveShortcut(string header, Dictionary<string, string> hotkeys)
    {
        string? action = header.Trim().ToLowerInvariant() switch
        {
            "load script" or "script loader" => "LoadScript",
            "toggle script" or "start/stop script" => "ToggleScript",
            "bank" or "open bank" => "OpenBank",
            "lag killer" or "toggle lag killer" => "ToggleLagKiller",
            "console" or "open console" => "OpenConsole",
            "search scripts" or "get scripts" => "SearchScripts",
            "quick launch" => "OpenQuickLaunch",
            "toggle fullscreen" or "fullscreen" => "ToggleFullscreen",
            "game only" or "game fullscreen" => "ToggleGameFullScreen",
            _ => null
        };
        return action is not null && hotkeys.TryGetValue(action, out string? gesture) ? gesture : null;
    }

    private static void Collect(
        IMainMenuItemViewModel item,
        string path,
        List<QuickPickItem> into,
        HashSet<string> seen,
        Dictionary<string, string> hotkeys)
    {
        string header = item.Header ?? string.Empty;
        if (item.SubItems is { Count: > 0 } children)
        {
            foreach (IMainMenuItemViewModel child in children)
                Collect(child, path.Length == 0 ? header : $"{path} › {header}", into, seen, hotkeys);
            return;
        }

        if (header.Length > 0 && item.Command is not null)
        {
            string key = $"{path}|{header}";
            if (seen.Add(key))
                into.Add(new QuickPickItem(path, header, null, () => item.Command.Execute(null), ResolveShortcut(header, hotkeys)));
        }
    }
}
