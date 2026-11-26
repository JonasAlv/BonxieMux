using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Skua.Manager.Avalonia.UserControls;

public partial class SchedulerRunDiagnosticsPanel : UserControl
{
    public SchedulerRunDiagnosticsPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
