using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace Skua.Shared.Avalonia.Views;

/// <summary>
/// Interaction logic for ScriptRepoView.xaml
/// </summary>
public partial class ScriptRepoView : UserControl
{
    public ScriptRepoView()
    {
        global::Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }
}

