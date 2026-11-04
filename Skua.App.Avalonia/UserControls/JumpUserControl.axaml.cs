using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Skua.App.Avalonia.ViewModels;

namespace Skua.App.Avalonia.UserControls;

public partial class JumpUserControl : UserControl
{
    public JumpUserControl()
    {
        InitializeComponent();
        Cells.DropDownOpened += Cells_DropDownOpened;
        AddHandler(InputElement.KeyDownEvent, Cells_KeyDown, RoutingStrategies.Tunnel);
    }

    private void Cells_DropDownOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is JumpViewModel vm)
            vm.UpdateCellsCommand.Execute(null);
    }

    private void Cells_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!Cells.IsFocused || Cells.ItemCount == 0)
            return;

        int direction = e.Key switch
        {
            Key.Up => -1,
            Key.Down => 1,
            _ => 0
        };

        if (direction == 0)
            return;

        int selectedIndex = Cells.SelectedIndex;
        if (selectedIndex < 0)
            selectedIndex = direction > 0 ? -1 : Cells.ItemCount;

        int nextIndex = System.Math.Clamp(selectedIndex + direction, 0, Cells.ItemCount - 1);

        // ComboBox treats arrow keys in an open dropdown as preview navigation and
        // waits for Enter/click to commit. Close it before applying the selection so
        // SelectedCell changes immediately and triggers the existing auto-jump.
        Cells.IsDropDownOpen = false;
        if (nextIndex != Cells.SelectedIndex)
            Cells.SelectedIndex = nextIndex;

        Cells.Focus();
        e.Handled = true;
    }
}