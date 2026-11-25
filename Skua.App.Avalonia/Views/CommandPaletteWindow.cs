using Skua.Core.Interfaces.ViewModels;
using Skua.Shared.Avalonia.Controls.Shell;
using System.Collections.Generic;

namespace Skua.App.Avalonia.Views;

public sealed class CommandPaletteWindow : QuickPickWindow
{
    public CommandPaletteWindow(IEnumerable<IMainMenuItemViewModel> menu)
        : base(BuildItems(menu), "Command palette", "Type a command or tool…")
    {
    }

    private static List<QuickPickItem> BuildItems(IEnumerable<IMainMenuItemViewModel> menu)
    {
        var items = new List<QuickPickItem>();
        foreach (IMainMenuItemViewModel top in menu)
            Collect(top, string.Empty, items);
        return items;
    }

    private static void Collect(IMainMenuItemViewModel item, string path, List<QuickPickItem> into)
    {
        string header = item.Header ?? string.Empty;
        if (item.SubItems is { Count: > 0 } children)
        {
            foreach (IMainMenuItemViewModel child in children)
                Collect(child, path.Length == 0 ? header : $"{path} › {header}", into);
            return;
        }

        if (header.Length > 0 && item.Command is not null)
        {
            into.Add(new QuickPickItem(path, header, null, () => item.Command.Execute(null)));
        }
    }
}
