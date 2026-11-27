using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using System;

namespace Skua.Shared.Avalonia.Controls.Icons;

/// <summary>
/// Renders an official Microsoft VS Code Codicon glyph from the embedded <c>codicon.ttf</c> font.
/// Inherits from <see cref="Decorator"/> (not <see cref="TextBlock"/>) so <c>OfType&lt;TextBlock&gt;()</c>
/// queries and global <c>TextBlock</c> styles never collide with icon controls.
/// Automatically tracks <see cref="Layoutable.WidthProperty"/> / <see cref="Layoutable.HeightProperty"/>
/// so dynamic density tokens (<c>UiIconSm</c>, <c>UiIconMd</c>, <c>UiActivityIconSize</c>) resize crisp vector icons live.
/// </summary>
public class Codicon : Decorator
{
    public static readonly FontFamily CodiconFont = new("avares://Skua.Shared.Avalonia/Assets#codicon");

    public static readonly StyledProperty<CodiconKind> KindProperty =
        AvaloniaProperty.Register<Codicon, CodiconKind>(nameof(Kind), CodiconKind.None);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Codicon>();

    private readonly TextBlock _glyph;

    public CodiconKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    static Codicon()
    {
        KindProperty.Changed.AddClassHandler<Codicon>((icon, _) => icon.UpdateGlyph());
        WidthProperty.Changed.AddClassHandler<Codicon>((icon, _) => icon.SyncFontSize());
        HeightProperty.Changed.AddClassHandler<Codicon>((icon, _) => icon.SyncFontSize());
    }

    public Codicon()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;

        _glyph = new TextBlock
        {
            FontFamily = CodiconFont,
            FontWeight = FontWeight.Normal,
            FontStyle = FontStyle.Normal,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        Child = _glyph;
        SyncFontSize();
        UpdateGlyph();
    }

    private void UpdateGlyph()
    {
        int codepoint = (int)Kind;
        _glyph.Text = codepoint > 0 ? char.ConvertFromUtf32(codepoint) : string.Empty;
    }

    private void SyncFontSize()
    {
        double size = !double.IsNaN(Width) && Width > 0
            ? Width
            : (!double.IsNaN(Height) && Height > 0 ? Height : 16.0);
        if (Math.Abs(_glyph.FontSize - size) > 0.01)
            _glyph.FontSize = size;
        if (Math.Abs(_glyph.LineHeight - size) > 0.01)
            _glyph.LineHeight = size;
    }
}
