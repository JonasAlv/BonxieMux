using Avalonia.Controls;
using Skua.Shared.Avalonia.ViewModels;
using System;

namespace Skua.Shared.Avalonia.Views;

public partial class LoadoutsView : UserControl
{
    public LoadoutsView()
    {
        global::Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }

    private void ProfileComboBox_DropDownOpened(object? sender, EventArgs e)
    {
        if (DataContext is LoadoutsViewModel vm)
            vm.RefreshProfiles();
    }
}
