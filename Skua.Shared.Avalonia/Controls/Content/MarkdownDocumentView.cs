using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Skua.Shared.Avalonia.Controls.Content;

public class MarkdownDocumentView : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownDocumentView, string?>(nameof(Markdown));

    static MarkdownDocumentView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownDocumentView>((view, _) => view.RenderDocument());
    }

    public MarkdownDocumentView()
    {
        AttachedToVisualTree += (_, _) => RenderDocument();
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void RenderDocument()
    {
        string? text = Markdown;
        if (string.IsNullOrWhiteSpace(text))
        {
            Content = null;
            return;
        }

        var stack = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (trimmed.StartsWith("# "))
            {
                var tb = new SelectableTextBlock
                {
                    Text = trimmed.Substring(2).Trim(),
                    FontSize = 22,
                    FontWeight = FontWeight.Bold,
                    Margin = new Thickness(0, 8, 0, 4),
                    TextWrapping = TextWrapping.Wrap
                };
                stack.Children.Add(tb);
            }
            else if (trimmed.StartsWith("## "))
            {
                var tb = new SelectableTextBlock
                {
                    Text = trimmed.Substring(3).Trim(),
                    FontSize = 18,
                    FontWeight = FontWeight.Bold,
                    Margin = new Thickness(0, 6, 0, 3),
                    TextWrapping = TextWrapping.Wrap
                };
                stack.Children.Add(tb);
            }
            else if (trimmed.StartsWith("### "))
            {
                var tb = new SelectableTextBlock
                {
                    Text = trimmed.Substring(4).Trim(),
                    FontSize = 15,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 6, 0, 2),
                    TextWrapping = TextWrapping.Wrap
                };
                stack.Children.Add(tb);
            }
            else if (trimmed.StartsWith(">"))
            {
                string quoteText = trimmed.TrimStart('>', ' ').Trim();
                var quoteBorder = new Border
                {
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    BorderBrush = Brushes.MediumPurple,
                    CornerRadius = new CornerRadius(0, 4, 4, 0),
                    Padding = new Thickness(12, 6),
                    Margin = new Thickness(0, 4, 0, 6)
                };

                var tb = CreateFormattedTextBlock(quoteText, 13);
                quoteBorder.Child = tb;
                stack.Children.Add(quoteBorder);
            }
            else if (trimmed.StartsWith("* ") || trimmed.StartsWith("- "))
            {
                string bulletText = trimmed.Substring(2).Trim();
                var grid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    Margin = new Thickness(8, 2, 0, 2)
                };

                var bullet = new TextBlock
                {
                    Text = "• ",
                    FontSize = 14,
                    FontWeight = FontWeight.Bold,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                Grid.SetColumn(bullet, 0);

                var tb = CreateFormattedTextBlock(bulletText, 13);
                Grid.SetColumn(tb, 1);

                grid.Children.Add(bullet);
                grid.Children.Add(tb);
                stack.Children.Add(grid);
            }
            else
            {
                var tb = CreateFormattedTextBlock(trimmed, 13);
                tb.Margin = new Thickness(0, 2, 0, 4);
                stack.Children.Add(tb);
            }
        }

        Content = stack;
    }

    private static SelectableTextBlock CreateFormattedTextBlock(string markdownSnippet, double fontSize)
    {
        var tb = new SelectableTextBlock
        {
            FontSize = fontSize,
            TextWrapping = TextWrapping.Wrap
        };

        // Parse **bold** markers
        string[] parts = Regex.Split(markdownSnippet, @"(\*\*.*?\*\*)");
        foreach (string part in parts)
        {
            if (string.IsNullOrEmpty(part))
                continue;

            if (part.StartsWith("**") && part.EndsWith("**") && part.Length >= 4)
            {
                string boldText = part.Substring(2, part.Length - 4);
                tb.Inlines?.Add(new Run
                {
                    Text = boldText,
                    FontWeight = FontWeight.Bold
                });
            }
            else
            {
                tb.Inlines?.Add(new Run
                {
                    Text = part
                });
            }
        }

        return tb;
    }
}
