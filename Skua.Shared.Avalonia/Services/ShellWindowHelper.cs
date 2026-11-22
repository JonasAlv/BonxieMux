using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Skua.Shared.Avalonia.Controls.Shell;

namespace Skua.Shared.Avalonia.Services;

public static class ShellWindowHelper
{
    public static void ConfigureFramelessWindow(Window window, bool attachResize = true)
    {
        window.WindowDecorations = WindowDecorations.None;
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaTitleBarHeightHint = 32;
        window.Classes.Add("skua");
        window[!Window.BackgroundProperty] = new DynamicResourceExtension("SkuaPageBrush");
        ThemeResourceApplicator.ApplyTitleBarTheme(window);
        if (attachResize)
            WindowResizeHelper.Attach(window);
    }

    public static Control CreateShellContent(string title, Control content)
    {
        Border contentHost = new()
        {
            Child = content,
            ClipToBounds = true
        };
        contentHost.Classes.Add("shell");

        Grid root = new();
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));

        WindowTitleBar titleBar = new()
        {
            TitleText = title
        };
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(contentHost, 1);
        root.Children.Add(titleBar);
        root.Children.Add(contentHost);

        Border rootBorder = new()
        {
            BorderThickness = new Thickness(1),
            Child = root
        };
        rootBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SkuaBorderBrush");
        rootBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("SkuaPageBrush");

        return rootBorder;
    }
}
