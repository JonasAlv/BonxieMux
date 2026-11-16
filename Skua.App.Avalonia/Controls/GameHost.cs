using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Threading;
using Skua.App.Avalonia.Flash;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Skua.App.Avalonia.Controls;

/// <summary>
/// X11/Avalonia game viewport. Captures Mugetsu's offscreen X11 window via
/// <c>libbonxie_x11.so</c> (XComposite + DRI3 DMA-BUF) and presents frames
/// inside Avalonia's OpenGL context via <see cref="OpenGlControlBase"/>,
/// forwarding all input through <see cref="FlashUtil.SendInput"/> or <see cref="Services.TabIpc.SendInput"/>.
/// </summary>
public class GameHost : Control, ICustomHitTest
{
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    private static GameHost? _activeInstance;
    private static readonly ConcurrentDictionary<int, int> _pendingRemotePlayerPids = new();
    private static (PixelRect Bounds, uint RefreshMhz)[] _cachedOutputs = Array.Empty<(PixelRect, uint)>();

    private FlashUtil? _flash;
    private readonly int _remoteChildPid;
    private int _remotePlayerPid;
    private IntPtr _x11Host;
    private int _attachedPid;
    private X11DmabufViewport? _glViewport;
    private DispatcherTimer? _resizeDebounceTimer;
    private int _pendingResizeW;
    private int _pendingResizeH;
    private int _lastSentWidth;
    private int _lastSentHeight;
    private bool? _lastSentVisible;

    public static event Action? GameClicked;

    public nint WindowHandle => 0;

    public GameHost() : this(0)
    {
    }

    public GameHost(int remoteChildPid)
    {
        Focusable = true;
        ClipToBounds = true;
        _remoteChildPid = remoteChildPid;
        if (remoteChildPid <= 0)
        {
            _activeInstance = this;
        }
        else if (_pendingRemotePlayerPids.TryRemove(remoteChildPid, out int mugetsuPid))
        {
            _remotePlayerPid = mugetsuPid;
        }
    }

    public static void RegisterPendingRemotePlayerPid(int childPid, int mugetsuPid)
    {
        if (childPid > 0 && mugetsuPid > 0)
            _pendingRemotePlayerPids[childPid] = mugetsuPid;
    }

    public static unsafe uint DetectDisplayRefreshMhz(Window? window = null)
    {
        (PixelRect Bounds, uint RefreshMhz)[] outputs = GetDisplayOutputs();
        if (outputs.Length == 0)
            return 60_000;

        if (window != null)
        {
            Screen? screen = window.Screens?.ScreenFromWindow(window) ?? window.Screens?.Primary;
            if (screen != null)
            {
                PixelRect sb = screen.Bounds;
                for (int i = 0; i < outputs.Length; i++)
                {
                    if (outputs[i].Bounds == sb)
                        return outputs[i].RefreshMhz;
                }
                PixelPoint center = sb.Center;
                for (int i = 0; i < outputs.Length; i++)
                {
                    if (outputs[i].Bounds.Contains(center))
                        return outputs[i].RefreshMhz;
                }
                for (int i = 0; i < outputs.Length; i++)
                {
                    if (outputs[i].Bounds.Intersects(sb))
                        return outputs[i].RefreshMhz;
                }
            }
        }

        return outputs[0].RefreshMhz;
    }

    private static unsafe (PixelRect Bounds, uint RefreshMhz)[] GetDisplayOutputs()
    {
        if (_cachedOutputs.Length > 0)
            return _cachedOutputs;

        var list = new List<(PixelRect Bounds, uint RefreshMhz)>();
        try
        {
            BonxieOutputInfo* buf = stackalloc BonxieOutputInfo[16];
            int count = BonxieX11Native.QueryOutputs(buf, 16);
            for (int i = 0; i < count; i++)
            {
                list.Add((
                    new PixelRect(buf[i].X, buf[i].Y, (int)buf[i].Width, (int)buf[i].Height),
                    buf[i].RefreshMhz > 0 ? buf[i].RefreshMhz : 60_000));
            }
        }
        catch { }

        if (list.Count > 0)
            _cachedOutputs = list.ToArray();
        return _cachedOutputs;
    }

    public void SetActiveInstance() => _activeInstance = this;

    public void AttachFlash(FlashUtil flash)
    {
        _flash = flash;
        EnsureX11HostStarted();
        SendResizeIfNeeded();
    }

    public void AttachRemotePlayerPid(int mugetsuPid)
    {
        if (mugetsuPid <= 0)
            return;
        _remotePlayerPid = mugetsuPid;
        EnsureX11HostStarted();
        SendResizeIfNeeded();
    }

    private void EnsureX11HostStarted()
    {
        int pid = _remoteChildPid > 0 ? _remotePlayerPid : (_flash?.PlayerPid ?? 0);
        if (pid <= 0 || pid == _attachedPid)
            return;

        StopX11Host();

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int bw = (int)Math.Round(Bounds.Width * scale);
        int bh = (int)Math.Round(Bounds.Height * scale);
        uint w = (uint)Math.Max(320, bw > 2 ? bw : _lastSentWidth);
        uint h = (uint)Math.Max(240, bh > 2 ? bh : _lastSentHeight);

        try
        {
            _x11Host = BonxieX11Native.Start((uint)pid, w, h);
        }
        catch
        {
            _x11Host = IntPtr.Zero;
        }

        if (_x11Host != IntPtr.Zero)
        {
            _attachedPid = pid;
            _lastSentWidth = -1;
            _lastSentHeight = -1;
            _glViewport = new X11DmabufViewport(_x11Host);
            LogicalChildren.Add(_glViewport);
            VisualChildren.Add(_glViewport);
            InvalidateMeasure();
            InvalidateArrange();
        }
    }

    private void StopX11Host()
    {
        if (_glViewport != null)
        {
            VisualChildren.Remove(_glViewport);
            LogicalChildren.Remove(_glViewport);
            _glViewport = null;
        }

        if (_x11Host != IntPtr.Zero)
        {
            IntPtr h = _x11Host;
            _x11Host = IntPtr.Zero;
            try { BonxieX11Native.Stop(h); } catch { }
        }
        _attachedPid = 0;
    }

    public void SuspendCapture() => StopX11Host();

    public void DisposeResources()
    {
        _resizeDebounceTimer?.Stop();
        if (_activeInstance == this)
            _activeInstance = null;
        _flash = null;
        StopX11Host();
    }

    private void DispatchInput(object payload)
    {
        if (_flash != null)
            _flash.SendInput(payload);
        else if (_remoteChildPid > 0)
            Skua.App.Avalonia.Services.TabIpc.SendInput(_remoteChildPid, payload);
    }

    public static void ForwardKey(bool down, string physical, string logical, bool ctrl, bool shift, bool alt)
    {
        _activeInstance?.DispatchInput(new
        {
            input = "key",
            down,
            code = physical,
            key = logical,
            ctrl,
            shift,
            alt
        });
        if (down && !ctrl && !alt && logical.Length == 1 && !char.IsControl(logical[0]))
        {
            _activeInstance?.DispatchInput(new { input = "text", text = logical });
        }
    }

    public static void ForwardText(string text)
    {
        if (!string.IsNullOrEmpty(text))
            _activeInstance?.DispatchInput(new { input = "text", text });
    }

    public static void ForwardFocus(bool focused) =>
        _activeInstance?.DispatchInput(new { input = "focus", focused });

    public static void ForwardClearKeys() =>
        _activeInstance?.DispatchInput(new { input = "clear_keys" });

    public void SetScaleMode(bool isExactFit = false)
    {
    }

    public void SetHostedVisible(bool visible)
    {
        if (_remoteChildPid > 0)
            return;
        if (_lastSentVisible == visible)
            return;
        _lastSentVisible = visible;
        DispatchInput(new { input = "visible", visible });
    }

    public void RefreshLayout()
    {
        EnsureX11HostStarted();
        SendResizeIfNeeded();
        if (_glViewport != null && _glViewport.HasPendingFrame())
            _glViewport.RequestNextFrameRendering();
    }

    public void ForceRefreshFrame()
    {
        EnsureX11HostStarted();
        _resizeDebounceTimer?.Stop();
        _lastSentWidth = -1;
        _lastSentHeight = -1;
        SendResizeIfNeeded();
        _glViewport?.RequestNextFrameRendering();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _glViewport?.Measure(availableSize);
        double w = double.IsInfinity(availableSize.Width) ? (_glViewport?.DesiredSize.Width ?? 0) : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? (_glViewport?.DesiredSize.Height ?? 0) : availableSize.Height;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _glViewport?.Arrange(new Rect(finalSize));
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible)
        {
            _glViewport?.RequestNextFrameRendering();
            ForceRefreshFrame();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_remoteChildPid <= 0 || IsVisible)
            _activeInstance = this;
        EnsureX11HostStarted();
        if (IsVisible)
            _glViewport?.RequestNextFrameRendering();
        SendResizeIfNeeded();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _resizeDebounceTimer?.Stop();
        if (_activeInstance == this)
            _activeInstance = null;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SendResizeIfNeeded();
        if (_glViewport != null)
            _glViewport.RequestNextFrameRendering();
        else
            InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_flash is null && _remoteChildPid <= 0)
            return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        Point p = e.GetPosition(this);
        DispatchInput(new { input = "mouse_move", x = p.X * scale, y = p.Y * scale });
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _activeInstance = this;
        Focus();
        GameClicked?.Invoke();
        if (_flash is null && _remoteChildPid <= 0)
            return;

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        Point p = e.GetPosition(this);
        PointerUpdateKind kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;

        DispatchInput(new { input = "mouse_move", x = p.X * scale, y = p.Y * scale });
        string button = kind switch
        {
            PointerUpdateKind.RightButtonPressed => "right",
            PointerUpdateKind.MiddleButtonPressed => "middle",
            _ => "left"
        };
        DispatchInput(new { input = "mouse_btn", button, down = true });
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_flash is null && _remoteChildPid <= 0)
            return;
        PointerUpdateKind kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        string button = kind switch
        {
            PointerUpdateKind.RightButtonReleased => "right",
            PointerUpdateKind.MiddleButtonReleased => "middle",
            _ => "left"
        };
        DispatchInput(new { input = "mouse_btn", button, down = false });
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_flash is null && _remoteChildPid <= 0)
            return;
        DispatchInput(new
        {
            input = "mouse_wheel",
            dx = e.Delta.X * 40.0,
            dy = e.Delta.Y * 40.0
        });
    }

    private void SendResizeIfNeeded()
    {
        if ((_flash is null && _remoteChildPid <= 0) || !IsVisible)
            return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = (int)Math.Round(Bounds.Width * scale);
        int h = (int)Math.Round(Bounds.Height * scale);
        if (w <= 2 || h <= 2)
            return;

        if (_lastSentWidth <= 0 || _lastSentHeight <= 0)
        {
            _lastSentWidth = w;
            _lastSentHeight = h;
            if (_x11Host != IntPtr.Zero)
                BonxieX11Native.Resize(_x11Host, (uint)w, (uint)h);
            DispatchInput(new { input = "resize", w, h });
            return;
        }

        _pendingResizeW = w;
        _pendingResizeH = h;
        _resizeDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _resizeDebounceTimer.Tick -= OnResizeDebounceTick;
        _resizeDebounceTimer.Tick += OnResizeDebounceTick;
        _resizeDebounceTimer.Stop();
        _resizeDebounceTimer.Start();
    }

    private void OnResizeDebounceTick(object? sender, EventArgs e)
    {
        _resizeDebounceTimer?.Stop();
        if ((_flash is null && _remoteChildPid <= 0) || !IsVisible)
            return;
        int w = _pendingResizeW;
        int h = _pendingResizeH;
        if (w <= 2 || h <= 2 || (w == _lastSentWidth && h == _lastSentHeight))
            return;
        _lastSentWidth = w;
        _lastSentHeight = h;
        if (_x11Host != IntPtr.Zero)
            BonxieX11Native.Resize(_x11Host, (uint)w, (uint)h);
        DispatchInput(new { input = "resize", w, h });
    }
}
