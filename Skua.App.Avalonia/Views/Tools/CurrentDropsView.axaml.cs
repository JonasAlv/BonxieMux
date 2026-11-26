using Avalonia.Controls;
using Skua.App.Avalonia.ViewModels;
using System.Collections.Generic;
using System.Linq;

namespace Skua.App.Avalonia.Views;

public partial class CurrentDropsView : UserControl
{
    public CurrentDropsView()
    {
        InitializeComponent();
    }

    private void DropsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        int count = DropsList.SelectedItems?.Count ?? 0;
        SelectedCountText.Text = count == 0 ? "None selected" : $"{count} selected";
    }

    private void PickupSelected_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is CurrentDropsViewModel vm && DropsList.SelectedItems is not null)
        {
            List<object> selected = DropsList.SelectedItems.Cast<object>().ToList();
            if (selected.Count > 0)
                vm.PickupSelectedCommand.Execute(selected);
            else if (vm.SelectedDrop is not null)
                vm.PickupCommand.Execute(null);
        }
    }
}
