using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
    private static extern bool XQueryPointer(
        IntPtr display,
        IntPtr w,
        out IntPtr root_return,
        out IntPtr child_return,
        out int root_x_return,
        out int root_y_return,
        out int win_x_return,
        out int win_y_return,
        out uint mask_return);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    public static PixelPoint? GetPointerPosition()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                IntPtr disp = XOpenDisplay(IntPtr.Zero);
                if (disp != IntPtr.Zero)
                {
                    try
                    {
                        IntPtr root = XDefaultRootWindow(disp);
                        if (XQueryPointer(disp, root, out _, out _, out int rx, out int ry, out _, out _, out _))
                        {
                            return new PixelPoint(rx, ry);
                        }
                    }
                    finally
                    {
                        XCloseDisplay(disp);
                    }
                }
            }
            catch { }
        }
        else if (OperatingSystem.IsWindows())
        {
            try
            {
                if (GetCursorPos(out POINT pt))
                    return new PixelPoint(pt.X, pt.Y);
            }
            catch { }
        }

        return null;
    }

    public static Screen? GetTargetScreen(Window window)
    {
        return GetTargetScreen(window, out _);
    }

    public static Screen? GetTargetScreen(Window window, out PixelRect? hostBounds)
    {
        hostBounds = null;

        // 1. Try owner window or active desktop MainWindow (for child dialogs/tool windows)
        try
        {
            if (window.Owner is Window owner && owner.IsVisible && owner.Bounds.Width > 100)
            {
                int ox = owner.Position.X;
                int oy = owner.Position.Y;
                int ow = (int)owner.Bounds.Width;
                int oh = (int)owner.Bounds.Height;
                PixelPoint center = new(ox + ow / 2, oy + oh / 2);
                var screen = window.Screens?.ScreenFromPoint(center);
                if (screen != null)
                {
                    hostBounds = new PixelRect(ox, oy, ow, oh);
                    return screen;
                }
            }

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow != null &&
                desktop.MainWindow != window &&
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

        // 2. Try global pointer position (the monitor where the user clicked the AppImage or has cursor focus)
        try
        {
            PixelPoint? ptr = GetPointerPosition();
            if (ptr.HasValue && window.Screens != null)
            {
                var screen = window.Screens.ScreenFromPoint(ptr.Value);
                if (screen != null)
                    return screen;
            }
        }
        catch { }

        // 3. Fallback to Primary screen or first screen
        if (window.Screens != null)
        {
            var primary = window.Screens.All.FirstOrDefault(s => s.IsPrimary) ?? window.Screens.Primary;
            if (primary != null)
                return primary;
            return window.Screens.All.FirstOrDefault();
        }

        return null;
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

        // Refine positioning once measured on Opened to guarantee proper centering on the chosen screen
        window.Opened += (s, e) =>
        {
            if (window.WindowState == WindowState.Maximized)
                return;

            int actualW = (int)window.Bounds.Width;
            int actualH = (int)window.Bounds.Height;
            if (actualW <= 0 || double.IsNaN(window.Bounds.Width))
                actualW = (int)targetW;
            if (actualH <= 0 || double.IsNaN(window.Bounds.Height))
                actualH = (int)targetH;

            var sc = screen ?? GetTargetScreen(window, out _);
            if (sc != null)
            {
                window.Position = CalculatePosition(sc, hostBounds, actualW, actualH);
            }
        };
    }
}
