using Avalonia;
using Avalonia.Controls;
using Skua.Shared.Avalonia.Controls.Icons;
using System.Windows.Input;

namespace Skua.Shared.Avalonia.Controls.Primitives;

/// <summary>
/// Reusable centered empty or loading state view with icon, title, optional progress bar, description, and CTA button.
/// </summary>
public partial class EmptyStateView : UserControl
{
    public static readonly StyledProperty<CodiconKind> IconKindProperty =
        AvaloniaProperty.Register<EmptyStateView, CodiconKind>(nameof(IconKind), CodiconKind.Search);

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<EmptyStateView, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<EmptyStateView, string?>(nameof(Description));

    public static readonly StyledProperty<string?> ActionTextProperty =
        AvaloniaProperty.Register<EmptyStateView, string?>(nameof(ActionText));

    public static readonly StyledProperty<ICommand?> ActionCommandProperty =
        AvaloniaProperty.Register<EmptyStateView, ICommand?>(nameof(ActionCommand));

    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<EmptyStateView, bool>(nameof(IsLoading));

    public CodiconKind IconKind
    {
        get => GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string? ActionText
    {
        get => GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public ICommand? ActionCommand
    {
        get => GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasAction => !string.IsNullOrWhiteSpace(ActionText);

    public EmptyStateView()
    {
        InitializeComponent();
    }
}
