using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Skua.App.Avalonia.Flash;
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace Skua.App.Avalonia.Controls;

/// <summary>
/// Pure Wayland/Avalonia game viewport. Mugetsu renders offscreen into a double-buffered
/// shared-memory file (<c>--skua-shm</c>) and this control blits frames into a
/// <see cref="WriteableBitmap"/> while forwarding pointer/keyboard/resize input over the bridge.
/// </summary>
public class GameHost : Control
{
    private const int HeaderSize = 64;
    private static readonly byte[] MagicBytes = "SKUAFRM1"u8.ToArray();

    private static GameHost? _activeInstance;

    private FlashUtil? _flash;
    private readonly int _remoteChildPid;
    private readonly string _shmPath;
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _resizeDebounceTimer;
    private int _pendingResizeW;
    private int _pendingResizeH;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private unsafe byte* _shmPtr;
    private long _mappedSize;
    private WriteableBitmap? _bitmap;
    private ulong _lastSeq;
    private int _frameUpdateQueued;
    private int _lastSentWidth;
    private int _lastSentHeight;
    private bool? _lastSentVisible;

    public static event Action? GameClicked;

    public static string GetShmPathForPid(int pid) =>
        Path.Combine(Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(), $"skua-shm-{pid}.bin");

    public GameHost() : this(0)
    {
    }

    public GameHost(int remoteChildPid)
    {
        Focusable = true;
        ClipToBounds = true;
        _remoteChildPid = remoteChildPid;
        _shmPath = GetShmPathForPid(remoteChildPid > 0 ? remoteChildPid : Environment.ProcessId);
        if (remoteChildPid <= 0)
        {
            _activeInstance = this;
        }
        else
        {
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _pollTimer.Tick += (_, _) =>
            {
                if (IsVisible && TopLevel.GetTopLevel(this) is not { IsVisible: false })
                    UpdateFrameFromShm();
            };
        }
    }

    public string ShmPath => _shmPath;

    public void SetActiveInstance() => _activeInstance = this;

    public void DisposeResources()
    {
        _pollTimer?.Stop();
        _resizeDebounceTimer?.Stop();
        if (_activeInstance == this)
            _activeInstance = null;
        if (_flash != null)
            _flash.FrameReady -= OnFrameReady;
        UnmapShm();
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private unsafe void MapShm(long fileLen)
    {
        UnmapShm();
        _mmf = MemoryMappedFile.CreateFromFile(_shmPath, FileMode.Open, null, fileLen, MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(0, fileLen, MemoryMappedFileAccess.Read);
        byte* ptr = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _shmPtr = ptr + _accessor.PointerOffset;
        _mappedSize = fileLen;
    }

    private unsafe void UnmapShm()
    {
        if (_accessor != null)
        {
            if (_shmPtr != null)
            {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _shmPtr = null;
            }
            _accessor.Dispose();
            _accessor = null;
        }
        _mmf?.Dispose();
        _mmf = null;
        _mappedSize = 0;
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
        if (_flash != null)
            _flash.FrameReady -= OnFrameReady;

        _flash = flash;
        flash.ShmPath = _shmPath;
        flash.FrameReady += OnFrameReady;
        SendResizeIfNeeded();
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
        if (string.IsNullOrEmpty(text))
            return;
        _activeInstance?.DispatchInput(new { input = "text", text });
    }

    public static void ForwardFocus(bool focused)
    {
        _activeInstance?.DispatchInput(new { input = "focus", focused });
    }

    public static void ForwardClearKeys()
    {
        _activeInstance?.DispatchInput(new { input = "clear_keys" });
    }

    public void SetScaleMode(bool isExactFit = false)
    {
    }

    public void RefreshLayout()
    {
        SendResizeIfNeeded();
    }

    public void ForceRefreshFrame()
    {
        _resizeDebounceTimer?.Stop();
        _lastSentWidth = -1;
        _lastSentHeight = -1;
        _lastSeq = 0;
        SendResizeIfNeeded();
        UpdateFrameFromShm();
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && _remoteChildPid > 0)
        {
            if (IsVisible)
            {
                _pollTimer?.Start();
                ForceRefreshFrame();
            }
            else
            {
                _pollTimer?.Stop();
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_remoteChildPid <= 0 || IsVisible)
            _activeInstance = this;
        if (_remoteChildPid > 0 && IsVisible)
            _pollTimer?.Start();
        SendResizeIfNeeded();
        if (_remoteChildPid > 0 && IsVisible)
            UpdateFrameFromShm();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _pollTimer?.Stop();
        _resizeDebounceTimer?.Stop();
        if (_activeInstance == this)
            _activeInstance = null;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SendResizeIfNeeded();
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
        DispatchInput(new { input = "mouse_move", x = p.X * scale, y = p.Y * scale });

        PointerUpdateKind kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
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

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        if (_bitmap != null)
        {
            context.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height), bounds);
        }
        else
        {
            context.FillRectangle(Brushes.Black, bounds);
        }
    }

    private void SendResizeIfNeeded()
    {
        if ((_flash is null && _remoteChildPid <= 0) || !IsVisible)
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
        DispatchInput(new { input = "resize", w, h });
    }

    private void OnFrameReady()
    {
        if (Interlocked.Exchange(ref _frameUpdateQueued, 1) == 0)
        {
            Dispatcher.UIThread.Post(UpdateFrameFromShm, DispatcherPriority.Render);
        }
    }

    private unsafe void UpdateFrameFromShm()
    {
        Interlocked.Exchange(ref _frameUpdateQueued, 0);
        if (!IsVisible || TopLevel.GetTopLevel(this) is { IsVisible: false })
            return;

        try
        {
            if (_shmPtr == null)
            {
                if (!File.Exists(_shmPath))
                    return;
                long fileLen = new FileInfo(_shmPath).Length;
                if (fileLen < HeaderSize)
                    return;
                MapShm(fileLen);
            }

            byte* hdr = _shmPtr;
            if (hdr == null)
                return;

            ulong seq = Volatile.Read(ref *(ulong*)(hdr + 16));
            if (seq == _lastSeq || seq == 0)
                return;

            // Validate magic "SKUAFRM1"
            if (*(ulong*)hdr != 0x314D524641554B53UL)
                return;

            int width = *(int*)(hdr + 8);
            int height = *(int*)(hdr + 12);
            uint slot = *(uint*)(hdr + 24);
            uint slotStride = *(uint*)(hdr + 28);
            if (width <= 0 || height <= 0 || slot > 1)
                return;

            long pixelBytes = (long)width * height * 4;
            long offset = HeaderSize + (long)slot * slotStride;
            if (offset + pixelBytes > _mappedSize)
            {
                long fileLen = new FileInfo(_shmPath).Length;
                if (offset + pixelBytes > fileLen)
                    return;
                MapShm(fileLen);
                hdr = _shmPtr;
                if (hdr == null)
                    return;
            }

            if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(
                    new PixelSize(width, height),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Opaque);
            }

            using (ILockedFramebuffer fb = _bitmap.Lock())
            {
                byte* src = hdr + offset;
                byte* dst = (byte*)fb.Address;
                int srcStride = width * 4;
                if (fb.RowBytes == srcStride)
                {
                    Buffer.MemoryCopy(src, dst, pixelBytes, pixelBytes);
                }
                else
                {
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * fb.RowBytes, fb.RowBytes, srcStride);
                    }
                }
            }

            _lastSeq = seq;
            InvalidateVisual();
        }
        catch
        {
            // Ignore transient resize/unmap races.
        }
    }

    private static bool TryParseHeader(byte[] header, out int width, out int height, out ulong seq, out uint slot, out uint slotStride)
    {
        width = 0;
        height = 0;
        seq = 0;
        slot = 0;
        slotStride = 0;

        for (int i = 0; i < MagicBytes.Length; i++)
        {
            if (header[i] != MagicBytes[i])
                return false;
        }

        width = BitConverter.ToInt32(header, 8);
        height = BitConverter.ToInt32(header, 12);
        seq = BitConverter.ToUInt64(header, 16);
        slot = BitConverter.ToUInt32(header, 24);
        slotStride = BitConverter.ToUInt32(header, 28);
        return width > 0 && height > 0 && slot <= 1;
    }

    private static unsafe void CopyFrameToBitmap(MemoryMappedViewAccessor accessor, long offset, int width, int height, WriteableBitmap bitmap)
    {
        long pixelBytes = (long)width * height * 4;
        using ILockedFramebuffer fb = bitmap.Lock();
        byte* srcBase = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref srcBase);
        try
        {
            byte* src = srcBase + accessor.PointerOffset + offset;
            byte* dst = (byte*)fb.Address;
            int srcStride = width * 4;
            if (fb.RowBytes == srcStride)
            {
                Buffer.MemoryCopy(src, dst, pixelBytes, pixelBytes);
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * fb.RowBytes, fb.RowBytes, srcStride);
                }
            }
        }
        finally
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    public static byte[]? TryCapturePngFromShm(int pid)
    {
        string shmPath = GetShmPathForPid(pid);
        if (!File.Exists(shmPath))
            return null;

        long fileLen = new FileInfo(shmPath).Length;
        if (fileLen < HeaderSize)
            return null;

        using var mmf = MemoryMappedFile.CreateFromFile(shmPath, FileMode.Open, null, fileLen, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileLen, MemoryMappedFileAccess.Read);

        byte[] header = new byte[HeaderSize];
        accessor.ReadArray(0, header, 0, HeaderSize);
        if (!TryParseHeader(header, out int width, out int height, out _, out uint slot, out uint slotStride))
            return null;

        long pixelBytes = (long)width * height * 4;
        long offset = HeaderSize + (long)slot * slotStride;
        if (offset + pixelBytes > fileLen)
            return null;

        using var wb = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        CopyFrameToBitmap(accessor, offset, width, height, wb);

        using var ms = new MemoryStream();
        wb.Save(ms);
        return ms.ToArray();
    }
}
