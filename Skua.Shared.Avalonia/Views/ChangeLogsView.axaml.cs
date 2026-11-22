using CommunityToolkit.Mvvm.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Skua.Shared.Avalonia.ViewModels;

namespace Skua.Shared.Avalonia.Views;

public partial class ChangeLogsView : UserControl
{
    public ChangeLogsView()
    {
        global::Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        DataContext = Ioc.Default.GetRequiredService<ChangeLogsViewModel>();
    }

}

