using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Threading;
using Skua.Backend.Flash;
using Skua.Backend.Ipc;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Skua.App.Avalonia.Controls;

public class GameHost : Control, ICustomHitTest
{
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    private const uint BtnLeft = 0x110;
    private const uint BtnRight = 0x111;
    private const uint BtnMiddle = 0x112;

    private static GameHost? _activeInstance;
    private static readonly ConcurrentDictionary<int, (IntPtr Handle, string Socket)> _pendingWaylandHosts = new();
    private static (PixelRect Bounds, uint RefreshMhz)[] _cachedOutputs = Array.Empty<(PixelRect, uint)>();
    private static readonly ConcurrentDictionary<IntPtr, byte> _liveWaylandHosts = new();
    private static volatile bool _uncapped = ReadInitialUncapped();
    private static volatile bool _interpolation = ReadInitialInterpolation();

    private static string GetRuffleConfigDir()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string configRoot = !string.IsNullOrEmpty(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(configRoot, "ruffle");
    }

    private static bool ReadInitialUncapped()
    {
        try
        {
            string path = Path.Combine(GetRuffleConfigDir(), "mugetsu-uncapped.txt");
            return File.Exists(path) && File.ReadAllText(path).Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadInitialInterpolation()
    {
        try
        {
            string path = Path.Combine(GetRuffleConfigDir(), "mugetsu-interpolation.txt");
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public static void SetAllUncapped(bool uncapped)
    {
        _uncapped = uncapped;
        byte flag = uncapped ? (byte)1 : (byte)0;
        foreach (IntPtr handle in _liveWaylandHosts.Keys)
        {
            try { BonxieWaylandNative.SetUncapped(handle, flag); } catch { }
        }
    }

    public static void SetAllInterpolation(bool enabled)
    {
        _interpolation = enabled;
        byte flag = enabled ? (byte)1 : (byte)0;
        foreach (IntPtr handle in _liveWaylandHosts.Keys)
        {
            try { BonxieWaylandNative.SetInterpolation(handle, flag); } catch { }
        }
    }

    private FlashUtil? _flash;
    private readonly int _remoteChildPid;
    private IntPtr _waylandHost;
    private string _waylandSocket = string.Empty;
    private uint _lastSentRefreshMhz;
    private Control? _glViewport;
    private TopLevel? _trackedTopLevel;
    private Func<bool>? _hasPendingFrame;
    private Action? _requestNextFrame;
    private volatile bool _isVisibleCached;
    private DispatcherTimer? _resizeDebounceTimer;
    private int _pendingResizeW;
    private int _pendingResizeH;
    private long _lastSeq;
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
            {
                _liveWaylandHosts[handle] = 0;
                BonxieWaylandNative.SetRefreshMhz(handle, DetectDisplayRefreshMhz());
                BonxieWaylandNative.SetUncapped(handle, _uncapped ? (byte)1 : (byte)0);
                BonxieWaylandNative.SetInterpolation(handle, _interpolation ? (byte)1 : (byte)0);
            }
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
            _liveWaylandHosts.TryRemove(handle, out _);
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
                    {
                        _liveWaylandHosts[_waylandHost] = 0;
                        BonxieWaylandNative.SetRefreshMhz(_waylandHost, DetectDisplayRefreshMhz());
                        BonxieWaylandNative.SetUncapped(_waylandHost, _uncapped ? (byte)1 : (byte)0);
                        BonxieWaylandNative.SetInterpolation(_waylandHost, _interpolation ? (byte)1 : (byte)0);
                    }
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
            AttachClipboardCallback();
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

    private static bool IsTopLevelVisible(TopLevel? tl) =>
        tl is { IsVisible: true } && (tl is not Window w || w.WindowState != WindowState.Minimized);

    private void OnWaylandViewportRendered(ulong seq, string mode)
    {
        IntPtr host = _waylandHost;
        if (host == IntPtr.Zero)
            return;

        if (seq != 0)
            BonxieWaylandNative.NotifyPresentedSeq(host, seq);
        else
            BonxieWaylandNative.NotifyPresented(host);

        if (seq != 0 && (long)seq != Volatile.Read(ref _lastSeq))
        {
            Volatile.Write(ref _lastSeq, (long)seq);
            RecordHostPresentedFrame(mode);
        }
    }

    private void SyncPollTimerState()
    {
        bool shouldRun = IsVisible && IsTopLevelVisible(TopLevel.GetTopLevel(this));
        _isVisibleCached = shouldRun;
        if (_glViewport != null && _glViewport.IsVisible != shouldRun)
            _glViewport.IsVisible = shouldRun;
        if (_waylandHost != IntPtr.Zero && _lastSentVisible != shouldRun)
        {
            _lastSentVisible = shouldRun;
            BonxieWaylandNative.SetVisible(_waylandHost, shouldRun ? (byte)1 : (byte)0);
        }
    }

    public void SetActiveInstance() => _activeInstance = this;

    public void DisposeResources()
    {
        _resizeDebounceTimer?.Stop();
        DetachTopLevelHandlers();
        if (_glViewport is WaylandDmabufViewport vp)
            vp.DisposeRenderer();
        _isVisibleCached = false;
        if (_activeInstance == this)
            _activeInstance = null;
        _flash = null;
        if (_waylandHost != IntPtr.Zero)
        {
            DetachClipboardCallback();
            IntPtr h = _waylandHost;
            _waylandHost = IntPtr.Zero;
            _liveWaylandHosts.TryRemove(h, out _);
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
            TabIpc.SendInput(_remoteChildPid, payload);
        }
    }

    public void AttachFlash(FlashUtil flash)
    {
        _flash = flash;
        if (!string.IsNullOrEmpty(_waylandSocket))
            flash.WaylandDisplay = _waylandSocket;
        SendResizeIfNeeded();
    }

    private static readonly Cursor s_cursorHidden = new(StandardCursorType.None);
    private static readonly Cursor s_cursorHand = new(StandardCursorType.Hand);
    private static readonly Cursor s_cursorIBeam = new(StandardCursorType.Ibeam);
    private static readonly Cursor s_cursorGrab = new(StandardCursorType.DragMove);
    private static readonly Cursor s_cursorCross = new(StandardCursorType.Cross);
    private static readonly Cursor s_cursorWait = new(StandardCursorType.Wait);
    private static readonly Cursor s_cursorNo = new(StandardCursorType.No);
    private static readonly Cursor s_cursorHelp = new(StandardCursorType.Help);

    private GCHandle _clipboardCallbackHandle;
    private bool _pasteInFlight;
    private bool _pasteReleasePending;

    private static Cursor MapCursorShape(uint shape) => shape switch
    {
        0 => s_cursorHidden,
        2 => s_cursorHand,
        3 => s_cursorIBeam,
        4 => s_cursorGrab,
        5 => s_cursorCross,
        6 => s_cursorWait,
        7 => s_cursorNo,
        8 => s_cursorHelp,
        _ => Cursor.Default
    };

    private unsafe void AttachClipboardCallback()
    {
        if (_waylandHost == IntPtr.Zero || _clipboardCallbackHandle.IsAllocated)
            return;
        _clipboardCallbackHandle = GCHandle.Alloc(this);
        nuint userData = (nuint)GCHandle.ToIntPtr(_clipboardCallbackHandle);
        BonxieWaylandNative.SetClipboardCallback(_waylandHost, &OnNativeClipboardCopy, userData);
        BonxieWaylandNative.SetCursorShapeCallback(_waylandHost, &OnNativeCursorShape, userData);
    }

    private unsafe void DetachClipboardCallback()
    {
        if (_waylandHost != IntPtr.Zero)
        {
            BonxieWaylandNative.SetClipboardCallback(_waylandHost, null, 0);
            BonxieWaylandNative.SetCursorShapeCallback(_waylandHost, null, 0);
        }
        if (_clipboardCallbackHandle.IsAllocated)
        {
            _clipboardCallbackHandle.Free();
            _clipboardCallbackHandle = default;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeCursorShape(nuint userData, uint shape)
    {
        try
        {
            if (userData == 0)
                return;
            GCHandle handle = GCHandle.FromIntPtr((IntPtr)userData);
            if (handle.Target is not GameHost host)
                return;
            Cursor cursor = MapCursorShape(shape);
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(host.Cursor, cursor))
                    host.Cursor = cursor;
            });
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnNativeClipboardCopy(nuint userData, byte* utf8Ptr, nuint len)
    {
        try
        {
            if (userData == 0)
                return;
            GCHandle handle = GCHandle.FromIntPtr((IntPtr)userData);
            if (handle.Target is not GameHost host)
                return;
            string text = (utf8Ptr != null && len > 0)
                ? Encoding.UTF8.GetString(utf8Ptr, (int)Math.Min(len, 4 * 1024 * 1024))
                : string.Empty;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    _ = TopLevel.GetTopLevel(host)?.Clipboard?.SetTextAsync(text);
                    if (host._waylandHost != IntPtr.Zero)
                        BonxieWaylandNative.SetClipboard(host._waylandHost, text);
                }
                catch { }
            });
        }
        catch { }
    }

    private async void SyncHostClipboardToWaylandAsync()
    {
        IntPtr wlHost = _waylandHost;
        if (wlHost == IntPtr.Zero)
            return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
                return;
            string? text = await clipboard.TryGetTextAsync();
            if (_waylandHost == wlHost && wlHost != IntPtr.Zero)
                BonxieWaylandNative.SetClipboard(wlHost, text ?? string.Empty);
        }
        catch { }
    }

    private async void SyncClipboardAndForwardPasteAsync(IntPtr wlHost)
    {
        _pasteInFlight = true;
        _pasteReleasePending = false;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            string? text = clipboard != null ? await clipboard.TryGetTextAsync() : null;
            if (_waylandHost == wlHost && wlHost != IntPtr.Zero)
                BonxieWaylandNative.SetClipboard(wlHost, text ?? string.Empty);
        }
        catch { }
        finally
        {
            _pasteInFlight = false;
            if (_waylandHost == wlHost && wlHost != IntPtr.Zero)
            {
                BonxieWaylandNative.Key(wlHost, 47, 1);
                if (_pasteReleasePending)
                {
                    _pasteReleasePending = false;
                    _pressedEvdevKeys.Remove(47);
                    BonxieWaylandNative.Key(wlHost, 47, 0);
                }
            }
        }
    }

    public static void ForwardKey(bool down, string physical, string logical, bool ctrl, bool shift, bool alt)
    {
        if (_activeInstance is { _waylandHost: nint wlHost } && wlHost != IntPtr.Zero)
        {
            uint evdev = MapKeyToEvdev(physical, logical);
            if (evdev != 0)
            {
                if (evdev == 47 && down && ctrl && !alt && !_activeInstance._pasteInFlight)
                {
                    _activeInstance._pressedEvdevKeys.Add(evdev);
                    _activeInstance.SyncClipboardAndForwardPasteAsync(wlHost);
                    return;
                }
                if (evdev == 47 && !down && _activeInstance._pasteInFlight)
                {
                    _activeInstance._pasteReleasePending = true;
                    return;
                }
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
        // Wayland handles window/keyboard focus natively via wl_keyboard.enter/leave.
    }

    public static void ForwardClearKeys()
    {
        if (_activeInstance is { _waylandHost: nint wlHost } && wlHost != IntPtr.Zero)
        {
            foreach (uint code in _activeInstance._pressedEvdevKeys)
                BonxieWaylandNative.Key(wlHost, code, 0);
            _activeInstance._pressedEvdevKeys.Clear();
        }
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
        ["IntlBackslash"] = 86, ["Oem102"] = 86,
        ["F11"] = 87, ["F12"] = 88,
        ["IntlRo"] = 89, ["AbntC1"] = 89,
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
        ["NumpadComma"] = 121, ["AbntC2"] = 121,
        ["IntlYen"] = 124,
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
        Volatile.Write(ref _lastSeq, 0);
        SendResizeIfNeeded();
        _requestNextFrame?.Invoke();
    }

    public void SetHostedVisible(bool visible)
    {
        if (_remoteChildPid > 0)
            return;
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

    private void AttachTopLevelHandlers(TopLevel? tl)
    {
        if (ReferenceEquals(_trackedTopLevel, tl))
            return;

        DetachTopLevelHandlers();
        _trackedTopLevel = tl;
        if (tl is Window w)
        {
            w.Opened += OnTrackedTopLevelOpened;
            w.Activated += OnTrackedTopLevelActivated;
            w.PositionChanged += OnTrackedTopLevelPositionChanged;
        }
        if (tl != null)
            tl.PropertyChanged += OnTrackedTopLevelPropertyChanged;
    }

    private void DetachTopLevelHandlers()
    {
        if (_trackedTopLevel is Window w)
        {
            w.Opened -= OnTrackedTopLevelOpened;
            w.Activated -= OnTrackedTopLevelActivated;
            w.PositionChanged -= OnTrackedTopLevelPositionChanged;
        }
        if (_trackedTopLevel != null)
            _trackedTopLevel.PropertyChanged -= OnTrackedTopLevelPropertyChanged;
        _trackedTopLevel = null;
    }

    private void OnTrackedTopLevelOpened(object? sender, EventArgs e)
    {
        SyncPollTimerState();
        UpdateRefreshRateForWindow();
        if (IsVisible)
            _requestNextFrame?.Invoke();
        SendResizeIfNeeded();
    }

    private void OnTrackedTopLevelActivated(object? sender, EventArgs e)
    {
        UpdateRefreshRateForWindow();
        SyncHostClipboardToWaylandAsync();
    }

    private void OnTrackedTopLevelPositionChanged(object? sender, PixelPointEventArgs e)
    {
        UpdateRefreshRateForWindow();
    }

    private void OnTrackedTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty || e.Property == Window.WindowStateProperty)
        {
            SyncPollTimerState();
            if (IsVisible && IsTopLevelVisible(_trackedTopLevel))
                _requestNextFrame?.Invoke();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_remoteChildPid <= 0 || IsVisible)
            _activeInstance = this;
        AttachTopLevelHandlers(TopLevel.GetTopLevel(this));
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
        DetachTopLevelHandlers();
        _isVisibleCached = false;
        if (_glViewport != null)
            _glViewport.IsVisible = false;
        if (_waylandHost != IntPtr.Zero && _lastSentVisible != false)
        {
            _lastSentVisible = false;
            BonxieWaylandNative.SetVisible(_waylandHost, 0);
        }
        _resizeDebounceTimer?.Stop();
        if (_activeInstance == this)
            _activeInstance = null;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRefreshRateForWindow();
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
        (double x, double y) = MapPointerPosition(e.GetPosition(this), scale);
        if (_waylandHost != IntPtr.Zero)
            BonxieWaylandNative.PointerMotion(_waylandHost, x, y);
        else
            DispatchInput(new { input = "mouse_move", x, y });
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
        (double x, double y) = MapPointerPosition(e.GetPosition(this), scale);
        PointerUpdateKind kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;

        if (_waylandHost != IntPtr.Zero)
        {
            UpdateRefreshRateForWindow();
            BonxieWaylandNative.PointerMotion(_waylandHost, x, y);
            uint wlBtn = kind switch
            {
                PointerUpdateKind.RightButtonPressed => BtnRight,
                PointerUpdateKind.MiddleButtonPressed => BtnMiddle,
                _ => BtnLeft
            };
            BonxieWaylandNative.PointerButton(_waylandHost, wlBtn, 1);
            return;
        }

        DispatchInput(new { input = "mouse_move", x, y });
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
        SyncHostClipboardToWaylandAsync();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
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

    private bool _stretchToFill;
    private double _renderResolutionScale = 1.0;

    public bool StretchToFill
    {
        get => _stretchToFill;
        set
        {
            if (_stretchToFill == value)
                return;
            _stretchToFill = value;
            if (_glViewport is WaylandDmabufViewport dmaVp)
                dmaVp.StretchToFill = value;
            SendResizeIfNeeded();
        }
    }

    public double RenderResolutionScale
    {
        get => _renderResolutionScale;
        set
        {
            double clamped = Math.Clamp(value, 0.5, 1.0);
            if (Math.Abs(_renderResolutionScale - clamped) < 0.001)
                return;
            _renderResolutionScale = clamped;
            if (_glViewport is WaylandDmabufViewport dmaVp)
                dmaVp.ForceScaleToBounds = clamped < 0.999;
            SendResizeIfNeeded();
        }
    }

    private (double X, double Y) MapPointerPosition(Point p, double scale)
    {
        if ((_stretchToFill || _renderResolutionScale < 0.999) && _lastSentWidth > 0 && _lastSentHeight > 0 && Bounds.Width > 0 && Bounds.Height > 0)
            return (p.X / Bounds.Width * _lastSentWidth, p.Y / Bounds.Height * _lastSentHeight);
        return (p.X * scale, p.Y * scale);
    }

    private void SendResizeIfNeeded()
    {
        if ((_flash is null && _remoteChildPid <= 0 && _waylandHost == IntPtr.Zero) || !IsVisible)
            return;
        double scale = _renderResolutionScale < 0.999
            ? _renderResolutionScale
            : (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0);
        double rw = Bounds.Width * scale;
        double rh = Bounds.Height * scale;
        int w;
        int h;
        if (_stretchToFill && rw > 2 && rh > 2)
        {
            const double aspect = 960.0 / 550.0;
            if (rw / rh > aspect)
            {
                h = Math.Max(2, (int)Math.Round(rh));
                w = Math.Max(2, (int)Math.Round(rh * aspect));
            }
            else
            {
                w = Math.Max(2, (int)Math.Round(rw));
                h = Math.Max(2, (int)Math.Round(rw / aspect));
            }
        }
        else
        {
            w = Math.Max(1, (int)Math.Round(rw));
            h = Math.Max(1, (int)Math.Round(rh));
        }
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
