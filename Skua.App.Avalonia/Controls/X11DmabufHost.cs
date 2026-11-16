using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;

namespace Skua.App.Avalonia.Controls;

[StructLayout(LayoutKind.Sequential)]
internal struct BonxieDmaBufPlane
{
    public int Fd;
    public uint Offset;
    public uint Stride;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BonxieDmaBufFrame
{
    public ulong Seq;
    public uint Width;
    public uint Height;
    public uint Fourcc;
    public ulong Modifier;
    public uint NumPlanes;
    public BonxieDmaBufPlane Plane0;
    public BonxieDmaBufPlane Plane1;
    public BonxieDmaBufPlane Plane2;
    public BonxieDmaBufPlane Plane3;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BonxieOutputInfo
{
    public int X;
    public int Y;
    public uint Width;
    public uint Height;
    public uint RefreshMhz;
}

internal static unsafe partial class BonxieX11Native
{
    private const string Lib = "bonxie_x11";

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_start")]
    public static partial IntPtr Start(uint childPid, uint width, uint height);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_stop")]
    public static partial void Stop(IntPtr host);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_frame_seq")]
    public static partial ulong FrameSeq(IntPtr host);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_set_frame_callback")]
    public static partial void SetFrameCallback(IntPtr host, IntPtr cb, nuint userData);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_poll_dmabuf")]
    public static partial int PollDmabuf(IntPtr host, ulong lastSeq, out BonxieDmaBufFrame outDmabuf);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_resize")]
    public static partial void Resize(IntPtr host, uint width, uint height);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_query_outputs")]
    public static partial int QueryOutputs(BonxieOutputInfo* outBuf, nuint maxCount);

    [LibraryImport("libc", EntryPoint = "close")]
    public static partial int CloseFd(int fd);

    [LibraryImport("libEGL.so.1", EntryPoint = "eglGetCurrentDisplay")]
    public static partial IntPtr EglGetCurrentDisplay();

    [LibraryImport("libEGL.so.1", EntryPoint = "eglGetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr EglGetProcAddress(string name);
}

/// <summary>
/// Imports DRI3 DMA-BUF frames from <c>libbonxie_x11.so</c> into
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
    private const int GL_VERTEX_SHADER = 0x8B31;
    private const int GL_FRAGMENT_SHADER = 0x8B30;
    private const int GL_ARRAY_BUFFER = 0x8892;
    private const int GL_STATIC_DRAW = 0x88E4;
    private const int GL_FLOAT = 0x1406;
    private const int GL_TRIANGLE_STRIP = 0x0005;
    private const int GL_COLOR_BUFFER_BIT = 0x00004000;

    private const int EGL_WIDTH = 0x3057;
    private const int EGL_HEIGHT = 0x3056;
    private const int EGL_NONE = 0x3038;
    private const int EGL_LINUX_DMA_BUF_EXT = 0x3270;
    private const int EGL_LINUX_DRM_FOURCC_EXT = 0x3271;
    private const int EGL_DMA_BUF_PLANE0_FD_EXT = 0x3272;
    private const int EGL_DMA_BUF_PLANE0_OFFSET_EXT = 0x3273;
    private const int EGL_DMA_BUF_PLANE0_PITCH_EXT = 0x3274;
    private const int EGL_DMA_BUF_PLANE0_MODIFIER_LO_EXT = 0x3443;
    private const int EGL_DMA_BUF_PLANE0_MODIFIER_HI_EXT = 0x3444;
    private const int EGL_DMA_BUF_PLANE1_FD_EXT = 0x3275;
    private const int EGL_DMA_BUF_PLANE1_OFFSET_EXT = 0x3276;
    private const int EGL_DMA_BUF_PLANE1_PITCH_EXT = 0x3277;
    private const int EGL_DMA_BUF_PLANE1_MODIFIER_LO_EXT = 0x3445;
    private const int EGL_DMA_BUF_PLANE1_MODIFIER_HI_EXT = 0x3446;
    private const int EGL_DMA_BUF_PLANE2_FD_EXT = 0x3278;
    private const int EGL_DMA_BUF_PLANE2_OFFSET_EXT = 0x3279;
    private const int EGL_DMA_BUF_PLANE2_PITCH_EXT = 0x327A;
    private const int EGL_DMA_BUF_PLANE2_MODIFIER_LO_EXT = 0x3447;
    private const int EGL_DMA_BUF_PLANE2_MODIFIER_HI_EXT = 0x3448;
    private const int EGL_DMA_BUF_PLANE3_FD_EXT = 0x3440;
    private const int EGL_DMA_BUF_PLANE3_OFFSET_EXT = 0x3441;
    private const int EGL_DMA_BUF_PLANE3_PITCH_EXT = 0x3442;
    private const int EGL_DMA_BUF_PLANE3_MODIFIER_LO_EXT = 0x3449;
    private const int EGL_DMA_BUF_PLANE3_MODIFIER_HI_EXT = 0x344A;

    private delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, IntPtr, int*, IntPtr> _eglCreateImageKhr;
    private delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> _eglDestroyImageKhr;
    private delegate* unmanaged[Cdecl]<int, IntPtr, void> _glEglImageTargetTexture2DOes;
    private delegate* unmanaged[Cdecl]<int, float, float, void> _glUniform2f;
    private delegate* unmanaged[Cdecl]<int, void> _glDisable;

    private IntPtr _eglDisplay;
    private IntPtr _currentEglImage;
    private int _texture;
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

    public void Init(GlInterface gl)
    {
        if (_initialized)
            return;

        _eglDisplay = BonxieX11Native.EglGetCurrentDisplay();
        if (_eglDisplay == IntPtr.Zero)
            Console.Error.WriteLine("[X11Dmabuf] WARNING: eglGetCurrentDisplay() returned 0 (Avalonia may be using GLX instead of EGL)");
        _eglCreateImageKhr = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, IntPtr, int*, IntPtr>)
            BonxieX11Native.EglGetProcAddress("eglCreateImageKHR");
        _eglDestroyImageKhr = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)
            BonxieX11Native.EglGetProcAddress("eglDestroyImageKHR");
        _glEglImageTargetTexture2DOes = (delegate* unmanaged[Cdecl]<int, IntPtr, void>)
            BonxieX11Native.EglGetProcAddress("glEGLImageTargetTexture2DOES");

        IntPtr uniform2fPtr = gl.GetProcAddress("glUniform2f");
        if (uniform2fPtr != IntPtr.Zero)
            _glUniform2f = (delegate* unmanaged[Cdecl]<int, float, float, void>)uniform2fPtr;
        IntPtr disablePtr = gl.GetProcAddress("glDisable");
        if (disablePtr != IntPtr.Zero)
            _glDisable = (delegate* unmanaged[Cdecl]<int, void>)disablePtr;

        _texture = gl.GenTexture();
        gl.BindTexture(GL_TEXTURE_2D, _texture);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

        bool isGles = gl.ContextInfo.Version.Type == GlProfileType.OpenGLES;
        string vsSrc = isGles
            ? """
              #version 300 es
              precision mediump float;
              layout(location = 0) in vec2 a_pos;
              layout(location = 1) in vec2 a_uv;
              uniform vec2 u_uv_scale;
              out vec2 v_uv;
              void main() {
                  v_uv = a_uv * u_uv_scale;
                  gl_Position = vec4(a_pos, 0.0, 1.0);
              }
              """
            : """
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

        string fsSrc = isGles
            ? """
              #version 300 es
              precision mediump float;
              uniform sampler2D u_tex;
              in vec2 v_uv;
              out vec4 o_color;
              void main() {
                  o_color = vec4(texture(u_tex, v_uv).rgb, 1.0);
              }
              """
            : """
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

    public void Render(GlInterface gl, IntPtr x11Host, int viewportW, int viewportH)
    {
        if (!_initialized || x11Host == IntPtr.Zero)
            return;

        if (BonxieX11Native.FrameSeq(x11Host) != _lastSeq
            && BonxieX11Native.PollDmabuf(x11Host, _lastSeq, out BonxieDmaBufFrame dmabuf) == 1)
        {
            ImportDmabuf(gl, in dmabuf);
        }

        gl.Viewport(0, 0, Math.Max(1, viewportW), Math.Max(1, viewportH));
        if (_lastSeq == 0 || _fbWidth == 0 || _fbHeight == 0)
        {
            gl.ClearColor(0f, 0f, 0f, 1f);
            gl.Clear(GL_COLOR_BUFFER_BIT);
            return;
        }

        if (_glDisable != null)
        {
            _glDisable(0x0B71); // GL_DEPTH_TEST
            _glDisable(0x0B90); // GL_STENCIL_TEST
            _glDisable(0x0B44); // GL_CULL_FACE
            _glDisable(0x0C11); // GL_SCISSOR_TEST
            _glDisable(0x0BE2); // GL_BLEND
        }

        gl.UseProgram(_program);
        gl.ActiveTexture(0x84C0); // GL_TEXTURE0
        gl.BindTexture(GL_TEXTURE_2D, _texture);

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

    private void ImportDmabuf(GlInterface gl, in BonxieDmaBufFrame dmabuf)
    {
        try
        {
            if (_eglDisplay == IntPtr.Zero || _eglCreateImageKhr == null || _glEglImageTargetTexture2DOes == null)
                return;

            int* attribs = stackalloc int[48];
            int idx = 0;
            attribs[idx++] = EGL_WIDTH;
            attribs[idx++] = (int)dmabuf.Width;
            attribs[idx++] = EGL_HEIGHT;
            attribs[idx++] = (int)dmabuf.Height;
            attribs[idx++] = EGL_LINUX_DRM_FOURCC_EXT;
            attribs[idx++] = (int)dmabuf.Fourcc;

            bool hasMod = dmabuf.Modifier != 0x00ffffffffffffffUL;
            int modLo = unchecked((int)(dmabuf.Modifier & 0xFFFFFFFFUL));
            int modHi = unchecked((int)(dmabuf.Modifier >> 32));
            uint planes = Math.Min(dmabuf.NumPlanes, 4u);

            if (planes > 0)
            {
                attribs[idx++] = EGL_DMA_BUF_PLANE0_FD_EXT;
                attribs[idx++] = dmabuf.Plane0.Fd;
                attribs[idx++] = EGL_DMA_BUF_PLANE0_OFFSET_EXT;
                attribs[idx++] = (int)dmabuf.Plane0.Offset;
                attribs[idx++] = EGL_DMA_BUF_PLANE0_PITCH_EXT;
                attribs[idx++] = (int)dmabuf.Plane0.Stride;
                if (hasMod)
                {
                    attribs[idx++] = EGL_DMA_BUF_PLANE0_MODIFIER_LO_EXT;
                    attribs[idx++] = modLo;
                    attribs[idx++] = EGL_DMA_BUF_PLANE0_MODIFIER_HI_EXT;
                    attribs[idx++] = modHi;
                }
            }
            if (planes > 1)
            {
                attribs[idx++] = EGL_DMA_BUF_PLANE1_FD_EXT;
                attribs[idx++] = dmabuf.Plane1.Fd;
                attribs[idx++] = EGL_DMA_BUF_PLANE1_OFFSET_EXT;
                attribs[idx++] = (int)dmabuf.Plane1.Offset;
                attribs[idx++] = EGL_DMA_BUF_PLANE1_PITCH_EXT;
                attribs[idx++] = (int)dmabuf.Plane1.Stride;
                if (hasMod)
                {
                    attribs[idx++] = EGL_DMA_BUF_PLANE1_MODIFIER_LO_EXT;
                    attribs[idx++] = modLo;
                    attribs[idx++] = EGL_DMA_BUF_PLANE1_MODIFIER_HI_EXT;
                    attribs[idx++] = modHi;
                }
            }
            if (planes > 2)
            {
                attribs[idx++] = EGL_DMA_BUF_PLANE2_FD_EXT;
                attribs[idx++] = dmabuf.Plane2.Fd;
                attribs[idx++] = EGL_DMA_BUF_PLANE2_OFFSET_EXT;
                attribs[idx++] = (int)dmabuf.Plane2.Offset;
                attribs[idx++] = EGL_DMA_BUF_PLANE2_PITCH_EXT;
                attribs[idx++] = (int)dmabuf.Plane2.Stride;
                if (hasMod)
                {
                    attribs[idx++] = EGL_DMA_BUF_PLANE2_MODIFIER_LO_EXT;
                    attribs[idx++] = modLo;
                    attribs[idx++] = EGL_DMA_BUF_PLANE2_MODIFIER_HI_EXT;
                    attribs[idx++] = modHi;
                }
            }
            if (planes > 3)
            {
                attribs[idx++] = EGL_DMA_BUF_PLANE3_FD_EXT;
                attribs[idx++] = dmabuf.Plane3.Fd;
                attribs[idx++] = EGL_DMA_BUF_PLANE3_OFFSET_EXT;
                attribs[idx++] = (int)dmabuf.Plane3.Offset;
                attribs[idx++] = EGL_DMA_BUF_PLANE3_PITCH_EXT;
                attribs[idx++] = (int)dmabuf.Plane3.Stride;
                if (hasMod)
                {
                    attribs[idx++] = EGL_DMA_BUF_PLANE3_MODIFIER_LO_EXT;
                    attribs[idx++] = modLo;
                    attribs[idx++] = EGL_DMA_BUF_PLANE3_MODIFIER_HI_EXT;
                    attribs[idx++] = modHi;
                }
            }
            attribs[idx] = EGL_NONE;

            IntPtr img = _eglCreateImageKhr(_eglDisplay, IntPtr.Zero, EGL_LINUX_DMA_BUF_EXT, IntPtr.Zero, attribs);
            if (img == IntPtr.Zero)
            {
                Console.Error.WriteLine($"[X11Dmabuf] eglCreateImageKHR failed for {dmabuf.Width}x{dmabuf.Height} fourcc=0x{dmabuf.Fourcc:X8} mod=0x{dmabuf.Modifier:X16} planes={dmabuf.NumPlanes}");
                return;
            }

            gl.BindTexture(GL_TEXTURE_2D, _texture);
            _glEglImageTargetTexture2DOes(GL_TEXTURE_2D, img);
            int glErr = gl.GetError();
            if (glErr != 0 && _lastSeq == 0)
                Console.Error.WriteLine($"[X11Dmabuf] glEGLImageTargetTexture2DOES GL error=0x{glErr:X}");
            gl.BindTexture(GL_TEXTURE_2D, 0);

            if (_currentEglImage != IntPtr.Zero && _eglDestroyImageKhr != null)
                _eglDestroyImageKhr(_eglDisplay, _currentEglImage);

            _currentEglImage = img;
            _fbWidth = dmabuf.Width;
            _fbHeight = dmabuf.Height;
            _lastSeq = dmabuf.Seq;
        }
        finally
        {
            if (dmabuf.NumPlanes > 0 && dmabuf.Plane0.Fd >= 0) BonxieX11Native.CloseFd(dmabuf.Plane0.Fd);
            if (dmabuf.NumPlanes > 1 && dmabuf.Plane1.Fd >= 0) BonxieX11Native.CloseFd(dmabuf.Plane1.Fd);
            if (dmabuf.NumPlanes > 2 && dmabuf.Plane2.Fd >= 0) BonxieX11Native.CloseFd(dmabuf.Plane2.Fd);
            if (dmabuf.NumPlanes > 3 && dmabuf.Plane3.Fd >= 0) BonxieX11Native.CloseFd(dmabuf.Plane3.Fd);
        }
    }

    public void Deinit(GlInterface gl)
    {
        if (!_initialized)
            return;

        if (_currentEglImage != IntPtr.Zero && _eglDisplay != IntPtr.Zero && _eglDestroyImageKhr != null)
        {
            _eglDestroyImageKhr(_eglDisplay, _currentEglImage);
            _currentEglImage = IntPtr.Zero;
        }
        if (_texture != 0) gl.DeleteTexture(_texture);
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

internal sealed class X11DmabufViewport : OpenGlControlBase
{
    private readonly IntPtr _x11Host;
    private readonly DmabufGlRenderer _renderer = new();
    private readonly Action<ulong>? _onPresentedFrame;
    private readonly Action<TimeSpan> _animCallback;
    private TopLevel? _topLevel;
    private bool _attached;
    private bool _animLoopRunning;

    public X11DmabufViewport(IntPtr x11Host, Action<ulong>? onPresentedFrame = null)
    {
        _x11Host = x11Host;
        _onPresentedFrame = onPresentedFrame;
        _animCallback = OnAnimationFrame;
        IsHitTestVisible = false;
    }

    public bool HasPendingFrame() =>
        _x11Host != IntPtr.Zero && BonxieX11Native.FrameSeq(_x11Host) != _renderer.LastSeq;

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

        if (HasPendingFrame())
            RequestNextFrameRendering();

        _topLevel.RequestAnimationFrame(_animCallback);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _topLevel = TopLevel.GetTopLevel(this);
        EnsureAnimationLoop();
        if (IsVisible)
            RequestNextFrameRendering();
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

    protected override void OnOpenGlDeinit(GlInterface gl) => _renderer.Deinit(gl);

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_x11Host == IntPtr.Zero)
            return;

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int vw = Math.Max(1, (int)Math.Round(Bounds.Width * scale));
        int vh = Math.Max(1, (int)Math.Round(Bounds.Height * scale));
        _renderer.Render(gl, _x11Host, vw, vh);
        _onPresentedFrame?.Invoke(_renderer.LastSeq);
    }
}
