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

    // Rows differ in height (wrapped tags). Scrolling up, the virtualizing panel places newly realized rows from an
    // estimated height and can leave a row overlapping its neighbour; laying it out again once the scroll settles
    // uses the measured heights and puts every row back in place.
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
