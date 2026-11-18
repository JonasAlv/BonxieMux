using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Threading;
using Skua.App.Avalonia.Flash;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Skua.App.Avalonia.Controls;

/// <summary>
/// Pure Wayland/Avalonia game viewport. Hosts an embedded Wayland compositor
/// (<c>libbonxie_wayland.so</c>) for zero-copy <c>DMA-BUF</c> presentation inside
/// Avalonia's OpenGL context via <see cref="OpenGlControlBase"/>.
/// </summary>
public class GameHost : Control, ICustomHitTest
{
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    private const uint BtnLeft = 0x110;
    private const uint BtnRight = 0x111;
    private const uint BtnMiddle = 0x112;

    private static GameHost? _activeInstance;
    private static readonly ConcurrentDictionary<int, (IntPtr Handle, string Socket)> _pendingWaylandHosts = new();
    private static (PixelRect Bounds, uint RefreshMhz)[] _cachedOutputs = Array.Empty<(PixelRect, uint)>();

    private FlashUtil? _flash;
    private readonly int _remoteChildPid;
    private IntPtr _waylandHost;
    private string _waylandSocket = string.Empty;
    private uint _lastSentRefreshMhz;
    private Control? _glViewport;
    private Func<bool>? _hasPendingFrame;
    private Action? _requestNextFrame;
    private volatile bool _isVisibleCached;
    private DispatcherTimer? _resizeDebounceTimer;
    private int _pendingResizeW;
    private int _pendingResizeH;
    private ulong _lastSeq;
    private int _lastSentWidth;
    private int _lastSentHeight;
    private bool? _lastSentVisible;
    private readonly HashSet<uint> _pressedEvdevKeys = new();
    private readonly double[] _frameIntervalsMs = new double[512];
    private int _intervalWriteIdx;
    private int _intervalCount;
    private long _lastFrameTimestamp;
    private long _windowStartTimestamp;
    private int _windowFrames;
    private long _totalPresentedFrames;
    private int _statsWriteInFlight;

    public static event Action? GameClicked;

    public static uint DetectDisplayRefreshMhz(Window? window = null)
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

        var list = new System.Collections.Generic.List<(PixelRect Bounds, uint RefreshMhz)>();
        try
        {
            const int MaxOutputs = 16;
            BonxieOutputInfo* buf = stackalloc BonxieOutputInfo[MaxOutputs];
            int count = BonxieWaylandNative.QueryOutputs(buf, MaxOutputs);
            for (int i = 0; i < count; i++)
            {
                uint mhz = Math.Clamp(buf[i].RefreshMhz, 24_000u, 360_000u);
                list.Add((new PixelRect(buf[i].X, buf[i].Y, buf[i].Width, buf[i].Height), mhz));
            }
        }
        catch { }

        if (list.Count > 0)
        {
            _cachedOutputs = list.ToArray();
        }
        return _cachedOutputs;
    }

    public static (IntPtr Handle, string Socket) CreateWaylandHostForChild(uint width = 1280, uint height = 720)
    {
        try
        {
            var (handle, socket) = BonxieWaylandNative.StartHost(width, height, true);
            if (handle != IntPtr.Zero)
                BonxieWaylandNative.SetRefreshMhz(handle, DetectDisplayRefreshMhz());
            return (handle, socket);
        }
        catch
        {
            return (IntPtr.Zero, string.Empty);
        }
    }

    public static void RegisterPendingWaylandHost(int childPid, IntPtr handle, string socket)
    {
        if (handle != IntPtr.Zero && childPid > 0)
            _pendingWaylandHosts[childPid] = (handle, socket);
    }

    public static void ReleasePendingWaylandHost(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            try { BonxieWaylandNative.Stop(handle); } catch { }
        }
    }

    public GameHost() : this(0)
    {
    }

    public GameHost(int remoteChildPid)
    {
        Focusable = true;
        ClipToBounds = true;
        _remoteChildPid = remoteChildPid;
        _isVisibleCached = IsVisible;

        if (remoteChildPid <= 0)
        {
            _activeInstance = this;
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BONXIE_WL_DISPLAY")))
            {
                try
                {
                    (_waylandHost, _waylandSocket) = BonxieWaylandNative.StartHost(1280, 720, true);
                    if (_waylandHost != IntPtr.Zero)
                        BonxieWaylandNative.SetRefreshMhz(_waylandHost, DetectDisplayRefreshMhz());
                    if (!string.IsNullOrEmpty(_waylandSocket))
                    {
                        Environment.SetEnvironmentVariable("BONXIE_WL_DISPLAY", _waylandSocket);
                    }
                }
                catch
                {
                    _waylandHost = IntPtr.Zero;
                    _waylandSocket = string.Empty;
                }
            }
        }
        else
        {
            if (_pendingWaylandHosts.TryRemove(remoteChildPid, out var pending))
            {
                _waylandHost = pending.Handle;
                _waylandSocket = pending.Socket;
            }
        }

        if (_waylandHost != IntPtr.Zero)
        {
            var dmaVp = new WaylandDmabufViewport(
                _waylandHost,
                static () => { },
                seq => OnWaylandViewportRendered(seq, "Wayland DMA-BUF"));
            _glViewport = dmaVp;
            _hasPendingFrame = dmaVp.HasPendingFrame;
            _requestNextFrame = dmaVp.RequestNextFrameRendering;
            LogicalChildren.Add(_glViewport);
            VisualChildren.Add(_glViewport);
        }
    }

    private void OnWaylandViewportRendered(ulong seq, string mode)
    {
        if (_waylandHost == IntPtr.Zero)
            return;

        BonxieWaylandNative.NotifyPresented(_waylandHost);

        if (seq != 0 && seq != _lastSeq)
        {
            _lastSeq = seq;
            RecordHostPresentedFrame(mode);
        }
    }

    private void SyncPollTimerState()
    {
        bool shouldRun = IsVisible && TopLevel.GetTopLevel(this) is { IsVisible: true };
        _isVisibleCached = shouldRun;
    }

    public void SetActiveInstance() => _activeInstance = this;

    public void DisposeResources()
    {
        _resizeDebounceTimer?.Stop();
        _isVisibleCached = false;
        if (_activeInstance == this)
            _activeInstance = null;
        _flash = null;
        if (_waylandHost != IntPtr.Zero)
        {
            IntPtr h = _waylandHost;
            _waylandHost = IntPtr.Zero;
            try
            {
                BonxieWaylandNative.Stop(h);
            }
            catch { }
        }
        TryDeleteHostPerfFile();
    }

    private void DispatchInput(object payload)
    {
        if (_flash != null)
        {
            _flash.SendInput(payload);
        }
        else if (_remoteChildPid > 0)
        {
            Skua.App.Avalonia.Services.TabIpc.SendInput(_remoteChildPid, payload);
        }
    }

    public void AttachFlash(FlashUtil flash)
    {
        _flash = flash;
        if (!string.IsNullOrEmpty(_waylandSocket))
            flash.WaylandDisplay = _waylandSocket;
        SendResizeIfNeeded();
    }

    public static void ForwardKey(bool down, string physical, string logical, bool ctrl, bool shift, bool alt)
    {
        if (_activeInstance is { _waylandHost: nint wlHost } && wlHost != IntPtr.Zero)
        {
            uint evdev = MapKeyToEvdev(physical, logical);
            if (evdev != 0)
            {
                if (down)
                    _activeInstance._pressedEvdevKeys.Add(evdev);
                else
                    _activeInstance._pressedEvdevKeys.Remove(evdev);
                BonxieWaylandNative.Key(wlHost, evdev, down ? (byte)1 : (byte)0);
            }
            return;
        }

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
        if (_activeInstance is { _waylandHost: nint wlHost } && wlHost != IntPtr.Zero)
            return;
        if (string.IsNullOrEmpty(text))
            return;
        _activeInstance?.DispatchInput(new { input = "text", text });
    }

    public static void ForwardFocus(bool focused)
    {
        if (_activeInstance is { _waylandHost: nint wlHost } && wlHost != IntPtr.Zero)
            return;
        _activeInstance?.DispatchInput(new { input = "focus", focused });
    }

    public static void ForwardClearKeys()
    {
        if (_activeInstance is { _waylandHost: nint wlHost } && wlHost != IntPtr.Zero)
        {
            foreach (uint code in _activeInstance._pressedEvdevKeys)
                BonxieWaylandNative.Key(wlHost, code, 0);
            _activeInstance._pressedEvdevKeys.Clear();
            return;
        }
        _activeInstance?.DispatchInput(new { input = "clear_keys" });
    }

    private static readonly Dictionary<string, uint> EvdevKeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Escape"] = 1,
        ["Digit1"] = 2, ["D1"] = 2, ["1"] = 2,
        ["Digit2"] = 3, ["D2"] = 3, ["2"] = 3,
        ["Digit3"] = 4, ["D3"] = 4, ["3"] = 4,
        ["Digit4"] = 5, ["D4"] = 5, ["4"] = 5,
        ["Digit5"] = 6, ["D5"] = 6, ["5"] = 6,
        ["Digit6"] = 7, ["D6"] = 7, ["6"] = 7,
        ["Digit7"] = 8, ["D7"] = 8, ["7"] = 8,
        ["Digit8"] = 9, ["D8"] = 9, ["8"] = 9,
        ["Digit9"] = 10, ["D9"] = 10, ["9"] = 10,
        ["Digit0"] = 11, ["D0"] = 11, ["0"] = 11,
        ["Minus"] = 12, ["OemMinus"] = 12, ["-"] = 12,
        ["Equal"] = 13, ["OemPlus"] = 13, ["="] = 13,
        ["Backspace"] = 14, ["Back"] = 14,
        ["Tab"] = 15,
        ["Q"] = 16, ["W"] = 17, ["E"] = 18, ["R"] = 19, ["T"] = 20, ["Y"] = 21, ["U"] = 22, ["I"] = 23, ["O"] = 24, ["P"] = 25,
        ["BracketLeft"] = 26, ["OemOpenBrackets"] = 26, ["["] = 26,
        ["BracketRight"] = 27, ["OemCloseBrackets"] = 27, ["]"] = 27,
        ["Enter"] = 28, ["Return"] = 28,
        ["ControlLeft"] = 29, ["LeftCtrl"] = 29,
        ["A"] = 30, ["S"] = 31, ["D"] = 32, ["F"] = 33, ["G"] = 34, ["H"] = 35, ["J"] = 36, ["K"] = 37, ["L"] = 38,
        ["Semicolon"] = 39, ["OemSemicolon"] = 39, [";"] = 39,
        ["Quote"] = 40, ["OemQuotes"] = 40, ["'"] = 40,
        ["Backquote"] = 41, ["OemTilde"] = 41, ["`"] = 41,
        ["ShiftLeft"] = 42, ["LeftShift"] = 42,
        ["Backslash"] = 43, ["OemPipe"] = 43, ["\\"] = 43,
        ["Z"] = 44, ["X"] = 45, ["C"] = 46, ["V"] = 47, ["B"] = 48, ["N"] = 49, ["M"] = 50,
        ["Comma"] = 51, ["OemComma"] = 51, [","] = 51,
        ["Period"] = 52, ["OemPeriod"] = 52, ["."] = 52,
        ["Slash"] = 53, ["OemQuestion"] = 53, ["/"] = 53,
        ["ShiftRight"] = 54, ["RightShift"] = 54,
        ["NumpadMultiply"] = 55, ["Multiply"] = 55,
        ["AltLeft"] = 56, ["LeftAlt"] = 56,
        ["Space"] = 57, [" "] = 57,
        ["CapsLock"] = 58, ["Capital"] = 58,
        ["F1"] = 59, ["F2"] = 60, ["F3"] = 61, ["F4"] = 62, ["F5"] = 63, ["F6"] = 64, ["F7"] = 65, ["F8"] = 66, ["F9"] = 67, ["F10"] = 68,
        ["NumLock"] = 69,
        ["ScrollLock"] = 70, ["Scroll"] = 70,
        ["Numpad7"] = 71, ["NumPad7"] = 71,
        ["Numpad8"] = 72, ["NumPad8"] = 72,
        ["Numpad9"] = 73, ["NumPad9"] = 73,
        ["NumpadSubtract"] = 74, ["Subtract"] = 74,
        ["Numpad4"] = 75, ["NumPad4"] = 75,
        ["Numpad5"] = 76, ["NumPad5"] = 76,
        ["Numpad6"] = 77, ["NumPad6"] = 77,
        ["NumpadAdd"] = 78, ["Add"] = 78,
        ["Numpad1"] = 79, ["NumPad1"] = 79,
        ["Numpad2"] = 80, ["NumPad2"] = 80,
        ["Numpad3"] = 81, ["NumPad3"] = 81,
        ["Numpad0"] = 82, ["NumPad0"] = 82,
        ["NumpadDecimal"] = 83, ["Decimal"] = 83,
        ["F11"] = 87, ["F12"] = 88,
        ["NumpadEnter"] = 96,
        ["ControlRight"] = 97, ["RightCtrl"] = 97,
        ["NumpadDivide"] = 98, ["Divide"] = 98,
        ["PrintScreen"] = 99, ["Snapshot"] = 99,
        ["AltRight"] = 100, ["RightAlt"] = 100,
        ["Home"] = 102,
        ["ArrowUp"] = 103, ["Up"] = 103,
        ["PageUp"] = 104,
        ["ArrowLeft"] = 105, ["Left"] = 105,
        ["ArrowRight"] = 106, ["Right"] = 106,
        ["End"] = 107,
        ["ArrowDown"] = 108, ["Down"] = 108,
        ["PageDown"] = 109, ["Next"] = 109,
        ["Insert"] = 110,
        ["Delete"] = 111,
        ["MetaLeft"] = 125, ["LWin"] = 125,
        ["MetaRight"] = 126, ["RWin"] = 126,
    };

    private static uint MapKeyToEvdev(string physical, string logical)
    {
        if (!string.IsNullOrEmpty(physical) && EvdevKeyMap.TryGetValue(physical, out uint code))
            return code;
        if (!string.IsNullOrEmpty(logical) && EvdevKeyMap.TryGetValue(logical, out code))
            return code;
        return 0;
    }

    public void SetScaleMode(bool isExactFit = false)
    {
    }

    public void RefreshLayout()
    {
        SyncPollTimerState();
        SendResizeIfNeeded();
        if (_hasPendingFrame != null && _hasPendingFrame() && _requestNextFrame != null)
            _requestNextFrame();
    }

    public void ForceRefreshFrame()
    {
        SyncPollTimerState();
        _resizeDebounceTimer?.Stop();
        _lastSentWidth = -1;
        _lastSentHeight = -1;
        _lastSeq = 0;
        SendResizeIfNeeded();
        _requestNextFrame?.Invoke();
    }

    public void SetHostedVisible(bool visible)
    {
        if (_remoteChildPid > 0)
            return;
        _lastSentVisible = visible;
        DispatchInput(new { input = "visible", visible });
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            SyncPollTimerState();
            if (IsVisible)
            {
                _requestNextFrame?.Invoke();
                ForceRefreshFrame();
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_remoteChildPid <= 0 || IsVisible)
            _activeInstance = this;
        SyncPollTimerState();
        UpdateRefreshRateForWindow();
        if (IsVisible)
            _requestNextFrame?.Invoke();
        SendResizeIfNeeded();
    }

    private void UpdateRefreshRateForWindow()
    {
        if (_waylandHost == IntPtr.Zero)
            return;
        Window? win = TopLevel.GetTopLevel(this) as Window;
        uint mhz = DetectDisplayRefreshMhz(win);
        if (mhz != _lastSentRefreshMhz)
        {
            _lastSentRefreshMhz = mhz;
            BonxieWaylandNative.SetRefreshMhz(_waylandHost, mhz);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isVisibleCached = false;
        _resizeDebounceTimer?.Stop();
        if (_activeInstance == this)
            _activeInstance = null;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SendResizeIfNeeded();
        if (_requestNextFrame != null)
            _requestNextFrame();
        else
            InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_flash is null && _remoteChildPid <= 0 && _waylandHost == IntPtr.Zero)
            return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        Point p = e.GetPosition(this);
        if (_waylandHost != IntPtr.Zero)
            BonxieWaylandNative.PointerMotion(_waylandHost, p.X * scale, p.Y * scale);
        else
            DispatchInput(new { input = "mouse_move", x = p.X * scale, y = p.Y * scale });
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _activeInstance = this;
        Focus();
        GameClicked?.Invoke();
        if (_flash is null && _remoteChildPid <= 0 && _waylandHost == IntPtr.Zero)
            return;

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        Point p = e.GetPosition(this);
        PointerUpdateKind kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;

        if (_waylandHost != IntPtr.Zero)
        {
            BonxieWaylandNative.PointerMotion(_waylandHost, p.X * scale, p.Y * scale);
            uint wlBtn = kind switch
            {
                PointerUpdateKind.RightButtonPressed => BtnRight,
                PointerUpdateKind.MiddleButtonPressed => BtnMiddle,
                _ => BtnLeft
            };
            BonxieWaylandNative.PointerButton(_waylandHost, wlBtn, 1);
            return;
        }

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
        if (_flash is null && _remoteChildPid <= 0 && _waylandHost == IntPtr.Zero)
            return;
        PointerUpdateKind kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (_waylandHost != IntPtr.Zero)
        {
            uint wlBtn = kind switch
            {
                PointerUpdateKind.RightButtonReleased => BtnRight,
                PointerUpdateKind.MiddleButtonReleased => BtnMiddle,
                _ => BtnLeft
            };
            BonxieWaylandNative.PointerButton(_waylandHost, wlBtn, 0);
            return;
        }
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
        if (_waylandHost != IntPtr.Zero)
        {
            BonxieWaylandNative.PointerScroll(_waylandHost, e.Delta.X * 15.0, e.Delta.Y * 15.0);
            return;
        }
        DispatchInput(new
        {
            input = "mouse_wheel",
            dx = e.Delta.X * 40.0,
            dy = e.Delta.Y * 40.0
        });
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _activeInstance = this;
        DispatchInput(new { input = "focus", focused = true });
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        DispatchInput(new { input = "focus", focused = false });
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _glViewport?.Arrange(new Rect(finalSize));
        return base.ArrangeOverride(finalSize);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
    }

    private void SendResizeIfNeeded()
    {
        if ((_flash is null && _remoteChildPid <= 0 && _waylandHost == IntPtr.Zero) || !IsVisible)
            return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)Math.Round(Bounds.Width * scale));
        int h = Math.Max(1, (int)Math.Round(Bounds.Height * scale));
        if (w <= 2 || h <= 2)
            return;
        if (w == _lastSentWidth && h == _lastSentHeight)
            return;

        if (_lastSentWidth <= 0 || _lastSentHeight <= 0)
        {
            _lastSentWidth = w;
            _lastSentHeight = h;
            if (_waylandHost != IntPtr.Zero)
                BonxieWaylandNative.Resize(_waylandHost, (uint)w, (uint)h);
            else
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
        if ((_flash is null && _remoteChildPid <= 0 && _waylandHost == IntPtr.Zero) || !IsVisible)
            return;
        int w = _pendingResizeW;
        int h = _pendingResizeH;
        if (w <= 2 || h <= 2 || (w == _lastSentWidth && h == _lastSentHeight))
            return;
        _lastSentWidth = w;
        _lastSentHeight = h;
        if (_waylandHost != IntPtr.Zero)
            BonxieWaylandNative.Resize(_waylandHost, (uint)w, (uint)h);
        else
            DispatchInput(new { input = "resize", w, h });
    }

    private static string GetHostPerfPath(int targetPid) =>
        Path.Combine(Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(), $"skua-host-perf-{targetPid}.json");

    private void RecordHostPresentedFrame(string mode)
    {
        long now = Stopwatch.GetTimestamp();
        if (_windowStartTimestamp == 0)
            _windowStartTimestamp = now;

        if (_lastFrameTimestamp != 0)
        {
            double dtMs = (double)(now - _lastFrameTimestamp) * 1000.0 / Stopwatch.Frequency;
            if (dtMs > 0.0 && dtMs < 5000.0)
            {
                _frameIntervalsMs[_intervalWriteIdx] = dtMs;
                _intervalWriteIdx = (_intervalWriteIdx + 1) % _frameIntervalsMs.Length;
                if (_intervalCount < _frameIntervalsMs.Length)
                    _intervalCount++;
            }
        }

        _lastFrameTimestamp = now;
        _windowFrames++;
        _totalPresentedFrames++;

        double elapsedSec = (double)(now - _windowStartTimestamp) / Stopwatch.Frequency;
        if (elapsedSec < 1.0 || _windowFrames <= 0)
            return;

        double hostFps = _windowFrames / elapsedSec;
        double avgMs = elapsedSec * 1000.0 / _windowFrames;
        double maxMs = avgMs;
        double onePercentLowFps = hostFps;

        int n = _intervalCount;
        if (n > 0)
        {
            Span<double> scratch = stackalloc double[n];
            _frameIntervalsMs.AsSpan(0, n).CopyTo(scratch);
            scratch.Sort();

            double sum = 0;
            for (int i = 0; i < n; i++)
                sum += scratch[i];
            avgMs = sum / n;
            maxMs = scratch[n - 1];

            int worstCount = Math.Max(1, (n + 99) / 100);
            double worstSum = 0;
            for (int i = n - worstCount; i < n; i++)
                worstSum += scratch[i];
            double onePercentLowMs = worstSum / worstCount;
            if (onePercentLowMs > 0.0)
                onePercentLowFps = 1000.0 / onePercentLowMs;
        }

        _windowFrames = 0;
        _windowStartTimestamp = now;
        _intervalCount = 0;
        _intervalWriteIdx = 0;

        if (Interlocked.CompareExchange(ref _statsWriteInFlight, 1, 0) != 0)
            return;

        int targetPid = _remoteChildPid > 0 ? _remoteChildPid : Environment.ProcessId;
        int hostPid = Environment.ProcessId;
        long totalFrames = _totalPresentedFrames;

        _ = Task.Run(() =>
        {
            try
            {
                string path = GetHostPerfPath(targetPid);
                string tmp = path + ".tmp";
                string json = JsonSerializer.Serialize(new
                {
                    HostPid = hostPid,
                    ChildPid = targetPid,
                    Mode = mode,
                    HostPresentedFps = Math.Round(hostFps, 2),
                    AvgFrameMs = Math.Round(avgMs, 2),
                    MaxFrameMs = Math.Round(maxMs, 2),
                    OnePercentLowFps = Math.Round(onePercentLowFps, 2),
                    TotalPresentedFrames = totalFrames,
                    UpdatedAtUtc = DateTime.UtcNow
                });
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
            catch { }
            finally
            {
                Volatile.Write(ref _statsWriteInFlight, 0);
            }
        });
    }

    private void TryDeleteHostPerfFile()
    {
        try
        {
            int targetPid = _remoteChildPid > 0 ? _remoteChildPid : Environment.ProcessId;
            string path = GetHostPerfPath(targetPid);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
