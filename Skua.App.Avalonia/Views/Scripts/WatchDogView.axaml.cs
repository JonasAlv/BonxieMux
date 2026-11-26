using Avalonia.Controls;
using Avalonia.Threading;
using WatchDog.Shared;

namespace Skua.App.Avalonia.Views;

public partial class WatchDogView : UserControl
{
    private readonly DispatcherTimer _statusTimer = new() { Interval = System.TimeSpan.FromSeconds(1) };
    public WatchDogView()
    {
        InitializeComponent();
        _statusTimer.Tick += (_, _) => RefreshStatus();
        AttachedToVisualTree += (_, _) => { RefreshStatus(); _statusTimer.Start(); };
        DetachedFromVisualTree += (_, _) => _statusTimer.Stop();
    }
    private void RefreshStatus()
    {
        this.FindControl<TextBlock>("RescueLiveStatus")!.Text = RescueStatus.Describe(RescueStatus.Latest, System.DateTime.UtcNow);
        (DataContext as ViewModels.Options.WatchDogViewModel)?.RefreshAccounts();
    }
}
