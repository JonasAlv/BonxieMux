using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;

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
    public static partial void SetFrameCallback(IntPtr host, IntPtr cb, nuint userData);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_import_dmabuf_to_bound_texture")]
    public static partial int ImportDmabufToBoundTexture(
        IntPtr host,
        ulong lastSeq,
        out ulong outSeq,
        out ulong outBufferId,
        out uint outWidth,
        out uint outHeight,
        out IntPtr outEglDisplay,
        out IntPtr outEglImage);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_destroy_egl_image")]
    public static partial void DestroyEglImage(IntPtr eglDisplay, IntPtr eglImage);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_peek_frame")]
    public static partial int PeekFrame(IntPtr host, out ulong outSeq, out ulong outBufferId, out uint outWidth, out uint outHeight);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_ack_frame")]
    public static partial void AckFrame(IntPtr host, ulong seq);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_notify_presented")]
    public static partial void NotifyPresented(IntPtr host);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_set_refresh_mhz")]
    public static partial void SetRefreshMhz(IntPtr host, uint mhz);

    [LibraryImport(Lib, EntryPoint = "bonxie_wl_set_uncapped")]
    public static partial void SetUncapped(IntPtr host, byte uncapped);

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

/// <summary>
/// Imports DMA-BUF frames from <c>libbonxie_wayland.so</c> into
/// Avalonia's OpenGL/EGL context and blits them as a fullscreen textured quad.
/// </summary>
internal sealed unsafe class DmabufGlRenderer
{
    private const int GL_TEXTURE_2D = 0x0DE1;
    private const int GL_TEXTURE_MIN_FILTER = 0x2801;
    private const int GL_TEXTURE_MAG_FILTER = 0x2800;
    private const int GL_TEXTURE_WRAP_S = 0x2802;
    private const int GL_TEXTURE_WRAP_T = 0x2803;
    private const int GL_LINEAR = 0x2601;
    private const int GL_CLAMP_TO_EDGE = 0x812F;
    private const int GL_RGBA = 0x1908;
    private const int GL_UNSIGNED_BYTE = 0x1401;
    private const int GL_VERTEX_SHADER = 0x8B31;
    private const int GL_FRAGMENT_SHADER = 0x8B30;
    private const int GL_ARRAY_BUFFER = 0x8892;
    private const int GL_STATIC_DRAW = 0x88E4;
    private const int GL_FLOAT = 0x1406;
    private const int GL_TRIANGLE_STRIP = 0x0005;
    private const int GL_COLOR_BUFFER_BIT = 0x00004000;

    private delegate* unmanaged[Cdecl]<int, float, float, void> _glUniform2f;

    private readonly struct CachedDmabufTexture
    {
        public readonly int Texture;
        public readonly IntPtr EglDisplay;
        public readonly IntPtr EglImage;
        public readonly uint Width;
        public readonly uint Height;

        public CachedDmabufTexture(int texture, IntPtr eglDisplay, IntPtr eglImage, uint width, uint height)
        {
            Texture = texture;
            EglDisplay = eglDisplay;
            EglImage = eglImage;
            Width = width;
            Height = height;
        }
    }

    private const int MaxCachedBuffers = 8;
    private readonly Dictionary<ulong, CachedDmabufTexture> _bufferCache = new();
    private readonly Queue<ulong> _cacheOrder = new();

    private int _activeTexture;
    private int _texture;
    private IntPtr _fallbackEglDisplay;
    private IntPtr _fallbackEglImage;
    private int _program;
    private int _vao;
    private int _vbo;
    private int _uTexLoc = -1;
    private int _uUvScaleLoc = -1;
    private ulong _lastSeq;
    public ulong LastSeq => _lastSeq;
    private uint _fbWidth;
    private uint _fbHeight;
    private bool _initialized;

    public uint FrameWidth => _fbWidth;
    public uint FrameHeight => _fbHeight;

    public void Init(GlInterface gl)
    {
        if (_initialized)
            return;

        IntPtr uniform2fPtr = gl.GetProcAddress("glUniform2f");
        if (uniform2fPtr != IntPtr.Zero)
            _glUniform2f = (delegate* unmanaged[Cdecl]<int, float, float, void>)uniform2fPtr;

        const string vsSrc = """
            #version 330 core
            layout(location = 0) in vec2 a_pos;
            layout(location = 1) in vec2 a_uv;
            uniform vec2 u_uv_scale;
            out vec2 v_uv;
            void main() {
                v_uv = a_uv * u_uv_scale;
                gl_Position = vec4(a_pos, 0.0, 1.0);
            }
            """;

        const string fsSrc = """
            #version 330 core
            uniform sampler2D u_tex;
            in vec2 v_uv;
            out vec4 o_color;
            void main() {
                o_color = vec4(texture(u_tex, v_uv).rgb, 1.0);
            }
            """;

        int vs = CompileShader(gl, GL_VERTEX_SHADER, vsSrc);
        int fs = CompileShader(gl, GL_FRAGMENT_SHADER, fsSrc);
        _program = gl.CreateProgram();
        gl.AttachShader(_program, vs);
        gl.AttachShader(_program, fs);
        gl.LinkProgram(_program);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);

        _uTexLoc = gl.GetUniformLocationString(_program, "u_tex");
        _uUvScaleLoc = gl.GetUniformLocationString(_program, "u_uv_scale");
        if (_uTexLoc >= 0)
        {
            gl.UseProgram(_program);
            gl.Uniform1i(_uTexLoc, 0);
            gl.UseProgram(0);
        }

        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);

        float[] quad =
        {
            -1f, -1f, 0f, 1f,
             1f, -1f, 1f, 1f,
            -1f,  1f, 0f, 0f,
             1f,  1f, 1f, 0f,
        };
        fixed (float* qPtr = quad)
        {
            gl.BufferData(GL_ARRAY_BUFFER, (IntPtr)(quad.Length * sizeof(float)), (IntPtr)qPtr, GL_STATIC_DRAW);
        }

        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, GL_FLOAT, 0, 4 * sizeof(float), IntPtr.Zero);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, GL_FLOAT, 0, 4 * sizeof(float), (IntPtr)(2 * sizeof(float)));

        gl.BindVertexArray(0);
        gl.BindBuffer(GL_ARRAY_BUFFER, 0);
        gl.BindTexture(GL_TEXTURE_2D, 0);
        _initialized = true;
    }

    public void Render(GlInterface gl, IntPtr waylandHost, int viewportW, int viewportH)
    {
        if (!_initialized || waylandHost == IntPtr.Zero)
            return;

        if (BonxieWaylandNative.FrameSeq(waylandHost) != _lastSeq)
        {
            if (BonxieWaylandNative.PeekFrame(waylandHost, out ulong seq, out ulong bufferId, out uint fw, out uint fh) == 1
                && seq != _lastSeq)
            {
                if (bufferId != 0
                    && _bufferCache.TryGetValue(bufferId, out CachedDmabufTexture cached)
                    && cached.Width == fw
                    && cached.Height == fh)
                {
                    _activeTexture = cached.Texture;
                    _fbWidth = cached.Width;
                    _fbHeight = cached.Height;
                    _lastSeq = seq;
                    BonxieWaylandNative.AckFrame(waylandHost, seq);
                }
                else
                {
                    ImportLatestDmabuf(gl, waylandHost);
                }
            }
        }

        gl.Viewport(0, 0, Math.Max(1, viewportW), Math.Max(1, viewportH));
        if (_lastSeq == 0 || _fbWidth == 0 || _fbHeight == 0 || _activeTexture == 0)
        {
            gl.ClearColor(0f, 0f, 0f, 1f);
            gl.Clear(GL_COLOR_BUFFER_BIT);
            return;
        }

        gl.Disable(0x0BE2); // GL_BLEND
        gl.Disable(0x0B71); // GL_DEPTH_TEST
        gl.UseProgram(_program);
        gl.ActiveTexture(0x84C0); // GL_TEXTURE0
        gl.BindTexture(GL_TEXTURE_2D, _activeTexture);

        if (_uUvScaleLoc >= 0 && _glUniform2f != null)
        {
            float sx = Math.Clamp(viewportW / (float)_fbWidth, 0.01f, 1.0f);
            float sy = Math.Clamp(viewportH / (float)_fbHeight, 0.01f, 1.0f);
            _glUniform2f(_uUvScaleLoc, sx, sy);
        }

        gl.BindVertexArray(_vao);
        gl.DrawArrays(GL_TRIANGLE_STRIP, 0, 4);
        gl.BindVertexArray(0);
        gl.BindTexture(GL_TEXTURE_2D, 0);
        gl.UseProgram(0);
    }

    private void ImportLatestDmabuf(GlInterface gl, IntPtr waylandHost)
    {
        int tex = gl.GenTexture();
        gl.BindTexture(GL_TEXTURE_2D, tex);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

        int rc = BonxieWaylandNative.ImportDmabufToBoundTexture(
            waylandHost,
            _lastSeq,
            out ulong seq,
            out ulong bufferId,
            out uint fw,
            out uint fh,
            out IntPtr eglDisplay,
            out IntPtr eglImage);

        gl.BindTexture(GL_TEXTURE_2D, 0);

        if (rc != 1 || eglImage == IntPtr.Zero)
        {
            gl.DeleteTexture(tex);
            return;
        }

        if (bufferId != 0)
        {
            if (_bufferCache.Remove(bufferId, out CachedDmabufTexture replaced))
                DestroyCachedTexture(gl, replaced);

            while (_bufferCache.Count >= MaxCachedBuffers && _cacheOrder.Count > 0)
            {
                ulong oldestId = _cacheOrder.Dequeue();
                if (_bufferCache.Remove(oldestId, out CachedDmabufTexture evicted))
                    DestroyCachedTexture(gl, evicted);
            }

            _bufferCache[bufferId] = new CachedDmabufTexture(tex, eglDisplay, eglImage, fw, fh);
            _cacheOrder.Enqueue(bufferId);
        }
        else
        {
            if (_texture != 0)
                gl.DeleteTexture(_texture);
            if (_fallbackEglImage != IntPtr.Zero)
                BonxieWaylandNative.DestroyEglImage(_fallbackEglDisplay, _fallbackEglImage);
            _texture = tex;
            _fallbackEglDisplay = eglDisplay;
            _fallbackEglImage = eglImage;
        }

        _activeTexture = tex;
        _fbWidth = fw;
        _fbHeight = fh;
        _lastSeq = seq;
    }

    private static void DestroyCachedTexture(GlInterface gl, CachedDmabufTexture cached)
    {
        if (cached.Texture != 0)
            gl.DeleteTexture(cached.Texture);
        if (cached.EglImage != IntPtr.Zero)
            BonxieWaylandNative.DestroyEglImage(cached.EglDisplay, cached.EglImage);
    }

    public void Deinit(GlInterface gl)
    {
        if (!_initialized)
            return;

        foreach (CachedDmabufTexture cached in _bufferCache.Values)
            DestroyCachedTexture(gl, cached);
        _bufferCache.Clear();
        _cacheOrder.Clear();
        _activeTexture = 0;

        if (_texture != 0) gl.DeleteTexture(_texture);
        if (_fallbackEglImage != IntPtr.Zero)
        {
            BonxieWaylandNative.DestroyEglImage(_fallbackEglDisplay, _fallbackEglImage);
            _fallbackEglImage = IntPtr.Zero;
            _fallbackEglDisplay = IntPtr.Zero;
        }
        if (_vbo != 0) gl.DeleteBuffer(_vbo);
        if (_vao != 0) gl.DeleteVertexArray(_vao);
        if (_program != 0) gl.DeleteProgram(_program);
        _texture = _vbo = _vao = _program = 0;
        _initialized = false;
    }

    private static int CompileShader(GlInterface gl, int type, string src)
    {
        int shader = gl.CreateShader(type);
        gl.ShaderSourceString(shader, src);
        gl.CompileShader(shader);
        return shader;
    }
}

internal sealed class WaylandDmabufViewport : OpenGlControlBase
{
    private readonly IntPtr _waylandHost;
    private readonly DmabufGlRenderer _renderer = new();
    private readonly Action _onBeforeRender;
    private readonly Action<ulong> _onPresentedFrame;
    private readonly Action<TimeSpan> _animCallback;
    private TopLevel? _topLevel;
    private bool _attached;
    private bool _animLoopRunning;
    private ulong _lastRecordedSeq;

    public WaylandDmabufViewport(
        IntPtr waylandHost,
        Action onBeforeRender,
        Action<ulong> onPresentedFrame)
    {
        _waylandHost = waylandHost;
        _onBeforeRender = onBeforeRender;
        _onPresentedFrame = onPresentedFrame;
        _animCallback = OnAnimationFrame;
        IsHitTestVisible = false;
    }

    public bool HasPendingFrame()
    {
        if (_waylandHost == IntPtr.Zero)
            return false;

        ulong seq = BonxieWaylandNative.FrameSeq(_waylandHost);
        return seq != 0 && seq != _lastRecordedSeq;
    }

    private void EnsureAnimationLoop()
    {
        if (_animLoopRunning || !_attached || !IsVisible)
            return;

        _topLevel ??= TopLevel.GetTopLevel(this);
        if (_topLevel is not { IsVisible: true })
            return;

        _animLoopRunning = true;
        _topLevel.RequestAnimationFrame(_animCallback);
    }

    private void OnAnimationFrame(TimeSpan _)
    {
        if (!_attached || !IsVisible || _topLevel is not { IsVisible: true })
        {
            _animLoopRunning = false;
            return;
        }

        if (_lastRecordedSeq == 0 || HasPendingFrame())
            RequestNextFrameRendering();
        _topLevel.RequestAnimationFrame(_animCallback);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _topLevel = TopLevel.GetTopLevel(this);
        EnsureAnimationLoop();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _animLoopRunning = false;
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible)
            EnsureAnimationLoop();
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        _renderer.Init(gl);
        if (IsVisible)
            RequestNextFrameRendering();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _renderer.Deinit(gl);
        base.OnOpenGlDeinit(gl);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_waylandHost == IntPtr.Zero)
            return;

        _onBeforeRender();
        double scale = _topLevel?.RenderScaling ?? TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)Math.Round(Bounds.Width * scale));
        int h = Math.Max(1, (int)Math.Round(Bounds.Height * scale));
        _renderer.Render(gl, _waylandHost, w, h);
        ulong seq = _renderer.LastSeq;
        if (seq != 0 && seq != _lastRecordedSeq)
        {
            _lastRecordedSeq = seq;
        }
        _onPresentedFrame(seq);
    }
}
