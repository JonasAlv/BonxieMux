using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace Skua.App.Avalonia.Controls;

[StructLayout(LayoutKind.Sequential)]
internal struct BonxieOutputInfo
{
    public int X;
    public int Y;
    public int Width;
    public int Height;
    public uint RefreshMhz;
}

internal static unsafe partial class BonxieWaylandNative
{
    private const string Lib = "bonxie_wayland";

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_start")]
    public static partial IntPtr Start(uint width, uint height, byte enableDmabuf, byte* socketBuf, nuint socketBufLen);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_stop")]
    public static partial void Stop(IntPtr host);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_frame_seq")]
    public static partial ulong FrameSeq(IntPtr host);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_set_frame_callback")]
    public static partial void SetFrameCallback(IntPtr host, delegate* unmanaged[Cdecl]<nuint, void> cb, nuint userData);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_import_dmabuf_texture")]
    public static partial int ImportDmabufTexture(
        IntPtr host,
        ulong lastSeq,
        out ulong outSeq,
        out ulong outBufferId,
        out uint outWidth,
        out uint outHeight,
        out uint outGlTex,
        out IntPtr outEglDisplay,
        out IntPtr outEglImage);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_rebind_dmabuf_texture")]
    public static partial int RebindDmabufTexture(uint glTex, IntPtr eglImage);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_destroy_dmabuf_texture")]
    public static partial void DestroyDmabufTexture(IntPtr eglDisplay, IntPtr eglImage, uint glTex);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_peek_frame")]
    public static partial int PeekFrame(IntPtr host, out ulong outSeq, out ulong outBufferId, out uint outWidth, out uint outHeight);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_ack_frame")]
    public static partial void AckFrame(IntPtr host, ulong seq);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_notify_presented")]
    public static partial void NotifyPresented(IntPtr host);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_notify_presented_seq")]
    public static partial void NotifyPresentedSeq(IntPtr host, ulong seq);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_set_refresh_mhz")]
    public static partial void SetRefreshMhz(IntPtr host, uint mhz);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_set_uncapped")]
    public static partial void SetUncapped(IntPtr host, byte uncapped);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_set_interpolation")]
    public static partial void SetInterpolation(IntPtr host, byte enabled);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_set_visible")]
    public static partial void SetVisible(IntPtr host, byte visible);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_resize")]
    public static partial void Resize(IntPtr host, uint width, uint height);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_pointer_motion")]
    public static partial void PointerMotion(IntPtr host, double x, double y);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_pointer_button")]
    public static partial void PointerButton(IntPtr host, uint button, byte pressed);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_pointer_scroll")]
    public static partial void PointerScroll(IntPtr host, double dx, double dy);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_key")]
    public static partial void Key(IntPtr host, uint evdevCode, byte pressed);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_query_outputs")]
    public static partial int QueryOutputs(BonxieOutputInfo* outBuf, nuint maxCount);

    public static (IntPtr Handle, string SocketName) StartHost(uint width, uint height, bool enableDmabuf = true)
    {
        byte* buf = stackalloc byte[256];
        IntPtr handle = Start(width, height, enableDmabuf ? (byte)1 : (byte)0, buf, 256);
        if (handle == IntPtr.Zero)
            return (IntPtr.Zero, string.Empty);

        int len = 0;
        while (len < 255 && buf[len] != 0)
            len++;
        string socketName = Encoding.UTF8.GetString(buf, len);
        return (handle, socketName);
    }
}

internal sealed class DmabufSkiaRenderer
{
    private const uint GL_TEXTURE_2D = 0x0DE1u;
    private const uint GL_RGBA8 = 0x8058u;
    private const int MaxCachedBuffers = 4;
    private static readonly SKSamplingOptions LinearSampling = new(SKFilterMode.Linear);
    private static readonly SKSamplingOptions NearestSampling = new(SKFilterMode.Nearest);
    private static readonly ConcurrentQueue<DmabufSkiaRenderer> s_retiredRenderers = new();
    private static readonly ConcurrentQueue<DmabufSkiaRenderer> s_purgeQueue = new();

    private sealed class CachedDmabufImage
    {
        public readonly uint GlTex;
        public readonly IntPtr EglDisplay;
        public readonly IntPtr EglImage;
        public readonly GRBackendTexture BackendTexture;
        public readonly SKImage Image;
        public readonly uint Width;
        public readonly uint Height;

        public CachedDmabufImage(
            uint glTex,
            IntPtr eglDisplay,
            IntPtr eglImage,
            GRBackendTexture backendTexture,
            SKImage image,
            uint width,
            uint height)
        {
            GlTex = glTex;
            EglDisplay = eglDisplay;
            EglImage = eglImage;
            BackendTexture = backendTexture;
            Image = image;
            Width = width;
            Height = height;
        }
    }

    private readonly object _hostLock = new();
    private IntPtr _waylandHost;
    private readonly Action<ulong> _onPresentedFrame;
    private readonly Action _scheduleWake;
    private readonly Dictionary<ulong, CachedDmabufImage> _bufferCache = new();
    private readonly Queue<ulong> _cacheOrder = new();
    private readonly SKPaint _opaquePaint = new() { BlendMode = SKBlendMode.Src, IsAntialias = false };
    private readonly SKPaint _blackPaint = new() { Color = SKColors.Black, Style = SKPaintStyle.Fill, BlendMode = SKBlendMode.Src };

    private CachedDmabufImage? _activeEntry;
    private CachedDmabufImage? _fallbackEntry;
    private IntPtr _lastGrContextHandle;
    private long _lastSeq;
    private ulong _lastNotifiedPresentedSeq;
    private bool _disposed;
    private volatile bool _isHidden;
    private int _purgeQueued;

    public bool StretchToFill { get; set; }
    public bool ForceScaleToBounds { get; set; }

    public void SetHidden(bool hidden)
    {
        _isHidden = hidden;
        if (hidden && Interlocked.Exchange(ref _purgeQueued, 1) == 0)
            s_purgeQueue.Enqueue(this);
    }

    public DmabufSkiaRenderer(
        IntPtr waylandHost,
        Action<ulong> onPresentedFrame,
        Action scheduleWake)
    {
        _waylandHost = waylandHost;
        _onPresentedFrame = onPresentedFrame;
        _scheduleWake = scheduleWake;
    }

    public ulong LastSeq => (ulong)Volatile.Read(ref _lastSeq);

    public bool HasPendingFrame()
    {
        lock (_hostLock)
        {
            if (_disposed || _waylandHost == IntPtr.Zero)
                return false;

            ulong seq = BonxieWaylandNative.FrameSeq(_waylandHost);
            return seq != 0 && seq != LastSeq;
        }
    }

    public void DetachHostAndRetire()
    {
        lock (_hostLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _waylandHost = IntPtr.Zero;
        }
        s_retiredRenderers.Enqueue(this);
    }

    public void Render(ImmediateDrawingContext drawingContext, Size size)
    {
        if (size.Width <= 0 || size.Height <= 0)
            return;

        if (drawingContext.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature skiaFeature)
            return;

        using ISkiaSharpApiLease lease = skiaFeature.Lease();
        SKCanvas canvas = lease.SkCanvas;
        GRContext? grContext = lease.GrContext;
        SKRect dstRect = new(0f, 0f, (float)size.Width, (float)size.Height);
        if (grContext == null)
        {
            canvas.DrawRect(dstRect, _blackPaint);
            return;
        }

        while (s_retiredRenderers.TryDequeue(out DmabufSkiaRenderer? retired))
            retired.CleanupOnRenderThread(grContext.Handle);
        while (s_purgeQueue.TryDequeue(out DmabufSkiaRenderer? toPurge))
            toPurge.PurgeIfHiddenOnRenderThread(grContext.Handle);

        bool hasMore = false;
        lock (_hostLock)
        {
            if (_disposed || _waylandHost == IntPtr.Zero)
                return;

            if (_lastGrContextHandle != IntPtr.Zero && _lastGrContextHandle != grContext.Handle)
                ClearCache(sameGrContext: false);
            _lastGrContextHandle = grContext.Handle;

            ulong lastSeq = LastSeq;
            ulong acquiredSeq = 0;
            if (BonxieWaylandNative.FrameSeq(_waylandHost) != lastSeq)
            {
                if (BonxieWaylandNative.PeekFrame(_waylandHost, out ulong seq, out ulong bufferId, out uint fw, out uint fh) == 1
                    && seq != lastSeq)
                {
                    if (bufferId != 0
                        && _bufferCache.TryGetValue(bufferId, out CachedDmabufImage? cached)
                        && cached.Width == fw
                        && cached.Height == fh)
                    {
                        BonxieWaylandNative.RebindDmabufTexture(cached.GlTex, cached.EglImage);
                        grContext.ResetContext(GRGlBackendState.TextureBinding);
                        _activeEntry = cached;
                        Volatile.Write(ref _lastSeq, (long)seq);
                        acquiredSeq = seq;
                    }
                    else if (TryImportLatestDmabuf(grContext, _waylandHost, lastSeq, out ulong importedSeq))
                    {
                        acquiredSeq = importedSeq;
                    }
                }
            }

            bool deferredAck = false;
            if (_activeEntry == null)
            {
                canvas.DrawRect(dstRect, _blackPaint);
            }
            else
            {
                SKMatrix m = canvas.TotalMatrix;
                float scaleX = MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
                float scaleY = MathF.Sqrt(m.ScaleY * m.ScaleY + m.SkewX * m.SkewX);
                if (scaleX < 0.01f) scaleX = 1f;
                if (scaleY < 0.01f) scaleY = 1f;
                SKRect srcRect;
                SKSamplingOptions sampling;
                if (StretchToFill || ForceScaleToBounds)
                {
                    srcRect = new(0f, 0f, _activeEntry.Width, _activeEntry.Height);
                    sampling = LinearSampling;
                }
                else
                {
                    float physW = (float)size.Width * scaleX;
                    float physH = (float)size.Height * scaleY;
                    float srcW = Math.Clamp(physW, 1f, _activeEntry.Width);
                    float srcH = Math.Clamp(physH, 1f, _activeEntry.Height);
                    srcRect = new(0f, 0f, srcW, srcH);
                    sampling = (MathF.Abs(srcW - physW) < 0.5f && MathF.Abs(srcH - physH) < 0.5f &&
                        MathF.Abs(srcW - MathF.Round(srcW)) < 0.01f && MathF.Abs(srcH - MathF.Round(srcH)) < 0.01f)
                        ? NearestSampling
                        : LinearSampling;
                }
                canvas.DrawImage(_activeEntry.Image, srcRect, dstRect, sampling, _opaquePaint);

                if (acquiredSeq != 0)
                {
                    if (Dispatcher.UIThread.CheckAccess())
                    {
                        deferredAck = true;
                    }
                    else
                    {
                        grContext.Flush();
                    }
                }
            }

            ulong presentedSeq = LastSeq;
            if (acquiredSeq != 0)
            {
                if (deferredAck)
                {
                    ulong seqToAck = acquiredSeq;
                    Dispatcher.UIThread.Post(() =>
                    {
                        lock (_hostLock)
                        {
                            if (!_disposed && _waylandHost != IntPtr.Zero)
                                BonxieWaylandNative.AckFrame(_waylandHost, seqToAck);
                        }
                    }, DispatcherPriority.Render);
                }
                else
                {
                    BonxieWaylandNative.AckFrame(_waylandHost, acquiredSeq);
                }
            }
            if (presentedSeq != _lastNotifiedPresentedSeq || (presentedSeq == 0 && acquiredSeq != 0))
            {
                _lastNotifiedPresentedSeq = presentedSeq;
                _onPresentedFrame(presentedSeq);
            }

            if (acquiredSeq != 0)
            {
                ulong latestSeq = BonxieWaylandNative.FrameSeq(_waylandHost);
                hasMore = latestSeq != 0 && latestSeq != presentedSeq;
            }
        }

        if (hasMore)
            _scheduleWake();
    }

    private bool TryImportLatestDmabuf(GRContext grContext, IntPtr waylandHost, ulong lastSeq, out ulong outSeq)
    {
        outSeq = 0;
        int rc = BonxieWaylandNative.ImportDmabufTexture(
            waylandHost,
            lastSeq,
            out ulong seq,
            out ulong bufferId,
            out uint fw,
            out uint fh,
            out uint glTex,
            out IntPtr eglDisplay,
            out IntPtr eglImage);

        if (rc != 1 || glTex == 0 || eglImage == IntPtr.Zero)
            return false;

        grContext.ResetContext(GRGlBackendState.TextureBinding);

        GRGlTextureInfo texInfo = new(GL_TEXTURE_2D, glTex, GL_RGBA8);
        GRBackendTexture backendTex = new((int)fw, (int)fh, false, texInfo);
        SKImage? skImage = SKImage.FromTexture(grContext, backendTex, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888, SKAlphaType.Opaque);
        if (skImage == null)
        {
            backendTex.Dispose();
            BonxieWaylandNative.DestroyDmabufTexture(eglDisplay, eglImage, glTex);
            return false;
        }

        CachedDmabufImage entry = new(glTex, eglDisplay, eglImage, backendTex, skImage, fw, fh);
        if (bufferId != 0)
        {
            if (_bufferCache.Remove(bufferId, out CachedDmabufImage? replaced))
            {
                if (ReferenceEquals(_activeEntry, replaced))
                    _activeEntry = null;
                DestroyCachedImage(replaced, sameGrContext: true);
            }

            while (_bufferCache.Count >= MaxCachedBuffers && _cacheOrder.Count > 0)
            {
                ulong oldestId = _cacheOrder.Dequeue();
                if (_bufferCache.Remove(oldestId, out CachedDmabufImage? evicted))
                {
                    if (ReferenceEquals(_activeEntry, evicted))
                        _activeEntry = null;
                    DestroyCachedImage(evicted, sameGrContext: true);
                }
            }

            _bufferCache[bufferId] = entry;
            _cacheOrder.Enqueue(bufferId);
        }
        else
        {
            if (_fallbackEntry != null)
            {
                if (ReferenceEquals(_activeEntry, _fallbackEntry))
                    _activeEntry = null;
                DestroyCachedImage(_fallbackEntry, sameGrContext: true);
            }
            _fallbackEntry = entry;
        }

        _activeEntry = entry;
        Volatile.Write(ref _lastSeq, (long)seq);
        outSeq = seq;
        return true;
    }

    private void ClearCache(bool sameGrContext)
    {
        foreach (CachedDmabufImage cached in _bufferCache.Values)
            DestroyCachedImage(cached, sameGrContext);
        _bufferCache.Clear();
        _cacheOrder.Clear();
        if (_fallbackEntry != null)
        {
            DestroyCachedImage(_fallbackEntry, sameGrContext);
            _fallbackEntry = null;
        }
        _activeEntry = null;
        Volatile.Write(ref _lastSeq, 0);
    }

    private void PurgeIfHiddenOnRenderThread(IntPtr currentGrContextHandle)
    {
        Interlocked.Exchange(ref _purgeQueued, 0);
        lock (_hostLock)
        {
            if (!_isHidden || _disposed)
                return;
            bool sameContext = _lastGrContextHandle == IntPtr.Zero || _lastGrContextHandle == currentGrContextHandle;
            ClearCache(sameContext);
        }
    }

    private void CleanupOnRenderThread(IntPtr currentGrContextHandle)
    {
        lock (_hostLock)
        {
            bool sameContext = _lastGrContextHandle == IntPtr.Zero || _lastGrContextHandle == currentGrContextHandle;
            ClearCache(sameContext);
            _opaquePaint.Dispose();
            _blackPaint.Dispose();
        }
    }

    private static void DestroyCachedImage(CachedDmabufImage cached, bool sameGrContext)
    {
        cached.Image.Dispose();
        cached.BackendTexture.Dispose();
        BonxieWaylandNative.DestroyDmabufTexture(cached.EglDisplay, cached.EglImage, sameGrContext ? cached.GlTex : 0);
    }
}

internal sealed class DmabufDrawOperation : ICustomDrawOperation
{
    private readonly DmabufSkiaRenderer _renderer;

    public DmabufDrawOperation(Rect bounds, DmabufSkiaRenderer renderer)
    {
        Bounds = bounds;
        _renderer = renderer;
    }

    public Rect Bounds { get; }

    public bool Equals(ICustomDrawOperation? other) => false;

    public bool HitTest(Point p) => false;

    public void Dispose()
    {
    }

    public void Render(ImmediateDrawingContext context) => _renderer.Render(context, Bounds.Size);
}

internal sealed unsafe class WaylandDmabufViewport : Control
{
    private readonly IntPtr _waylandHost;
    private readonly DmabufSkiaRenderer _renderer;
    private readonly Action _invalidateAction;
    private readonly Action _scheduleWakeAction;
    private readonly object _cbLock = new();
    private TopLevel? _topLevel;
    private GCHandle _selfHandle;
    private volatile bool _attached;
    private volatile bool _isVisibleCached;
    private volatile bool _topLevelVisibleCached;
    private int _wakeQueued;

    public WaylandDmabufViewport(
        IntPtr waylandHost,
        Action _onBeforeRender,
        Action<ulong> onPresentedFrame)
    {
        _waylandHost = waylandHost;
        _invalidateAction = DoInvalidate;
        _scheduleWakeAction = ScheduleWake;
        _renderer = new DmabufSkiaRenderer(waylandHost, onPresentedFrame, _scheduleWakeAction);
        IsHitTestVisible = false;
    }

    public bool StretchToFill
    {
        get => _renderer.StretchToFill;
        set
        {
            if (_renderer.StretchToFill != value)
            {
                _renderer.StretchToFill = value;
                InvalidateVisual();
            }
        }
    }

    public bool ForceScaleToBounds
    {
        get => _renderer.ForceScaleToBounds;
        set
        {
            if (_renderer.ForceScaleToBounds != value)
            {
                _renderer.ForceScaleToBounds = value;
                InvalidateVisual();
            }
        }
    }

    public bool HasPendingFrame() => _renderer.HasPendingFrame();

    public void DisposeRenderer()
    {
        DetachCallback();
        _renderer.DetachHostAndRetire();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeFrameReady(nuint userData)
    {
        try
        {
            if (userData == 0)
                return;

            GCHandle handle = GCHandle.FromIntPtr((IntPtr)userData);
            if (handle.Target is WaylandDmabufViewport vp)
                vp.ScheduleWake();
        }
        catch
        {
        }
    }

    private void ScheduleWake()
    {
        if (_attached &&
            _isVisibleCached &&
            _topLevelVisibleCached &&
            Interlocked.Exchange(ref _wakeQueued, 1) == 0)
        {
            Dispatcher.UIThread.Post(_invalidateAction, DispatcherPriority.Render);
        }
    }

    private void DoInvalidate()
    {
        Interlocked.Exchange(ref _wakeQueued, 0);
        UpdateVisibilityCache();
        if (!_attached || !_isVisibleCached || !_topLevelVisibleCached)
            return;

        InvalidateVisual();
    }

    public void RequestNextFrameRendering()
    {
        UpdateVisibilityCache();
        if (_attached && _isVisibleCached && _topLevelVisibleCached)
        {
            Interlocked.Exchange(ref _wakeQueued, 0);
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        if (!_attached || _waylandHost == IntPtr.Zero)
            return;

        Size size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return;

        context.Custom(new DmabufDrawOperation(new Rect(size), _renderer));
    }

    private void AttachCallback()
    {
        lock (_cbLock)
        {
            if (_waylandHost == IntPtr.Zero || _selfHandle.IsAllocated)
                return;

            _selfHandle = GCHandle.Alloc(this);
            BonxieWaylandNative.SetFrameCallback(_waylandHost, &OnNativeFrameReady, (nuint)GCHandle.ToIntPtr(_selfHandle));
        }
    }

    public void DetachCallback()
    {
        lock (_cbLock)
        {
            if (_waylandHost != IntPtr.Zero)
                BonxieWaylandNative.SetFrameCallback(_waylandHost, null, 0);

            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
                _selfHandle = default;
            }
        }
    }

    private void AttachTopLevelHandlers(TopLevel? tl)
    {
        if (ReferenceEquals(_topLevel, tl))
        {
            UpdateVisibilityCache();
            return;
        }

        DetachTopLevelHandlers();
        _topLevel = tl;
        if (tl is Window w)
            w.Opened += OnTopLevelOpened;
        if (tl != null)
            tl.PropertyChanged += OnTopLevelPropertyChanged;
        UpdateVisibilityCache();
    }

    private void DetachTopLevelHandlers()
    {
        if (_topLevel is Window w)
            w.Opened -= OnTopLevelOpened;
        if (_topLevel != null)
            _topLevel.PropertyChanged -= OnTopLevelPropertyChanged;
        _topLevel = null;
        _topLevelVisibleCached = false;
    }

    private void OnTopLevelOpened(object? sender, EventArgs e)
    {
        RequestRenderIfNeeded();
    }

    private void OnTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.IsVisibleProperty || e.Property == Window.WindowStateProperty)
            RequestRenderIfNeeded();
    }

    private void UpdateVisibilityCache()
    {
        _isVisibleCached = IsVisible && this.IsEffectivelyVisible;
        _topLevelVisibleCached = _topLevel is { IsVisible: true } &&
            (_topLevel is not Window w || w.WindowState != WindowState.Minimized);
        _renderer.SetHidden(!(_attached && _isVisibleCached && _topLevelVisibleCached));
    }

    private void RequestRenderIfNeeded()
    {
        UpdateVisibilityCache();
        if (_attached && _isVisibleCached && _topLevelVisibleCached &&
            (_renderer.LastSeq == 0 || HasPendingFrame()))
        {
            RequestNextFrameRendering();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        AttachTopLevelHandlers(TopLevel.GetTopLevel(this));
        AttachCallback();
        RequestRenderIfNeeded();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _renderer.SetHidden(true);
        Interlocked.Exchange(ref _wakeQueued, 0);
        DetachCallback();
        DetachTopLevelHandlers();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            RequestNextFrameRendering();
        }
        else if (change.Property == IsVisibleProperty)
        {
            UpdateVisibilityCache();
            if (IsVisible)
                RequestRenderIfNeeded();
        }
    }
}
