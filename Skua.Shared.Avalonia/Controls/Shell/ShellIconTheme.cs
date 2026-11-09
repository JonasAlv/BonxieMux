using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using System;

namespace Skua.Shared.Avalonia.Controls.Shell;

/// <summary>
/// Tray icons sit on the OS system tray/panel, so they follow the desktop OS theme:
/// SkuaShellImage.png on dark panels, SkuaShellImageLightTheme.png on light panels.
/// The window and desktop taskbar icons use SkuaIcon.png.
/// </summary>
public static class ShellIconTheme
{
    private static Application? _app;
    private static WindowIcon? _darkTrayIcon;
    private static WindowIcon? _lightTrayIcon;
    private static WindowIcon? _windowIcon;

    /// <summary>The tray icon for the current desktop OS theme.</summary>
    public static WindowIcon? CurrentTrayIcon { get; private set; }

    /// <summary>The icon for the current OS theme (tray icon), kept for backwards compatibility.</summary>
    public static WindowIcon? Current => CurrentTrayIcon;

    /// <summary>The desktop taskbar / window icon.</summary>
    public static WindowIcon? WindowIcon => _windowIcon;

    public static void Track(
        Application app,
        string darkTrayIconUri = "avares://Skua.Shared.Avalonia/Assets/SkuaShellImage.png",
        string lightTrayIconUri = "avares://Skua.Shared.Avalonia/Assets/SkuaShellImageLightTheme.png",
        string windowIconUri = "avares://Skua.Shared.Avalonia/Assets/SkuaIcon.png")
    {
        _app = app;
        _darkTrayIcon = Load(darkTrayIconUri);
        _lightTrayIcon = Load(lightTrayIconUri);
        _windowIcon = Load(windowIconUri);

        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => window.Icon = _windowIcon);
        if (app.PlatformSettings is { } settings)
            settings.ColorValuesChanged += (_, values) => Dispatcher.UIThread.Post(() => Apply(values.ThemeVariant));
        Apply(app.PlatformSettings?.GetColorValues().ThemeVariant ?? PlatformThemeVariant.Dark);
    }

    private static void Apply(PlatformThemeVariant osTheme)
    {
        CurrentTrayIcon = osTheme == PlatformThemeVariant.Light ? _lightTrayIcon : _darkTrayIcon;
        if (_app?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            foreach (Window window in desktop.Windows)
                window.Icon = _windowIcon;
        if (_app is not null && TrayIcon.GetIcons(_app) is { } trays)
            foreach (TrayIcon tray in trays)
                tray.Icon = CurrentTrayIcon;
    }

    private static WindowIcon Load(string avaresUri)
    {
        using var stream = AssetLoader.Open(new Uri(avaresUri));
        return new WindowIcon(stream);
    }
}
