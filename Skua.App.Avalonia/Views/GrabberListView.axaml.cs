using Avalonia.Controls;
using Skua.App.Avalonia.ViewModels.Grabber;
using System.Collections.Generic;
using System.Linq;

namespace Skua.App.Avalonia.Views;

public partial class GrabberListView : UserControl
{
    public GrabberListView()
    {
        InitializeComponent();
    }

    private List<object> SelectedGrabbedItems()
    {
        if (GrabbedItemsList.SelectedItems is null)
            return new List<object>();

        return GrabbedItemsList.SelectedItems.Cast<object>().ToList();
    }

    private void GrabbedItemsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        int count = GrabbedItemsList.SelectedItems?.Count ?? 0;
        SelectedCountText.Text = count == 0 ? string.Empty : $"{count} selected";
    }

    private void UnselectAll_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        GrabbedItemsList.SelectedItems?.Clear();
    }

    private void GrabberTaskButton_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: GrabberTaskViewModel taskVm })
            return;

        taskVm.GrabberTaskCommand.Execute(SelectedGrabbedItems());
    }

    private void CopyButton_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not GrabberListViewModel vm)
            return;

        vm.Copy(SelectedGrabbedItems());
    }

    private void ExportButton_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not GrabberListViewModel vm)
            return;

        vm.ExportToFile(SelectedGrabbedItems());
    }
}
