using Avalonia;
using Avalonia.Controls;
using Skua.Shared.Avalonia.Controls.Shell;
using Skua.Shared.Avalonia.Theming;

namespace Skua.Shared.Avalonia.Services;

public static class ShellWindowHelper
{
    public static void ConfigureFramelessWindow(Window window, bool attachResize = true)
    {
        window.WindowDecorations = WindowDecorations.None;
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaTitleBarHeightHint = UiTokens.GetDouble(UiTokens.UiTitleBarHeight, 30);
        window.Classes.Add("skua");
        UiTokens.Bind(window, Window.BackgroundProperty, UiTokens.SkuaPageBrush);
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
            Child = root
        };
        rootBorder.Classes.Add("window-frame");

        return rootBorder;
    }
}
