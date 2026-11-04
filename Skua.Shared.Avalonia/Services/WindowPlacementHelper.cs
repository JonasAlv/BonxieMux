using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using System;
using System.Linq;

namespace Skua.Shared.Avalonia.Services;

public static class WindowPlacementHelper
{
    public static Screen? GetTargetScreen(Window window)
    {
        return GetTargetScreen(window, out _);
    }

    public static Screen? GetTargetScreen(Window window, out PixelRect? hostBounds)
    {
        hostBounds = null;

        // Try desktop MainWindow if visible and on a real screen (host process or standalone mode)
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow != null &&
                desktop.MainWindow.IsVisible &&
                desktop.MainWindow.Position.X > -10000 &&
                desktop.MainWindow.Bounds.Width > 100)
            {
                int mwX = desktop.MainWindow.Position.X;
                int mwY = desktop.MainWindow.Position.Y;
                int mwW = (int)desktop.MainWindow.Bounds.Width;
                int mwH = (int)desktop.MainWindow.Bounds.Height;
                PixelPoint center = new(mwX + mwW / 2, mwY + mwH / 2);
                var screen = window.Screens?.ScreenFromPoint(center);
                if (screen != null)
                {
                    hostBounds = new PixelRect(mwX, mwY, mwW, mwH);
                    return screen;
                }
            }
        }
        catch { }

        // Fallback to Primary screen or first screen
        return window.Screens?.Primary ?? window.Screens?.All?.FirstOrDefault();
    }

    public static PixelPoint CalculatePosition(Screen screen, PixelRect? hostBounds, int width, int height)
    {
        int posX;
        int posY;

        if (hostBounds.HasValue && hostBounds.Value.Width > 0 && hostBounds.Value.Height > 0)
        {
            var hb = hostBounds.Value;
            posX = hb.X + (hb.Width - width) / 2;
            posY = hb.Y + (hb.Height - height) / 2;
        }
        else
        {
            posX = screen.WorkingArea.X + (screen.WorkingArea.Width - width) / 2;
            posY = screen.WorkingArea.Y + (screen.WorkingArea.Height - height) / 2;
        }

        // Clamp inside screen working area
        int minX = screen.WorkingArea.X;
        int maxX = Math.Max(minX, screen.WorkingArea.Right - width);
        int minY = screen.WorkingArea.Y;
        int maxY = Math.Max(minY, screen.WorkingArea.Bottom - height);

        posX = Math.Clamp(posX, minX, maxX);
        posY = Math.Clamp(posY, minY, maxY);

        return new PixelPoint(posX, posY);
    }

    public static void CenterOnActiveScreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        double targetW = window.Width;
        if (double.IsNaN(targetW) || targetW <= 0)
            targetW = window.MinWidth > 0 ? window.MinWidth : 500;

        double targetH = window.Height;
        if (double.IsNaN(targetH) || targetH <= 0)
            targetH = window.MinHeight > 0 ? window.MinHeight : 400;

        var screen = GetTargetScreen(window, out var hostBounds);
        if (screen != null)
        {
            window.Position = CalculatePosition(screen, hostBounds, (int)targetW, (int)targetH);
        }

        // Refine positioning once measured on Opened
        window.Opened += (s, e) =>
        {
            int actualW = (int)window.Bounds.Width;
            int actualH = (int)window.Bounds.Height;
            if (actualW > 0 && actualH > 0 && (Math.Abs(actualW - (int)targetW) > 10 || Math.Abs(actualH - (int)targetH) > 10))
            {
                var sc = GetTargetScreen(window, out var hb);
                if (sc != null)
                {
                    window.Position = CalculatePosition(sc, hb, actualW, actualH);
                }
            }
        };
    }
}
