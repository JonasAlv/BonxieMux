using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace Skua.Shared.Avalonia.Services;

public static class WindowPlacementHelper
{
    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr display);
    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")]
    private static extern bool XQueryPointer(IntPtr d, IntPtr w, out IntPtr r, out IntPtr c, out int rx, out int ry, out int wx, out int wy, out uint m);

    public static Screen? GetTargetScreen(Window window)
    {
        // 1. Check screen where mouse cursor is (where AppImage was clicked)
        try
        {
            IntPtr disp = XOpenDisplay(IntPtr.Zero);
            if (disp != IntPtr.Zero)
            {
                try
                {
                    if (XQueryPointer(disp, XDefaultRootWindow(disp), out _, out _, out int rx, out int ry, out _, out _, out _))
                    {
                        var screen = window.Screens?.ScreenFromPoint(new PixelPoint(rx, ry));
                        if (screen != null)
                            return screen;
                    }
                }
                finally { XCloseDisplay(disp); }
            }
        }
        catch { }

        // 2. Default to primary display, or first available screen
        return window.Screens?.Primary ?? window.Screens?.All?.FirstOrDefault();
    }

    public static Screen? GetTargetScreen(Window window, out PixelRect? hostBounds)
    {
        hostBounds = null;
        return GetTargetScreen(window);
    }

    public static PixelPoint CalculatePosition(Screen screen, int width, int height)
    {
        int posX = screen.WorkingArea.X + (screen.WorkingArea.Width - width) / 2;
        int posY = screen.WorkingArea.Y + (screen.WorkingArea.Height - height) / 2;

        int minX = screen.WorkingArea.X;
        int maxX = Math.Max(minX, screen.WorkingArea.Right - width);
        int minY = screen.WorkingArea.Y;
        int maxY = Math.Max(minY, screen.WorkingArea.Bottom - height);

        return new PixelPoint(Math.Clamp(posX, minX, maxX), Math.Clamp(posY, minY, maxY));
    }

    public static PixelPoint CalculatePosition(Screen screen, PixelRect? hostBounds, int width, int height)
    {
        return CalculatePosition(screen, width, height);
    }

    public static void CenterOnActiveScreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        double targetW = window.Width > 0 && !double.IsNaN(window.Width) ? window.Width : (window.MinWidth > 0 ? window.MinWidth : 500);
        double targetH = window.Height > 0 && !double.IsNaN(window.Height) ? window.Height : (window.MinHeight > 0 ? window.MinHeight : 400);

        var screen = GetTargetScreen(window);
        if (screen != null)
            window.Position = CalculatePosition(screen, (int)targetW, (int)targetH);

        window.Opened += (s, e) =>
        {
            if (window.WindowState == WindowState.Maximized)
                return;

            int actualW = (int)window.Bounds.Width;
            int actualH = (int)window.Bounds.Height;
            if (actualW > 0 && actualH > 0 && screen != null)
                window.Position = CalculatePosition(screen, actualW, actualH);
        };
    }
}
