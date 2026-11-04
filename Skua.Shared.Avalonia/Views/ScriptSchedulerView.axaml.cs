using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.Linq;

namespace Skua.Shared.Avalonia.Views;

public partial class ScriptSchedulerView : UserControl
{
    public ScriptSchedulerView()
    {
        InitializeComponent();
    }

    // Row numbers: each container carries its 1-based position in Tag, which the row template shows.
    private void QueueList_ContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        e.Container.Tag = (e.Index + 1).ToString();
    }

    private void QueueList_ContainerIndexChanged(object? sender, ContainerIndexChangedEventArgs e)
    {
        e.Container.Tag = (e.NewIndex + 1).ToString();
    }

    // Rename: the name's TextBlock and its TextBox share a Grid; double-click swaps to the box.
    private void Name_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not TextBlock name || name.Parent is not Grid cell || cell.Children.OfType<TextBox>().FirstOrDefault() is not { } box)
            return;

        box.Tag = box.Text;
        name.IsVisible = false;
        box.IsVisible = true;
        box.Focus();
        box.SelectAll();
        e.Handled = true;
    }

    private void NameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;

        if (e.Key == Key.Escape)
            box.Text = box.Tag as string;
        if (e.Key is Key.Enter or Key.Escape)
        {
            EndRename(box);
            e.Handled = true;
        }
    }

    private void NameBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
            EndRename(box);
    }

    private static void EndRename(TextBox box)
    {
        if (!box.IsVisible)
            return;

        // An empty name would leave nothing to double-click; keep the old one
        if (string.IsNullOrWhiteSpace(box.Text))
            box.Text = box.Tag as string;
        box.IsVisible = false;
        if (box.Parent is Grid cell && cell.Children.OfType<TextBlock>().FirstOrDefault() is { } name)
            name.IsVisible = true;
    }
}
