using Avalonia.Controls;
using Avalonia.Interactivity;
using Skua.App.Avalonia.ViewModels.Packets;
using System;
using System.Linq;

namespace Skua.App.Avalonia.Views;

public partial class PacketInterceptorView : UserControl
{
    public PacketInterceptorView()
    {
        InitializeComponent();
    }

    private async void CopySelectedPackets_Click(object? sender, RoutedEventArgs e)
    {
        if (PacketsListBox?.SelectedItems == null || PacketsListBox.SelectedItems.Count == 0)
            return;

        var selectedTexts = PacketsListBox.SelectedItems
            .OfType<InterceptedPacketViewModel>()
            .Select(p => p.Packet);

        string textToCopy = string.Join(Environment.NewLine, selectedTexts);
        if (string.IsNullOrEmpty(textToCopy))
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard != null)
        {
            await topLevel.Clipboard.SetTextAsync(textToCopy);
        }
    }

    private async void CopyAllPackets_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PacketInterceptorViewModel vm || vm.FilteredPackets.Count == 0)
            return;

        string textToCopy = string.Join(Environment.NewLine, vm.FilteredPackets.Select(p => p.Packet));
        if (string.IsNullOrEmpty(textToCopy))
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard != null)
        {
            await topLevel.Clipboard.SetTextAsync(textToCopy);
        }
    }

    private void UnselectAllPackets_Click(object? sender, RoutedEventArgs e)
    {
        PacketsListBox?.SelectedItems?.Clear();
    }
}
