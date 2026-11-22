using Avalonia;
using Avalonia.Controls;

namespace Skua.Shared.Avalonia.Views;

public partial class ApplicationThemesSharedView : UserControl
{
    public static readonly StyledProperty<bool> IsManagerProperty =
        AvaloniaProperty.Register<ApplicationThemesSharedView, bool>(nameof(IsManager));

    public bool IsManager
    {
        get => GetValue(IsManagerProperty);
        set => SetValue(IsManagerProperty, value);
    }

    public ApplicationThemesSharedView()
    {
        InitializeComponent();
    }
}
