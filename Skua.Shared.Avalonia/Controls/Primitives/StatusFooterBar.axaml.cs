using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Skua.Shared.Avalonia.Theming;

namespace Skua.Shared.Avalonia.Controls.Primitives;

/// <summary>
/// Compact bottom status/action bar with left status content and right action controls.
/// </summary>
public partial class StatusFooterBar : UserControl
{
    public static readonly StyledProperty<object?> LeftContentProperty =
        AvaloniaProperty.Register<StatusFooterBar, object?>(nameof(LeftContent));

    public static readonly StyledProperty<object?> RightContentProperty =
        AvaloniaProperty.Register<StatusFooterBar, object?>(nameof(RightContent));

    public static readonly StyledProperty<bool> ShowTopBorderProperty =
        AvaloniaProperty.Register<StatusFooterBar, bool>(nameof(ShowTopBorder), true);

    public object? LeftContent
    {
        get => GetValue(LeftContentProperty);
        set => SetValue(LeftContentProperty, value);
    }

    public object? RightContent
    {
        get => GetValue(RightContentProperty);
        set => SetValue(RightContentProperty, value);
    }

    public bool ShowTopBorder
    {
        get => GetValue(ShowTopBorderProperty);
        set => SetValue(ShowTopBorderProperty, value);
    }

    public StatusFooterBar()
    {
        InitializeComponent();
        ApplyChrome();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowTopBorderProperty)
            ApplyChrome();
    }

    private void ApplyChrome()
    {
        if (FooterBorder is null)
            return;

        if (ShowTopBorder)
        {
            UiTokens.Bind(FooterBorder, Border.BackgroundProperty, UiTokens.SkuaSurfaceAltBrush);
            FooterBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            FooterBorder.Padding = new Thickness(12, 6);
        }
        else
        {
            FooterBorder.Background = Brushes.Transparent;
            FooterBorder.BorderThickness = new Thickness(0);
            FooterBorder.Padding = new Thickness(0);
        }
    }
}
