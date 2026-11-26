using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Skua.Manager.Avalonia.ViewModels;

namespace Skua.Manager.Avalonia.UserControls;

public partial class ArmyQueueAccountPickerOverlay : UserControl
{
    private TopLevel? _topLevel;

    public ArmyQueueAccountPickerOverlay()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(InputElement.KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(InputElement.KeyDownEvent, OnTopLevelKeyDown);
        _topLevel = null;
    }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape &&
            DataContext is ArmyQueueViewModel { ShowAccountPicker: true } vm)
        {
            vm.CancelAccountPickerCommand.Execute(null);
            e.Handled = true;
        }
    }
}
