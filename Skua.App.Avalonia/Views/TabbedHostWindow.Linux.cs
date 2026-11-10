using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Skua.App.Avalonia.Services;
using Skua.Core.Interfaces;

namespace Skua.App.Avalonia.Views;

public partial class TabbedHostWindow : Window
{
    #region Linux X11 API
    [StructLayout(LayoutKind.Sequential)]
    public struct XSetWindowAttributes
    {
        public IntPtr background_pixmap;
        public UIntPtr background_pixel;
        public IntPtr border_pixmap;
        public UIntPtr border_pixel;
        public int bit_gravity;
        public int win_gravity;
        public int backing_store;
        public UIntPtr backing_planes;
        public UIntPtr backing_pixel;
        public int save_under;
        public IntPtr event_mask;
        public IntPtr do_not_propagate_mask;
        public int override_redirect;
        public IntPtr colormap;
        public IntPtr cursor;
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XMapWindow(IntPtr display, IntPtr w);

    [DllImport("libX11.so.6")]
    private static extern int XUnmapWindow(IntPtr display, IntPtr w);

    [DllImport("libX11.so.6")]
    private static extern int XReparentWindow(IntPtr display, IntPtr w, IntPtr parent, int x, int y);

    [DllImport("libX11.so.6")]
    private static extern int XMoveResizeWindow(IntPtr display, IntPtr w, int x, int y, uint width, uint height);

    [DllImport("libX11.so.6")]
    private static extern int XMoveWindow(IntPtr display, IntPtr w, int x, int y);

    [DllImport("libX11.so.6")]
    private static extern int XRaiseWindow(IntPtr display, IntPtr w);

    [DllImport("libX11.so.6")]
    private static extern int XSetTransientForHint(IntPtr display, IntPtr w, IntPtr prop_window);

    [DllImport("libX11.so.6")]
    private static extern int XSetInputFocus(IntPtr display, IntPtr w, int revert_to, IntPtr time);

    [DllImport("libX11.so.6")]
    private static extern int XGetInputFocus(IntPtr display, out IntPtr focus_return, out int revert_to_return);

    [DllImport("libX11.so.6")]
    private static extern int XChangeWindowAttributes(IntPtr display, IntPtr w, UIntPtr valuemask, ref XSetWindowAttributes attributes);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XTranslateCoordinates(IntPtr display, IntPtr src_w, IntPtr dest_w, int src_x, int src_y, out int dest_x, out int dest_y, out IntPtr child);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XSync(IntPtr display, int discard);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(IntPtr display);

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

    private static IntPtr _xDisplay;
    private static readonly object _xLock = new();
    private DispatcherTimer? _gridFocusTimer;
    private IntPtr _gridFocusedChild;

    private static IntPtr GetDisplay()
    {
        lock (_xLock)
        {
            if (_xDisplay == IntPtr.Zero)
            {
                try
                {
                    _xDisplay = XOpenDisplay(IntPtr.Zero);
                }
                catch { }
            }
            return _xDisplay;
        }
    }

    private void StartGridFocusTracking()
    {
        _gridFocusTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(100),
            DispatcherPriority.Background,
            (_, _) => UpdateGridFocusedChild());
        _gridFocusTimer.Start();
    }

    private void UpdateGridFocusedChild()
    {
        if (!_isGridViewEnabled || !IsVisible || WindowState == WindowState.Minimized)
            return;

        IScriptOption? options = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<IScriptOption>();
        if (options is null || !options.AutoHeadless || !options.AutoHeadlessLiftOnFocus)
            return;

        IntPtr display = GetDisplay();
        if (display == IntPtr.Zero)
            return;

        try
        {
            XGetInputFocus(display, out IntPtr focusedWindow, out _);
            TabInfo? focusedTab = _tabs.Values.FirstOrDefault(t => t.ChildHwnd == focusedWindow);
            IntPtr focusedChild = focusedTab?.ChildHwnd ?? IntPtr.Zero;
            if (focusedChild == _gridFocusedChild)
                return;

            _gridFocusedChild = focusedChild;
            Dispatcher.UIThread.Post(DoReposition, DispatcherPriority.Background);
        }
        catch { }
    }
    #endregion

    #region Platform Window Operations
    private void PostChildMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var tab = _tabs.Values.FirstOrDefault(t => t.ChildHwnd == hWnd);
        if (tab?.Process != null && !tab.Process.HasExited)
        {
            TabIpc.SendMessage(tab.Process.Id, msg, wParam, lParam);
        }
    }

    private void PlatformReparentChild(IntPtr childHwnd)
    {
        IntPtr display = GetDisplay();
        if (display != IntPtr.Zero && _hostHwnd != IntPtr.Zero && childHwnd != IntPtr.Zero)
        {
            lock (_xLock)
            {
                XUnmapWindow(display, childHwnd);
                XSync(display, 0);
                XReparentWindow(display, childHwnd, _hostHwnd, -32000, -32000);
                XSync(display, 0);
            }
        }
    }

    private void PlatformDetachChild(IntPtr childHwnd, int screenX, int screenY, int width, int height)
    {
        IntPtr display = GetDisplay();
        if (display != IntPtr.Zero && childHwnd != IntPtr.Zero)
        {
            lock (_xLock)
            {
                IntPtr root = XDefaultRootWindow(display);
                XUnmapWindow(display, childHwnd);
                XSync(display, 0);
                XReparentWindow(display, childHwnd, root, screenX, screenY);
                XMoveResizeWindow(display, childHwnd, screenX, screenY, (uint)Math.Max(100, width), (uint)Math.Max(100, height));
                XMapWindow(display, childHwnd);
                XRaiseWindow(display, childHwnd);
                XSync(display, 0);
            }
        }
    }

    private void PlatformReattachChild(IntPtr childHwnd)
    {
        PlatformReparentChild(childHwnd);
    }

    private void PlatformRaiseChild(IntPtr childHwnd)
    {
        IntPtr display = GetDisplay();
        if (display != IntPtr.Zero && childHwnd != IntPtr.Zero)
        {
            lock (_xLock)
            {
                XRaiseWindow(display, childHwnd);
                XSetInputFocus(display, childHwnd, 1, IntPtr.Zero);
                XFlush(display);
            }
        }
    }

    public static (int rootX, int rootY) GetGlobalPointerPosition()
    {
        IntPtr display = GetDisplay();
        if (display != IntPtr.Zero)
        {
            lock (_xLock)
            {
                IntPtr root = XDefaultRootWindow(display);
                if (XQueryPointer(display, root, out _, out _, out int rx, out int ry, out _, out _, out _))
                {
                    return (rx, ry);
                }
            }
        }
        return (-1, -1);
    }

    private void PlatformMoveResizeChild(IntPtr childHwnd, int x, int y, int width, int height, bool focus = false)
    {
        IntPtr display = GetDisplay();
        if (display != IntPtr.Zero && childHwnd != IntPtr.Zero)
        {
            // Callers pass Avalonia logical (DIP) units, but X11 works in physical pixels.
            // With fractional scaling (Xft.dpi != 96, e.g. KDE Plasma at 125%) the child would
            // otherwise be placed too far up-left and sized too small inside the host.
            double scale = RenderScaling > 0 ? RenderScaling : 1.0;
            int px = (int)Math.Round(x * scale);
            int py = (int)Math.Round(y * scale);
            uint pw = (uint)Math.Max(1, Math.Round(width * scale));
            uint ph = (uint)Math.Max(1, Math.Round(height * scale));

            lock (_xLock)
            {
                XMoveResizeWindow(display, childHwnd, px, py, pw, ph);
                XMapWindow(display, childHwnd);
                XRaiseWindow(display, childHwnd);
                if (focus)
                {
                    XSetInputFocus(display, childHwnd, 1, IntPtr.Zero);
                }
                XFlush(display);
            }
        }
    }

    private void PlatformHideChild(IntPtr childHwnd)
    {
        IntPtr display = GetDisplay();
        if (display != IntPtr.Zero && childHwnd != IntPtr.Zero)
        {
            lock (_xLock)
            {
                // Keep the window's laid-out dimensions intact across unmaps so Avalonia and the child GL surface
                // do not thrash to 1x1 and lose top-bar layout while switching tabs.
                XMoveWindow(display, childHwnd, -32000, -32000);
                XUnmapWindow(display, childHwnd);
                XFlush(display);
            }
        }
    }

    private void PlatformUnmapChild(IntPtr childHwnd)
    {
        IntPtr display = GetDisplay();
        if (display != IntPtr.Zero && childHwnd != IntPtr.Zero)
        {
            lock (_xLock)
            {
                XUnmapWindow(display, childHwnd);
                XFlush(display);
            }
        }
    }
    #endregion
}
