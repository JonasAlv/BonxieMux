using Avalonia.Controls;
using Skua.App.Avalonia.ViewModels;
using Avalonia.Interactivity;

namespace Skua.App.Avalonia.Views;

public partial class ScriptLoaderView : UserControl
{
    public ScriptLoaderView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => (DataContext as ScriptLoaderViewModel)?.SetAccountPolling(true);
        DetachedFromVisualTree += (_, _) => (DataContext as ScriptLoaderViewModel)?.SetAccountPolling(false);
    }

    private async void CopyLog_Click(object? sender, RoutedEventArgs e)
    {
        await ScriptLog.CopySelectedOrAllAsync();
    }

    private void DeselectLog_Click(object? sender, RoutedEventArgs e)
    {
        ScriptLog.DeselectAll();
    }
}
