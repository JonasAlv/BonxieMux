using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using System;
using System.Linq;

namespace Skua.Shared.Avalonia.Services;

public static class WindowPlacementHelper
{
    public static Screen? GetFocusedScreen(Window window)
    {
        var screens = window.Screens;
        if (screens == null)
            return null;

        if (window.Owner != null)
        {
            try
            {
                var ownerScreen = screens.ScreenFromWindow(window.Owner);
                if (ownerScreen != null)
                    return ownerScreen;
            }
            catch { }
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                var activeWin = desktop.Windows.FirstOrDefault(w => w.IsActive && w.IsVisible && w != window);
                if (activeWin != null)
                {
                    var activeScreen = screens.ScreenFromWindow(activeWin);
                    if (activeScreen != null)
                        return activeScreen;
                }
            }
            catch { }
        }

        return screens.Primary ?? screens.All?.FirstOrDefault();
    }

    public static Screen? GetTargetScreen(Window window)
    {
        return GetFocusedScreen(window);
    }

    public static Screen? GetTargetScreen(Window window, out PixelRect? hostBounds)
    {
        hostBounds = null;
        return GetFocusedScreen(window);
    }

    public static PixelPoint CalculatePosition(Screen screen, double widthDip, double heightDip)
    {
        double scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
        int pixelW = (int)Math.Round(widthDip * scale);
        int pixelH = (int)Math.Round(heightDip * scale);

        var work = screen.WorkingArea;

        int posX = work.X + (work.Width - pixelW) / 2;
        int posY = work.Y + (work.Height - pixelH) / 2;

        int minX = work.X;
        int maxX = Math.Max(minX, work.Right - pixelW);
        int minY = work.Y;
        int maxY = Math.Max(minY, work.Bottom - pixelH);

        return new PixelPoint(Math.Clamp(posX, minX, maxX), Math.Clamp(posY, minY, maxY));
    }

    public static PixelPoint CalculatePosition(Screen screen, int width, int height)
    {
        return CalculatePosition(screen, (double)width, (double)height);
    }

    public static PixelPoint CalculatePosition(Screen screen, PixelRect? hostBounds, int width, int height)
    {
        return CalculatePosition(screen, (double)width, (double)height);
    }

    public static void CenterOnActiveScreen(Window window)
    {
        var screen = GetFocusedScreen(window);
        if (screen == null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;

        double targetW = window.Width > 0 && !double.IsNaN(window.Width) ? window.Width : (window.MinWidth > 0 ? window.MinWidth : 800);
        double targetH = window.Height > 0 && !double.IsNaN(window.Height) ? window.Height : (window.MinHeight > 0 ? window.MinHeight : 600);

        window.Position = CalculatePosition(screen, targetW, targetH);

        window.Opened += (s, e) =>
        {
            if (window.WindowState == WindowState.Maximized || window.WindowState == WindowState.FullScreen)
                return;

            double actualW = window.Bounds.Width;
            double actualH = window.Bounds.Height;
            if (actualW > 0 && actualH > 0)
            {
                var activeScreen = GetFocusedScreen(window) ?? screen;
                window.Position = CalculatePosition(activeScreen, actualW, actualH);
            }
        };
    }
}
