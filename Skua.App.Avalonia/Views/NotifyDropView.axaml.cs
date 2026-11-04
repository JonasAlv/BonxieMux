using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Skua.App.Avalonia.ViewModels.Runtime;
using System.Collections.Generic;
using System.Linq;

namespace Skua.App.Avalonia.Views;

public partial class NotifyDropView : UserControl
{
    public NotifyDropView() => InitializeComponent();

    private void RemoveSelected_Click(object? sender, RoutedEventArgs e)
    {
        RemoveSelectedItems();
    }

    private void NotifyDropListBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            RemoveSelectedItems();
            e.Handled = true;
        }
    }

    private void RemoveSelectedItems()
    {
        if (DataContext is not NotifyDropViewModel vm || NotifyDropListBox.SelectedItems is null)
            return;

        List<object> selected = NotifyDropListBox.SelectedItems.Cast<object>().ToList();
        if (selected.Count > 0 && vm.RemoveDropsCommand.CanExecute(selected))
        {
            vm.RemoveDropsCommand.Execute(selected);
        }
    }
}
