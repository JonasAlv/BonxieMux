using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using System;
using System.Runtime.InteropServices;

namespace Skua.App.Avalonia.Controls;

/// <summary>
/// Hosts the game player's window on X11 (Linux). The player (another process) is started as a child of
/// <see cref="WindowHandle"/>, an X11 window created once on BonxieMux's own display connection. Avalonia places that
/// window in the game area and, when it recreates the native control, parks it instead of destroying it, so the game
/// keeps running.
/// </summary>
public class GameHost : NativeControlHost
{
    private static nint _display;
    private nint _holder;
    private DispatcherTimer? _fitTimer;
    private nint _player;

    public static event Action? GameClicked
    {
        add { }
        remove { }
    }
    public static void ForwardKey(bool down, string physical, string logical, bool ctrl, bool shift, bool alt) { }
    public static void ForwardText(string text) { }
    public static void ForwardFocus(bool focused) { }
    public static void ForwardClearKeys() { }
    public void SetScaleMode(bool isExactFit = false) { }

    public nint WindowHandle
    {
        get
        {
            if (_holder == 0)
            {
                try
                {
                    // Kept open for the app's lifetime: the X server destroys a connection's windows when it closes.
                    if (_display == 0)
                        _display = XOpenDisplay(0);
                    if (_display != 0)
                    {
                        _holder = XCreateSimpleWindow(_display, XDefaultRootWindow(_display), 0, 0, 1, 1, 0, 0, 0x141414);
                        XFlush(_display);
                    }
                }
                catch (DllNotFoundException)
                {
                    // No libX11: the player opens a window of its own.
                }
            }
            return _holder;
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (WindowHandle == 0)
            return base.CreateNativeControlCore(parent);

        _fitTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => FitPlayer());
        _fitTimer.Start();
        return new PlatformHandle(_holder, "XID");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // Avalonia has already parked our window; destroying it would destroy the player's window with it.
        if (control.Handle != _holder)
            base.DestroyNativeControlCore(control);
        _fitTimer?.Stop();
    }

    // Polls the sizes every 200 ms to keep the child window fitted to the holder.
    private void FitPlayer()
    {
        if (_display == 0 || _holder == 0)
            return;
        if (XGetGeometry(_display, _holder, out _, out _, out _, out uint width, out uint height, out _, out _) == 0)
            return;
        if (XQueryTree(_display, _holder, out _, out _, out nint children, out uint count) == 0)
            return;

        nint player = 0;
        try
        {
            // The player normally has one direct child. Keep selecting the first
            // live child so a restarted player or recreated X11 window is picked
            // up without requiring a new Avalonia control.
            if (count > 0 && children != 0)
                player = Marshal.ReadIntPtr(children);
        }
        finally
        {
            if (children != 0)
                XFree(children);
        }

        if (player == 0)
        {
            _player = 0;
            return;
        }

        if (XGetGeometry(_display, player, out _, out _, out _, out uint playerWidth, out uint playerHeight, out _, out _) == 0)
        {
            _player = 0;
            return;
        }

        bool playerChanged = _player != player;
        _player = player;
        if (playerChanged)
            XMapWindow(_display, player);

        if (playerChanged || playerWidth != width || playerHeight != height)
        {
            XMoveResizeWindow(_display, player, 0, 0, width, height);
            XFlush(_display);
        }
    }

    private const string LibX11 = "libX11.so.6";

    [DllImport(LibX11)]
    private static extern nint XOpenDisplay(nint name);

    [DllImport(LibX11)]
    private static extern nint XDefaultRootWindow(nint display);

    [DllImport(LibX11)]
    private static extern nint XCreateSimpleWindow(nint display, nint parent, int x, int y, uint width, uint height, uint borderWidth, nuint border, nuint background);

    [DllImport(LibX11)]
    private static extern int XGetGeometry(nint display, nint drawable, out nint root, out int x, out int y, out uint width, out uint height, out uint borderWidth, out uint depth);

    [DllImport(LibX11)]
    private static extern int XQueryTree(nint display, nint window, out nint root, out nint parent, out nint children, out uint count);

    [DllImport(LibX11)]
    private static extern int XMoveResizeWindow(nint display, nint window, int x, int y, uint width, uint height);

    [DllImport(LibX11)]
    private static extern int XMapWindow(nint display, nint window);

    [DllImport(LibX11)]
    private static extern int XFree(nint data);

    [DllImport(LibX11)]
    private static extern int XFlush(nint display);
}
