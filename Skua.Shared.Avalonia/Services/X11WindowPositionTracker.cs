using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Skua.Shared.Avalonia.Services;

/// <summary>
/// Ensures native X11 override-redirect popups and window coordinates remain synchronized
/// under Wayland / XWayland compositors (such as COSMIC/Smithay, GNOME, KDE) which do not
/// send synthetic ConfigureNotify events when windows are moved across the screen.
/// </summary>
public static class X11WindowPositionTracker
{
    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XTranslateCoordinates(
        IntPtr display,
        IntPtr src_w,
        IntPtr dest_w,
        int src_x,
        int src_y,
        out int dest_x_return,
        out int dest_y_return,
        out IntPtr child_return);

    private static IntPtr _display;
    private static IntPtr _rootWindow;
    private static readonly object _lock = new();

    private static Type? _x11WindowType;
    private static FieldInfo? _positionField;
    private static PropertyInfo? _posChangedProp;
    private static MethodInfo? _updateScalingMethod;
    private static bool _initialized;
    private static readonly ConditionalWeakTable<Window, object> _trackedWindows = new();

    static X11WindowPositionTracker()
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = asm.GetType("Avalonia.X11.X11Window");
                if (type != null)
                {
                    _x11WindowType = type;
                    _positionField = type.GetField("_position", BindingFlags.NonPublic | BindingFlags.Instance);
                    _posChangedProp = type.GetProperty("PositionChanged", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _updateScalingMethod = type.GetMethod("UpdateScaling", BindingFlags.NonPublic | BindingFlags.Instance);
                    _initialized = true;
                    break;
                }
            }
        }
        catch { }
    }

    private static bool EnsureDisplay()
    {
        lock (_lock)
        {
            if (_display != IntPtr.Zero)
                return true;

            try
            {
                _display = XOpenDisplay(IntPtr.Zero);
                if (_display != IntPtr.Zero)
                {
                    _rootWindow = XDefaultRootWindow(_display);
                    return true;
                }
            }
            catch { }
            return false;
        }
    }

    /// <summary>
    /// Tracks window movement on X11 / XWayland and keeps Avalonia's internal position in sync.
    /// </summary>
    public static void Track(Window window)
    {
        // Track can be called by more than one window/service setup path. Do not
        // attach another set of handlers and another timer to an already tracked
        // window; duplicate timers can cause visible popup jitter under XWayland.
        lock (_lock)
        {
            if (_trackedWindows.TryGetValue(window, out _))
                return;

            _trackedWindows.Add(window, new object());
        }

        window.AddHandler(InputElement.PointerMovedEvent, (s, e) => SyncPosition(window), RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerPressedEvent, (s, e) => SyncPosition(window), RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerEnteredEvent, (s, e) => SyncPosition(window), RoutingStrategies.Tunnel);

        DispatcherTimer timer = new()
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        timer.Tick += (s, e) =>
        {
            if (window.IsVisible && window.WindowState != WindowState.Minimized)
                SyncPosition(window);
            else
                timer.Stop();
        };

        window.Opened += (s, e) =>
        {
            SyncPosition(window);
            timer.Start();
        };

        window.Closed += (s, e) =>
        {
            timer.Stop();
        };

        window.Activated += (s, e) =>
        {
            // The compositor can move an X11/XWayland window while it is inactive.
            // Refresh after activation so newly opened popups use the real owner position.
            Dispatcher.UIThread.Post(() => SyncPosition(window), DispatcherPriority.Render);
            if (window.IsVisible && window.WindowState != WindowState.Minimized)
                timer.Start();
        };

        window.PropertyChanged += (s, e) =>
        {
            if (e.Property != Window.WindowStateProperty && e.Property != Window.IsVisibleProperty)
                return;

            if (window.IsVisible && window.WindowState != WindowState.Minimized)
            {
                // Do not trust coordinates reported during minimization. Re-read them after
                // the compositor has restored the native window, then keep polling while visible.
                Dispatcher.UIThread.Post(() => SyncPosition(window), DispatcherPriority.Render);
                timer.Start();
            }
            else
            {
                timer.Stop();
            }
        };

        if (window.IsVisible && window.WindowState != WindowState.Minimized)
        {
            SyncPosition(window);
            timer.Start();
        }
    }

    /// <summary>
    /// Synchronizes the window's live X11 screen coordinates with Avalonia's X11Window implementation.
    /// </summary>
    public static void SyncPosition(Window window)
    {
        try
        {
            var platformImpl = window.PlatformImpl;
            if (platformImpl == null)
                return;

            if (_x11WindowType == null || !_x11WindowType.IsInstanceOfType(platformImpl))
            {
                // Avalonia may load its X11 backend after this type's static
                // constructor. Discover it from the live platform implementation.
                if (_initialized || !platformImpl.GetType().Name.Contains("X11", StringComparison.Ordinal))
                {
                    return;
                }

                _x11WindowType = platformImpl.GetType();
                _positionField = _x11WindowType.GetField("_position", BindingFlags.NonPublic | BindingFlags.Instance);
                _posChangedProp = _x11WindowType.GetProperty("PositionChanged", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _updateScalingMethod = _x11WindowType.GetMethod("UpdateScaling", BindingFlags.NonPublic | BindingFlags.Instance);
                _initialized = true;
            }

            IntPtr handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero)
                return;

            if (!EnsureDisplay())
                return;

            if (XTranslateCoordinates(_display, handle, _rootWindow, 0, 0, out int realX, out int realY, out _) != 0)
            {
                PixelPoint livePos = new(realX, realY);
                object? currentValue = _positionField?.GetValue(platformImpl);
                PixelPoint? currentPos = currentValue is PixelPoint position ? position : null;

                if (!currentPos.HasValue || currentPos.Value != livePos)
                {
                    _positionField?.SetValue(platformImpl, livePos);
                    if (_posChangedProp?.GetValue(platformImpl) is Action<PixelPoint> onPositionChanged)
                    {
                        onPositionChanged.Invoke(livePos);
                    }
                    _updateScalingMethod?.Invoke(platformImpl, null);
                }
            }
        }
        catch { }
    }
}
