using CommunityToolkit.Mvvm.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Skua.Shared.Avalonia.ViewModels;

namespace Skua.Shared.Avalonia.Views;

/// <summary>
/// Interaction logic for AboutView.xaml
/// </summary>
public partial class AboutView : UserControl
{
    public AboutView()
    {
        global::Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        DataContext = Ioc.Default.GetRequiredService<AboutViewModel>();
        // Markdownview.MarkdownStyle = MarkdownStyle.SasabuneStandard;
    }

    // private void Markdownview_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    // {
    //     Dispatcher.BeginInvoke(new Action(() =>
    //     {
    //         SubscribeToAllHyperlinks(Markdownview.Document);
    //     }), DispatcherPriority.Loaded);
    // }

    // private void SubscribeToAllHyperlinks(FlowDocument flowDocument)
    // {
    //     foreach (Hyperlink link in GetVisuals(flowDocument).OfType<Hyperlink>())
    //         link.Command = ((AboutViewModel)DataContext).NavigateCommand;
    // }

    // private IEnumerable<DependencyObject> GetVisuals(DependencyObject root)
    // {
    //     foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
    //     {
    //         yield return child;
    //         foreach (DependencyObject descendants in GetVisuals(child))
    //             yield return descendants;
    //     }
    // }

    // private void link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    // {
    //     Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
    //     e.Handled = true;
    // }
}

