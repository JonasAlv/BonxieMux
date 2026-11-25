using Avalonia;
using Avalonia.Controls;

namespace Skua.Shared.Avalonia.Controls.Primitives;

/// <summary>
/// Standard section/page header with optional micro category label, bold title, muted description, and right-aligned action slot.
/// </summary>
public partial class SectionHeader : UserControl
{
    public static readonly StyledProperty<string?> CategoryProperty =
        AvaloniaProperty.Register<SectionHeader, string?>(nameof(Category));

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<SectionHeader, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SectionHeader, string?>(nameof(Description));

    public static readonly StyledProperty<object?> ActionContentProperty =
        AvaloniaProperty.Register<SectionHeader, object?>(nameof(ActionContent));

    public string? Category
    {
        get => GetValue(CategoryProperty);
        set => SetValue(CategoryProperty, value);
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

    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    public bool HasCategory => !string.IsNullOrWhiteSpace(Category);
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public SectionHeader()
    {
        InitializeComponent();
    }
}
