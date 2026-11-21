using Avalonia.Input;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Skua.App.Avalonia.Services;

/// <summary>
/// Linux hotkeys backed by <c>libbonxie_x11.so</c> (<c>Skua.X11Host</c>).
/// The game runs in the player's own X11 window inside BonxieMux's, and while it has keyboard focus
/// Avalonia never sees a key press. Grabbing configured keys on BonxieMux's window via Rust (<c>x11rb</c>)
/// delivers matching presses cleanly without raw libX11 P/Invoke or polling.
/// </summary>
internal sealed unsafe partial class X11HotKeyGrabber : IDisposable
{
    public readonly record struct Gesture(Key Key, KeyModifiers Modifiers);

    private const string Lib = "bonxie_x11";
    private const uint ModShiftBit = 1 << 0;
    private const uint ModControlBit = 1 << 1;
    private const uint ModAltBit = 1 << 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeHotkeySpec
    {
        public ulong Tag;
        public uint Keysym;
        public uint Modifiers;
    }

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_hotkeys_create")]
    private static partial IntPtr NativeCreate(
        delegate* unmanaged[Cdecl]<nuint, ulong, void> cb,
        nuint userData);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_hotkeys_update")]
    private static partial void NativeUpdate(
        IntPtr handle,
        uint windowXid,
        NativeHotkeySpec* specs,
        nuint count);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_hotkeys_destroy")]
    private static partial void NativeDestroy(IntPtr handle);

    private readonly Action<Gesture> _onPressed;
    private readonly object _lock = new();
    private IntPtr _handle;
    private GCHandle _selfHandle;
    private volatile bool _disposed;

    public X11HotKeyGrabber(Action<Gesture> onPressed)
    {
        _onPressed = onPressed;
    }

    /// <summary>Grabs <paramref name="gestures"/> on the X11 window <paramref name="window"/>, replacing the previous grabs.</summary>
    public void Update(IntPtr window, IEnumerable<Gesture> gestures)
    {
        List<NativeHotkeySpec> specsList = [];
        foreach (Gesture gesture in gestures)
        {
            uint keysym = ToKeysym(gesture.Key);
            if (keysym == 0)
                continue;

            specsList.Add(new NativeHotkeySpec
            {
                Tag = ((ulong)(uint)gesture.Modifiers << 32) | (uint)gesture.Key,
                Keysym = keysym,
                Modifiers = ToModifierMask(gesture.Modifiers)
            });
        }

        NativeHotkeySpec[] specs = [.. specsList];
        uint windowXid = unchecked((uint)window.ToInt64());

        lock (_lock)
        {
            if (_disposed)
                return;

            if (_handle == IntPtr.Zero)
            {
                _selfHandle = GCHandle.Alloc(this);
                _handle = NativeCreate(&OnNativeHotkey, (nuint)GCHandle.ToIntPtr(_selfHandle));
                if (_handle == IntPtr.Zero)
                {
                    if (_selfHandle.IsAllocated)
                        _selfHandle.Free();
                    _selfHandle = default;
                    return;
                }
            }

            fixed (NativeHotkeySpec* ptr = specs)
            {
                NativeUpdate(_handle, windowXid, ptr, (nuint)specs.Length);
            }
        }
    }

    public void Dispose()
    {
        IntPtr handle;
        GCHandle selfHandle;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            handle = _handle;
            _handle = IntPtr.Zero;
            selfHandle = _selfHandle;
            _selfHandle = default;
        }

        if (handle != IntPtr.Zero)
            NativeDestroy(handle);

        if (selfHandle.IsAllocated)
            selfHandle.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeHotkey(nuint userData, ulong tag)
    {
        try
        {
            GCHandle handle = GCHandle.FromIntPtr((IntPtr)userData);
            if (handle.Target is X11HotKeyGrabber grabber && !grabber._disposed)
            {
                Key key = (Key)(uint)(tag & 0xFFFF_FFFF);
                KeyModifiers modifiers = (KeyModifiers)(uint)(tag >> 32);
                grabber._onPressed(new Gesture(key, modifiers));
            }
        }
        catch
        {
        }
    }

    private static uint ToModifierMask(KeyModifiers modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(KeyModifiers.Shift))
            result |= ModShiftBit;
        if (modifiers.HasFlag(KeyModifiers.Control))
            result |= ModControlBit;
        if (modifiers.HasFlag(KeyModifiers.Alt))
            result |= ModAltBit;
        return result;
    }

    // X11 keysyms (X11/keysymdef.h) for the keys a hotkey can use.
    private static uint ToKeysym(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
            return (uint)('a' + (key - Key.A));
        if (key is >= Key.D0 and <= Key.D9)
            return (uint)('0' + (key - Key.D0));
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
            return (uint)(0xFFB0 + (key - Key.NumPad0));
        if (key is >= Key.F1 and <= Key.F24)
            return (uint)(0xFFBE + (key - Key.F1));

        // Missing on purpose: Shift, Ctrl and Alt alone (the hotkey dialog takes them as modifiers), and the IME,
        // DBE and other OEM-specific keys, which have no keysym of their own.
        return key switch
        {
            Key.Enter => 0xFF0D,
            Key.Tab => 0xFF09,
            Key.Escape => 0xFF1B,
            Key.Space => 0x20,
            Key.Back => 0xFF08,
            Key.Delete => 0xFFFF,
            Key.Insert => 0xFF63,
            Key.Home => 0xFF50,
            Key.End => 0xFF57,
            Key.PageUp => 0xFF55,
            Key.PageDown => 0xFF56,
            Key.Left => 0xFF51,
            Key.Up => 0xFF52,
            Key.Right => 0xFF53,
            Key.Down => 0xFF54,
            Key.Cancel => 0xFF69,
            Key.LineFeed => 0xFF0A,
            Key.Clear => 0xFF0B,
            Key.Pause => 0xFF13,
            Key.CapsLock => 0xFFE5,
            Key.Select => 0xFF60,
            Key.Print => 0xFF61,
            Key.Snapshot => 0xFF61,
            Key.Execute => 0xFF62,
            Key.Help => 0xFF6A,
            Key.LWin => 0xFFEB,
            Key.RWin => 0xFFEC,
            Key.Apps => 0xFF67,
            Key.NumLock => 0xFF7F,
            Key.Scroll => 0xFF14,
            Key.Multiply => 0xFFAA,
            Key.Add => 0xFFAB,
            Key.Separator => 0xFFAC,
            Key.Subtract => 0xFFAD,
            Key.Decimal => 0xFFAE,
            Key.Divide => 0xFFAF,

            // X11/XF86keysym.h
            Key.Sleep => 0x1008FF2F,
            Key.VolumeDown => 0x1008FF11,
            Key.VolumeMute => 0x1008FF12,
            Key.VolumeUp => 0x1008FF13,
            Key.MediaPlayPause => 0x1008FF14,
            Key.MediaStop => 0x1008FF15,
            Key.MediaPreviousTrack => 0x1008FF16,
            Key.MediaNextTrack => 0x1008FF17,
            Key.BrowserHome => 0x1008FF18,
            Key.LaunchMail => 0x1008FF19,
            Key.BrowserSearch => 0x1008FF1B,
            Key.BrowserBack => 0x1008FF26,
            Key.BrowserForward => 0x1008FF27,
            Key.BrowserStop => 0x1008FF28,
            Key.BrowserRefresh => 0x1008FF29,
            Key.BrowserFavorites => 0x1008FF30,
            Key.SelectMedia => 0x1008FF32,

            Key.OemTilde => 0x60,
            Key.OemMinus => 0x2D,
            Key.OemPlus => 0x3D,
            Key.OemOpenBrackets => 0x5B,
            Key.OemCloseBrackets => 0x5D,
            Key.OemSemicolon => 0x3B,
            Key.OemQuotes => 0x27,
            Key.OemComma => 0x2C,
            Key.OemPeriod => 0x2E,
            Key.OemQuestion => 0x2F,
            Key.OemPipe => 0x5C,
            Key.OemBackslash => 0x3C,
            _ => 0
        };
    }
}
