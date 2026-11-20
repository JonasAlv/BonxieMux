using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Skua.App.Avalonia.Flash;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Skua.App.Avalonia.Controls;

internal static partial class BonxieX11Native
{
    private const string Lib = "bonxie_x11";

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_start")]
    public static partial IntPtr Start(uint childPid, uint parentXid, uint width, uint height);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_stop")]
    public static partial void Stop(IntPtr host);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_suspend")]
    public static partial void Suspend(IntPtr host);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_resize")]
    public static partial void Resize(IntPtr host, uint width, uint height);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_set_click_callback")]
    public static partial void SetClickCallback(IntPtr host, IntPtr cb, nuint userData);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_focus")]
    public static partial void FocusWindow(uint xid);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_state")]
    public static partial int GetState(IntPtr host);
}

/// <summary>
/// X11 NativeControlHost on Linux. Creates a child X11 window positioned strictly inside
/// <see cref="GameHost"/>'s layout bounds and reparents Mugetsu's X11 window directly into it
/// via <c>libbonxie_x11.so</c> (supports both standalone and tabbed remote-client viewports).
/// </summary>
public class GameHost : NativeControlHost
{
    private static GameHost? _activeInstance;
    private static readonly ConcurrentDictionary<int, int> _pendingRemotePlayerPids = new();

    private FlashUtil? _flash;
    private readonly int _remoteChildPid;
    private int _remotePlayerPid;
    private IntPtr _x11Host;
    private uint _childXid;
    private int _attachedPid;
    private Window? _ownerWindow;
    private GCHandle _selfHandle;
    private DispatcherTimer? _resizeDebounceTimer;
    private DispatcherTimer? _attachMonitorTimer;
    private int _attachRetryCount;
    private int _pendingResizeW;
    private int _pendingResizeH;
    private int _lastSentWidth;
    private int _lastSentHeight;
    private bool _forceNextResize;

    public static event Action? GameClicked;
    public event Action? Clicked;

    public nint WindowHandle => (nint)_childXid;

    public GameHost() : this(0)
    {
    }

    public GameHost(int remoteChildPid)
    {
        Focusable = true;
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

    public void SetActiveInstance() => _activeInstance = this;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        IPlatformHandle handle = base.CreateNativeControlCore(parent);
        if (OperatingSystem.IsLinux())
        {
            _childXid = (uint)handle.Handle;
            EnsureX11HostStarted();
            SendResizeIfNeeded();
        }
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        StopX11Host();
        _childXid = 0;
        base.DestroyNativeControlCore(control);
    }

    public void AttachFlash(FlashUtil flash)
    {
        _flash = flash;
        EnsureX11HostStarted();
        UpdateHostedVisibility();
    }

    public void AttachRemotePlayerPid(int mugetsuPid)
    {
        if (mugetsuPid <= 0)
            return;
        _remotePlayerPid = mugetsuPid;
        EnsureX11HostStarted();
        UpdateHostedVisibility();
    }

    private int TargetPid => _remoteChildPid > 0 ? _remotePlayerPid : (_flash?.PlayerPid ?? 0);

    private void EnsureX11HostStarted()
    {
        if (!OperatingSystem.IsLinux())
            return;

        int pid = TargetPid;
        if (_childXid == 0 || pid <= 0)
            return;

        if (pid == _attachedPid && _x11Host != IntPtr.Zero)
        {
            try
            {
                if (BonxieX11Native.GetState(_x11Host) >= 0)
                    return;
            }
            catch
            {
                return;
            }
        }

        StopX11Host();

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int bw = (int)Math.Round(Bounds.Width * scale);
        int bh = (int)Math.Round(Bounds.Height * scale);
        if (bw <= 2 && _ownerWindow != null)
        {
            bw = (int)Math.Round(_ownerWindow.ClientSize.Width * scale);
            bh = (int)Math.Round(Math.Max(240, (_ownerWindow.ClientSize.Height - 64) * scale));
        }
        uint w = (uint)Math.Max(320, bw > 2 ? bw : _lastSentWidth);
        uint h = (uint)Math.Max(240, bh > 2 ? bh : _lastSentHeight);

        bool startThrew = false;
        try
        {
            _x11Host = BonxieX11Native.Start((uint)pid, _childXid, w, h);
        }
        catch (Exception ex)
        {
            startThrew = true;
            System.Diagnostics.Trace.WriteLine($"Skua.X11Host failed to start: {ex}");
            _x11Host = IntPtr.Zero;
            ShowEmbedErrorDialog();
        }

        if (_x11Host != IntPtr.Zero)
        {
            _attachedPid = pid;
            _lastSentWidth = -1;
            _lastSentHeight = -1;
            if (!_selfHandle.IsAllocated)
                _selfHandle = GCHandle.Alloc(this);
            unsafe
            {
                delegate* unmanaged[Cdecl]<nuint, void> cb = &OnX11Clicked;
                BonxieX11Native.SetClickCallback(_x11Host, (IntPtr)cb, (nuint)(IntPtr)_selfHandle);
            }
            StartAttachMonitor();
        }
        else if (!startThrew)
        {
            System.Diagnostics.Trace.WriteLine($"Skua.X11Host failed to start for pid {pid}: BonxieX11Native.Start returned null.");
            if (_attachRetryCount < 3)
            {
                _attachRetryCount++;
                StartAttachMonitor();
            }
            else
            {
                _attachRetryCount = 0;
                ShowEmbedErrorDialog();
            }
        }
    }

    private static void ShowEmbedErrorDialog()
    {
        CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Skua.Core.Interfaces.IDialogService>()?
            .ShowMessageBox("Failed to embed the Mugetsu game window into BonxieMux.", "Game Host Error");
    }

    private void StartAttachMonitor()
    {
        _attachMonitorTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _attachMonitorTimer.Tick -= OnAttachMonitorTick;
        _attachMonitorTimer.Tick += OnAttachMonitorTick;
        _attachMonitorTimer.Start();
    }

    private void OnAttachMonitorTick(object? sender, EventArgs e)
    {
        if (_x11Host == IntPtr.Zero)
        {
            _attachMonitorTimer?.Stop();
            EnsureX11HostStarted();
            return;
        }
        int state;
        try { state = BonxieX11Native.GetState(_x11Host); }
        catch { _attachMonitorTimer?.Stop(); return; }

        if (state == 1)
        {
            _attachRetryCount = 0;
            _attachMonitorTimer?.Stop();
        }
        else if (state < 0)
        {
            _attachMonitorTimer?.Stop();
            if (_attachRetryCount < 3)
            {
                _attachRetryCount++;
                EnsureX11HostStarted();
            }
            else
            {
                _attachRetryCount = 0;
                ShowEmbedErrorDialog();
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnX11Clicked(nuint userData)
    {
        if (userData == 0)
            return;
        if (GCHandle.FromIntPtr((IntPtr)userData).Target is GameHost host)
        {
            Dispatcher.UIThread.Post(() =>
            {
                host.Focus();
                host.Clicked?.Invoke();
                GameClicked?.Invoke();
            });
        }
    }

    private void StopX11Host(bool keepMapped = false)
    {
        _attachMonitorTimer?.Stop();
        if (_x11Host != IntPtr.Zero)
        {
            IntPtr h = _x11Host;
            _x11Host = IntPtr.Zero;
            try { BonxieX11Native.SetClickCallback(h, IntPtr.Zero, 0); } catch { }
            try
            {
                if (keepMapped)
                    BonxieX11Native.Suspend(h);
                else
                    BonxieX11Native.Stop(h);
            }
            catch { }
        }
        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
        _attachedPid = 0;
    }

    public void SuspendCapture() => StopX11Host(keepMapped: true);

    public void DisposeResources()
    {
        _resizeDebounceTimer?.Stop();
        if (_activeInstance == this)
            _activeInstance = null;
        _flash = null;
        StopX11Host();
    }

    public void RefreshLayout() => SendResizeIfNeeded();

    public void ForceRefreshFrame() => SendResizeIfNeeded();

    public void SetScaleMode(bool isExactFit = false)
    {
    }

    public void SetHostedVisible(bool visible) => UpdateHostedVisibility();

    public void FocusHostWindow()
    {
        if (!OperatingSystem.IsLinux())
            return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle is nint h && h != 0)
                BonxieX11Native.FocusWindow((uint)h);
        }
        catch { }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
            UpdateHostedVisibility();
    }

    private void OnOwnerWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            _forceNextResize = true;
            if (IsHostEffectivelyVisible)
                ScheduleResizeDebounce();
            else
                _resizeDebounceTimer?.Stop();
        }
        else if (e.Property == IsVisibleProperty)
        {
            UpdateHostedVisibility();
        }
    }

    private bool IsHostEffectivelyVisible =>
        IsVisible && (_ownerWindow is null || (_ownerWindow.IsVisible && _ownerWindow.WindowState != WindowState.Minimized));

    private void UpdateHostedVisibility()
    {
        if (IsHostEffectivelyVisible)
            SendResizeIfNeeded();
        else
            _resizeDebounceTimer?.Stop();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        DetachOwnerWindow();
        _ownerWindow = TopLevel.GetTopLevel(this) as Window;
        if (_ownerWindow != null)
            _ownerWindow.PropertyChanged += OnOwnerWindowPropertyChanged;
        EnsureX11HostStarted();
        UpdateHostedVisibility();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        DetachOwnerWindow();
        _resizeDebounceTimer?.Stop();
        StopX11Host();
    }

    private void DetachOwnerWindow()
    {
        if (_ownerWindow != null)
        {
            _ownerWindow.PropertyChanged -= OnOwnerWindowPropertyChanged;
            _ownerWindow = null;
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SendResizeIfNeeded();
    }

    private void SendResizeIfNeeded()
    {
        if (!OperatingSystem.IsLinux() || TargetPid <= 0 || !IsHostEffectivelyVisible)
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
            return;
        }

        _pendingResizeW = w;
        _pendingResizeH = h;
        ScheduleResizeDebounce();
    }

    private void ScheduleResizeDebounce()
    {
        _resizeDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _resizeDebounceTimer.Tick -= OnResizeDebounceTick;
        _resizeDebounceTimer.Tick += OnResizeDebounceTick;
        _resizeDebounceTimer.Stop();
        _resizeDebounceTimer.Start();
    }

    private void OnResizeDebounceTick(object? sender, EventArgs e)
    {
        _resizeDebounceTimer?.Stop();
        if (!OperatingSystem.IsLinux() || TargetPid <= 0 || !IsHostEffectivelyVisible)
            return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int bw = (int)Math.Round(Bounds.Width * scale);
        int bh = (int)Math.Round(Bounds.Height * scale);
        int w = bw > 2 ? bw : _pendingResizeW;
        int h = bh > 2 ? bh : _pendingResizeH;
        bool force = _forceNextResize;
        _forceNextResize = false;
        if (w <= 2 || h <= 2 || (!force && w == _lastSentWidth && h == _lastSentHeight))
            return;
        _lastSentWidth = w;
        _lastSentHeight = h;
        if (_x11Host != IntPtr.Zero)
            BonxieX11Native.Resize(_x11Host, (uint)w, (uint)h);
    }
}
