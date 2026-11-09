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
    #region Native Cursor Detection

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern bool XQueryPointer(
        IntPtr d,
        IntPtr w,
        out IntPtr root_return,
        out IntPtr child_return,
        out int rx,
        out int ry,
        out int wx,
        out int wy,
        out uint mask);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    public static PixelPoint? GetGlobalCursorPosition()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                IntPtr disp = XOpenDisplay(IntPtr.Zero);
                if (disp != IntPtr.Zero)
                {
                    try
                    {
                        if (XQueryPointer(disp, XDefaultRootWindow(disp), out _, out _, out int rx, out int ry, out _, out _, out _))
                            return new PixelPoint(rx, ry);
                    }
                    finally
                    {
                        XCloseDisplay(disp);
                    }
                }
            }
            catch { }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
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

    #endregion

    /// <summary>
    /// Resolves the focused display in priority order:
    /// 1. Owner window's display
    /// 2. Currently active window in this application
    /// 3. Display containing the mouse pointer (where user clicked / is looking)
    /// 4. Primary display
    /// 5. First available display
    /// </summary>
    public static Screen? GetFocusedScreen(Window window)
    {
        var screens = window.Screens;
        if (screens == null)
            return null;

        // Tier 1: Check window owner
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

        // Tier 2: Check any active window in the current application
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

        // Tier 3: Check screen where the mouse cursor is located
        var cursor = GetGlobalCursorPosition();
        if (cursor.HasValue)
        {
            try
            {
                var mouseScreen = screens.ScreenFromPoint(cursor.Value);
                if (mouseScreen != null)
                    return mouseScreen;
            }
            catch { }
        }

        // Tier 4: Primary display fallback
        // Tier 5: First available screen fallback
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

    /// <summary>
    /// Calculates the centered position taking DPI scaling and WorkingArea into account.
    /// </summary>
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

    /// <summary>
    /// Centers the window on the active/focused display.
    /// </summary>
    public static void CenterOnActiveScreen(Window window)
    {
        var screen = GetFocusedScreen(window);
        if (screen == null)
        {
            // Fallback to Avalonia built-in centering if no screen metadata is accessible
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;

        double targetW = window.Width > 0 && !double.IsNaN(window.Width) ? window.Width : (window.MinWidth > 0 ? window.MinWidth : 800);
        double targetH = window.Height > 0 && !double.IsNaN(window.Height) ? window.Height : (window.MinHeight > 0 ? window.MinHeight : 600);

        // Pre-show positioning to hint to the Window Manager which display to place the window on
        window.Position = CalculatePosition(screen, targetW, targetH);

        // Post-render exact centering once Avalonia has measured layout bounds
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
