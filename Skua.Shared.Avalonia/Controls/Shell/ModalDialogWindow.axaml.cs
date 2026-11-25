using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Skua.Shared.Avalonia.Services;
using Skua.Shared.Avalonia.Theming;
using System;

namespace Skua.Shared.Avalonia.Controls.Shell;

/// <summary>
/// Standardized frameless modal dialog shell with title bar, body slot, and optional left/right footer actions.
/// </summary>
public partial class ModalDialogWindow : Window
{
    public ModalDialogWindow()
    {
        InitializeComponent();
        ShellWindowHelper.ConfigureFramelessWindow(this);
    }

    public ModalDialogWindow(string title, double width = 420) : this()
    {
        Width = width;
        SetTitle(title);
    }

    public void SetTitle(string title)
    {
        string resolved = title ?? string.Empty;
        Title = resolved;
        DialogTitleBar.TitleText = resolved;
    }

    public void SetBody(Control body, Thickness? margin = null)
    {
        BodyHost.Content = body;
        if (margin.HasValue)
            BodyHost.Margin = margin.Value;
    }

    public void SetLeftFooter(Control? leftControl)
    {
        LeftFooterHost.Content = leftControl;
        if (leftControl is not null)
            FooterBorder.IsVisible = true;
    }

    public Button AddButton(
        string text,
        Action onClick,
        bool isPrimary = false,
        bool isDanger = false,
        bool isDefault = false,
        bool isCancel = false,
        double minWidth = 84)
    {
        Button button = new()
        {
            Content = text,
            MinWidth = minWidth,
            IsDefault = isDefault,
            IsCancel = isCancel
        };
        button.Classes.Add("skua-button");
        if (isPrimary)
            button.Classes.Add("skua-primary");
        if (isDanger)
            button.Classes.Add("skua-danger");

        button.Click += (_, _) => onClick();
        ButtonsPanel.Children.Add(button);
        FooterBorder.IsVisible = true;
        return button;
    }

    public static TextBlock CreateFieldLabel(string text)
    {
        TextBlock label = new()
        {
            Text = text,
            FontWeight = FontWeight.SemiBold
        };
        UiTokens.Bind(label, TextBlock.FontSizeProperty, UiTokens.UiFontBody);
        UiTokens.Bind(label, TextBlock.ForegroundProperty, UiTokens.SkuaTextBrush);
        return label;
    }

    public static TextBlock CreateBodyText(string text, bool muted = false)
    {
        TextBlock block = new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap
        };
        UiTokens.Bind(block, TextBlock.FontSizeProperty, UiTokens.UiFontBody);
        UiTokens.Bind(block, TextBlock.ForegroundProperty, muted ? UiTokens.SkuaMutedTextBrush : UiTokens.SkuaTextBrush);
        return block;
    }
}
