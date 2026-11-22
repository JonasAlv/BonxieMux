using Avalonia.Controls;
using Avalonia.Threading;

namespace Skua.Shared.Avalonia.Controls.ScriptRepo;

public partial class ScriptRepoControl : UserControl
{
    public ScriptRepoControl()
    {
        InitializeComponent();
        ScriptList.AddHandler(ScrollViewer.ScrollChangedEvent, ScriptList_ScrollChanged);
    }

    private void ScriptList_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta.Y == 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (ScriptList.ItemsPanelRoot is { } panel)
            {
                panel.InvalidateMeasure();
                panel.InvalidateArrange();
            }
        }, DispatcherPriority.Background);
    }
}
