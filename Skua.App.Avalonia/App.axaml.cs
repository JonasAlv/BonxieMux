using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Skua.Backend.Audio;
using Skua.Backend.Flash;
using Skua.App.Avalonia.AppStartup;
using Skua.App.Avalonia.Services;
using Skua.Shared.Avalonia.Services;
using Skua.Shared.Avalonia.Controls.Theming;
using Skua.Core.AppStartup;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Utils;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Skua.App.Avalonia;

public partial class App : Application
{
    private static int _exceptionDialogShown;
    private int _parentHostProcessId;
    private DispatcherTimer? _hostThemeSyncTimer;
    private AvaloniaThemeService? _pendingHostThemeSync;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public static System.IO.FileStream? HostLockStream;
    public static string? HostInstanceId { get; private set; }
    public static string HostPipeName { get; private set; } = System.IO.Path.Combine(ClientFileSources.TabsDIR, "host.pipe");

    private static bool TryAcquireSingleInstanceLock(string? safeInstanceId)
    {
        string lockName = string.IsNullOrEmpty(safeInstanceId) ? "host.lock" : $"host-{safeInstanceId}.lock";
        string lockPath = System.IO.Path.Combine(ClientFileSources.TabsDIR, lockName);
        try
        {
            HostLockStream?.Dispose();
            HostLockStream = new System.IO.FileStream(
                lockPath,
                System.IO.FileMode.OpenOrCreate,
                System.IO.FileAccess.ReadWrite,
                System.IO.FileShare.None);
            ClientFileSources.RestrictFilePermissions(lockPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar.SetDefaultIcon(Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar.AppIconUri);
        RegisterGlobalExceptionHandlers();

        JsonConvert.DefaultSettings = () => new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            Converters = { new Skua.Core.Models.Converters.WholeNumberConverter() },
            Error = (sender, args) =>
            {
                if (args.ErrorContext.Error.Message.Contains("No JSON content found") ||
                    args.ErrorContext.Error.Message.Contains("Error converting value {null}"))
                {
                    args.ErrorContext.Handled = true;
                    return;
                }
                System.Diagnostics.Trace.WriteLine($"[JSON Warning] {args.ErrorContext.Path}: {args.ErrorContext.Error.Message}");
                args.ErrorContext.Handled = true;
            }
        };

        ServiceCollection services = new();
        services.AddCommonServices();
        services.AddCompiler();
        services.AddScriptableObjects();
        services.AddSingleton<ISettingsService>(_ => new Skua.Shared.Avalonia.Services.SettingsService(Skua.Core.Models.AppRole.Client));
        services.AddSingleton<IDispatcherService, Skua.Shared.Avalonia.Services.DispatcherService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<IFileDialogService, Skua.Shared.Avalonia.Services.FileDialogService>();
        services.AddSingleton<IThemeService, AvaloniaThemeService>();
        services.AddSingleton(HotKeys.CreateHotKeys);
        services.AddSingleton<IHotKeyService, HotKeyService>();
        services.AddSingleton<ISoundService, SoundService>();
        services.AddSingleton<IFlashUtil, FlashUtil>();
        services.AddSingleton<IScreenshotService, ScreenshotService>();
        services.AddSingleton<SkuaStartupHandler>();
        Skua.App.Avalonia.AppStartup.Services.AddSkuaMainAppViewModels(services);

        Ioc.Default.ConfigureServices(services.BuildServiceProvider());

        Task.Run(() =>
        {
            try
            {
                Ioc.Default.GetRequiredService<IDiagnosticsService>().Start();
                ISettingsService settings = Ioc.Default.GetRequiredService<ISettingsService>();
                settings.SetApplicationVersion();
            }
            catch { }
        });

        ColorSpectrumFix.Initialize();

        IThemeService themeService = Ioc.Default.GetRequiredService<IThemeService>();
        ThemeResourceApplicator.AttachThemeService(this, themeService, ats =>
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow is Views.TabbedHostWindow hostWindow)
            {
                ThemeResourceApplicator.ApplyTitleBarTheme(hostWindow, ats.IsDarkTheme);
            }
            QueueHostThemeSync(ats);
        });

        string[] args = Environment.GetCommandLineArgs();
        bool hasEmbedFlag = false;
        long embedHwnd = 0;
        int hostPid = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--embed" && i + 1 < args.Length)
            {
                hasEmbedFlag = true;
                if (long.TryParse(args[i + 1], out embedHwnd))
                    Skua.Core.AppStartup.HotKeys.HostHwnd = new IntPtr(embedHwnd);
            }
            if (args[i] == "--host-pid" && i + 1 < args.Length)
            {
                int.TryParse(args[i + 1], out hostPid);
            }
        }

        for (int i = 1; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--host-instance", StringComparison.OrdinalIgnoreCase)) { HostInstanceId = args[i + 1]; break; }
        }
        if (!string.IsNullOrWhiteSpace(HostInstanceId))
        {
            string safeInstanceId = new string(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(HostInstanceId, char.IsLetterOrDigit)));
            if (!string.IsNullOrWhiteSpace(safeInstanceId))
            {
                if (safeInstanceId.Length > 24)
                    safeInstanceId = safeInstanceId[..24];
                HostInstanceId = safeInstanceId;
                HostPipeName = System.IO.Path.Combine(ClientFileSources.TabsDIR, $"host-{safeInstanceId}.pipe");
            }
            else HostInstanceId = null;
        }
        else
        {
            HostPipeName = System.IO.Path.Combine(ClientFileSources.TabsDIR, "host.pipe");
        }
        _parentHostProcessId = hasEmbedFlag ? hostPid : 0;
        if (_parentHostProcessId > 0)
            Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar.ArtworkPathChanged += OnTitleBarArtworkPathChanged;
        if (_parentHostProcessId > 0 && themeService is AvaloniaThemeService initialTheme)
            QueueHostThemeSync(initialTheme);

        Skua.Core.AppStartup.HotKeys.SendHostCommandAction = (cmd) =>
        {
            Task.Run(async () =>
            {
                try
                {
                    using var client = new System.IO.Pipes.NamedPipeClientStream(".", HostPipeName, System.IO.Pipes.PipeDirection.Out);
                    await client.ConnectAsync(500);
                    using var writer = new System.IO.StreamWriter(client) { AutoFlush = true };
                    await writer.WriteLineAsync(cmd);
                }
                catch { }
            });
        };

        if (!hasEmbedFlag)
        {
            Skua.Core.AppStartup.HotKeys.IsHostProcess = true;
            Task.Run(() =>
            {
                IClientFilesService clientFiles = Ioc.Default.GetRequiredService<IClientFilesService>();
                clientFiles.CreateDirectories();
                clientFiles.CreateFiles();
                Skua.Shared.Avalonia.Services.AppImageDesktopIntegration.EnsureIntegrated(
                    Skua.Shared.Avalonia.Services.AppIdentity.BonxieMux);
            });
        }
        else
        {
            try
            {
                var icons = TrayIcon.GetIcons(this);
                if (icons != null)
                {
                    foreach (var ic in icons)
                        ic.IsVisible = false;
                    icons.Clear();
                }
                TrayIcon.SetIcons(this, null);
            }
            catch { }
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            FlashUtil.UiPost = static action => Dispatcher.UIThread.Post(action);
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            Task.Run(() => FlashUtil.PreloadSwf());

            if (hasEmbedFlag)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                Views.MainWindow mainChild = new()
                {
                    WindowStartupLocation = global::Avalonia.Controls.WindowStartupLocation.Manual,
                    Title = "Skua-Child",
                    MinWidth = 0,
                    MinHeight = 0,
                    Width = 1,
                    Height = 1,
                    Opacity = 0,
                    Position = new global::Avalonia.PixelPoint(-32000, -32000),
                    WindowDecorations = global::Avalonia.Controls.WindowDecorations.None,
                    ShowInTaskbar = false,
                    ShowActivated = false
                };
                mainChild.Closed += (_, _) => desktop.Shutdown();
                desktop.MainWindow = mainChild;
                mainChild.HideTitleBar();

                mainChild.Opened += (s, ev) =>
                {
                    IntPtr childHwnd = mainChild.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                    if (childHwnd == IntPtr.Zero)
                        childHwnd = new IntPtr(Environment.ProcessId);

                    mainChild.Opacity = 1;

                    try
                    {
                        string dir = ClientFileSources.TabsDIR;
                        System.IO.Directory.CreateDirectory(dir);
                        string file = System.IO.Path.Combine(dir, $"ready_{Environment.ProcessId}.txt");
                        System.IO.File.WriteAllText(file, childHwnd.ToInt64().ToString());
                    }
                    catch { }

                    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (!Skua.Core.AppStartup.HotKeys.IsDetachedMode)
                            mainChild.Hide();
                    }, global::Avalonia.Threading.DispatcherPriority.Background);
                };

                if (hostPid != 0)
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            var parentProcess = System.Diagnostics.Process.GetProcessById(hostPid);
                            parentProcess.EnableRaisingEvents = true;
                            parentProcess.Exited += (_, _) =>
                            {
                                try { System.Diagnostics.Process.GetCurrentProcess().Kill(); } catch { }
                            };
                            while (!parentProcess.HasExited)
                            {
                                await Task.Delay(1000);
                            }
                        }
                        catch { }
                        try { System.Diagnostics.Process.GetCurrentProcess().Kill(); } catch { }
                    });
                }
            }
            else
            {
                string[] rawClientArgs = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Skip(args, 1));
                List<string> filteredArgs = new();
                string? envUser = Environment.GetEnvironmentVariable("BONXIEMUX_USERNAME");
                string? envPass = Environment.GetEnvironmentVariable("BONXIEMUX_PASSWORD");

                try
                {
                    Environment.SetEnvironmentVariable("BONXIEMUX_USERNAME", null);
                    Environment.SetEnvironmentVariable("BONXIEMUX_PASSWORD", null);
                }
                catch { }

                for (int i = 0; i < rawClientArgs.Length; i++)
                {
                    if (string.Equals(rawClientArgs[i], "--host-instance", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
                    if ((string.Equals(rawClientArgs[i], "-u", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(rawClientArgs[i], "--user", StringComparison.OrdinalIgnoreCase)) && i + 1 < rawClientArgs.Length)
                    {
                        string usr = rawClientArgs[i + 1];
                        i++;
                        if (!string.IsNullOrWhiteSpace(usr))
                            envUser = usr;
                        continue;
                    }
                    if ((string.Equals(rawClientArgs[i], "-p", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(rawClientArgs[i], "--password", StringComparison.OrdinalIgnoreCase)) && i + 1 < rawClientArgs.Length)
                    {
                        string psw = rawClientArgs[i + 1];
                        i++;
                        if (!string.IsNullOrWhiteSpace(psw))
                            envPass = psw;
                        continue;
                    }
                    filteredArgs.Add(rawClientArgs[i]);
                }

                if (!string.IsNullOrWhiteSpace(envUser))
                {
                    filteredArgs.Add("-u");
                    filteredArgs.Add(envUser);
                }
                if (!string.IsNullOrWhiteSpace(envPass))
                {
                    filteredArgs.Add("-p");
                    filteredArgs.Add(envPass);
                }

                string[] clientArgs = filteredArgs.ToArray();
                string payload = System.Text.Json.JsonSerializer.Serialize(clientArgs);
                bool createdNew = TryAcquireSingleInstanceLock(HostInstanceId);

                if (!createdNew)
                {
                    bool forwarded = false;
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        try
                        {
                            using System.IO.Pipes.NamedPipeClientStream pipeClient = new(".", HostPipeName, System.IO.Pipes.PipeDirection.Out, System.IO.Pipes.PipeOptions.None);
                            pipeClient.Connect(1000);
                            using System.IO.StreamWriter sw = new(pipeClient);
                            sw.WriteLine(payload);
                            sw.Flush();
                            forwarded = true;
                            break;
                        }
                        catch
                        {
                            System.Threading.Thread.Sleep(150);
                        }
                    }

                    if (forwarded)
                    {
                        Environment.Exit(0);
                        return;
                    }

                    try
                    {
                        TryAcquireSingleInstanceLock(HostInstanceId);
                    }
                    catch { }
                }

                SetupTrayIcon();
                Views.TabbedHostWindow masterWindow = new(clientArgs);
                desktop.MainWindow = masterWindow;
                ThemeResourceApplicator.ApplyTitleBarTheme(masterWindow, themeService.IsDarkTheme);
            }
            desktop.Exit += OnDesktopExit;
        }

        Task.Run(() => TryInitializeStartupServices());
        base.OnFrameworkInitializationCompleted();
    }

    private static void RegisterGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Exception ex = e.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception.");
            ShowUnhandledException("AppDomain.CurrentDomain.UnhandledException", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            System.Diagnostics.Trace.WriteLine($"TaskScheduler.UnobservedTaskException: {e.Exception}");
            e.SetObserved();
        };

    }

    private static void ShowUnhandledException(string source, Exception ex)
    {
        if (Interlocked.Exchange(ref _exceptionDialogShown, 1) == 1)
            return;

        try
        {
            string details = $"Unhandled exception source: {source}\r\n\r\n{ex.GetType().FullName}: {ex.Message}\r\n\r\n{ex.StackTrace}";
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "crash.txt"), details); } catch { }
            Console.Error.WriteLine(details);
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref _exceptionDialogShown, 0);
        }
    }

    private static async Task TryInitializeStartupServices()
    {
        try
        {
            MainMenu.RegisterManagedWindows(Ioc.Default.GetRequiredService<IWindowService>());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Startup window service init failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            Ioc.Default.GetRequiredService<IPluginManager>().Initialize();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Startup plugin init failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            Ioc.Default.GetRequiredService<IHotKeyService>().Reload();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Startup hotkey init failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            IGetScriptsService? getScripts = Ioc.Default.GetService<IGetScriptsService>();
            if (getScripts is null)
                return;

            await getScripts.GetScriptsAsync(null, default);

            if (!Skua.Core.AppStartup.HotKeys.IsHostProcess)
                return;

            ISettingsService settings = Ioc.Default.GetRequiredService<ISettingsService>();
            IDialogService dialogService = Ioc.Default.GetRequiredService<IDialogService>();
            if (settings.Get<bool>("CheckBotScriptsUpdates") && (getScripts.Missing > 0 || getScripts.Outdated > 0)
                && (settings.Get<bool>("AutoUpdateBotScripts")
                    || dialogService.ShowMessageBox("Would you like to update your scripts?", "Script Update", true) == true))
            {
                await Skua.Core.Services.ScriptStartupUpdater.DownloadMissingAndOutdatedAsync(getScripts, dialogService);
            }
        }
        catch { }
    }

    private static void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        DisposeTrayIcon();
        try
        {
            if (Ioc.Default.GetService<IThemeService>() is AvaloniaThemeService ats)
                ats.FlushPendingSave();
        }
        catch { }

        Task.Run(async () =>
        {
            try
            {
                var cleanupTask = Task.Run(async () =>
                {
                    try { await Ioc.Default.GetRequiredService<IDiagnosticsService>().DisposeAsync(); } catch { }
                    try { Ioc.Default.GetRequiredService<ICaptureProxy>().Stop(); } catch { }
                    try
                    {
                        await ((IAsyncDisposable)Ioc.Default.GetRequiredService<IScriptBoost>()).DisposeAsync();
                        await ((IAsyncDisposable)Ioc.Default.GetRequiredService<IScriptBotStats>()).DisposeAsync();
                        await ((IAsyncDisposable)Ioc.Default.GetRequiredService<IScriptDrop>()).DisposeAsync();
                        await Ioc.Default.GetRequiredService<IScriptManager>().StopScript();
                        await ((IScriptInterfaceManager)Ioc.Default.GetRequiredService<IScriptInterface>()).StopTimerAsync();
                    } catch { }
                    try { Ioc.Default.GetRequiredService<IFlashUtil>().Dispose(); } catch { }
                    try
                    {
                        if (Ioc.Default.GetRequiredService<IHotKeyService>() is IDisposable disposableHotKeys)
                            disposableHotKeys.Dispose();
                    } catch { }

                    try
                    {
                        WeakReferenceMessenger.Default.Cleanup();
                        WeakReferenceMessenger.Default.Reset();
                        StrongReferenceMessenger.Default.Reset();
                    } catch { }
                });

                await Task.WhenAny(cleanupTask, Task.Delay(1500));
            }
            catch { }
            finally
            {
                Environment.Exit(0);
            }
        });
    }

    private void OnTitleBarArtworkPathChanged(string? path)
    {
        if (_parentHostProcessId <= 0)
            return;

        string encodedPath = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(path ?? string.Empty));
        _ = Task.Run(() =>
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using System.IO.Pipes.NamedPipeClientStream pipe = new(".", HostPipeName, System.IO.Pipes.PipeDirection.Out);
                    pipe.Connect(500);
                    using System.IO.StreamWriter writer = new(pipe);
                    writer.WriteLine("ARTWORK|" + encodedPath);
                    writer.Flush();
                    return;
                }
                catch
                {
                    if (attempt < 4)
                        System.Threading.Thread.Sleep(100);
                }
            }
        });
    }

    private void QueueHostThemeSync(AvaloniaThemeService themeService)
    {
        if (_parentHostProcessId <= 0)
            return;

        _pendingHostThemeSync = themeService;
        if (_hostThemeSyncTimer is null)
        {
            DispatcherTimer syncTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
            syncTimer.Tick += (_, _) =>
            {
                syncTimer.Stop();
                AvaloniaThemeService? latestTheme = _pendingHostThemeSync;
                _pendingHostThemeSync = null;
                if (latestTheme is not null)
                    SendHostThemeSnapshot(latestTheme);
            };
            _hostThemeSyncTimer = syncTimer;
        }

        DispatcherTimer timer = _hostThemeSyncTimer!;
        timer.Stop();
        timer.Start();
    }

    private void SendHostThemeSnapshot(AvaloniaThemeService themeService)
    {
        try
        {
            string message = $"THEME|{themeService.IsDarkTheme}|{themeService.PrimaryHex}|{themeService.ForegroundHex}|{themeService.ActiveThemeName}|{themeService.IsComfortableDensity}";
            _ = Task.Run(() =>
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        using System.IO.Pipes.NamedPipeClientStream pipe = new(".", HostPipeName, System.IO.Pipes.PipeDirection.Out);
                        pipe.Connect(500);
                        using System.IO.StreamWriter writer = new(pipe);
                        writer.WriteLine(message);
                        writer.Flush();
                        return;
                    }
                    catch
                    {
                        if (attempt < 4)
                            System.Threading.Thread.Sleep(100);
                    }
                }
            });
        }
        catch
        {
        }
    }

    private void TrayIcon_Clicked(object? sender, EventArgs e)
    {
        ShowClientWindow();
    }

    private void TrayShowClient_Click(object? sender, EventArgs e)
    {
        ShowClientWindow();
    }

    private void TrayHideClient_Click(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow is null)
            return;
        desktop.MainWindow.WindowState = WindowState.Minimized;
    }

    private static TrayIcon? _trayIcon;

    public static void DisposeTrayIcon()
    {
        try
        {
            void Cleanup()
            {
                try
                {
                    if (_trayIcon != null)
                    {
                        _trayIcon.IsVisible = false;
                        if (_trayIcon is IDisposable d)
                            d.Dispose();
                        _trayIcon = null;
                    }

                    if (Current != null)
                    {
                        var icons = TrayIcon.GetIcons(Current);
                        if (icons != null)
                        {
                            foreach (var ic in icons)
                            {
                                try
                                {
                                    ic.IsVisible = false;
                                    if (ic is IDisposable d)
                                        d.Dispose();
                                }
                                catch { }
                            }
                            icons.Clear();
                        }
                        TrayIcon.SetIcons(Current, null);
                    }
                }
                catch { }
            }

            if (Dispatcher.UIThread.CheckAccess())
                Cleanup();
            else
                Dispatcher.UIThread.InvokeAsync(Cleanup).Wait(TimeSpan.FromMilliseconds(500));
        }
        catch { }
    }

    private void TrayExit_Click(object? sender, EventArgs e)
    {
        DisposeTrayIcon();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void ShowClientWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow is null)
            return;

        if (!desktop.MainWindow.IsVisible)
            desktop.MainWindow.Show();
        if (desktop.MainWindow.WindowState == WindowState.Minimized)
            desktop.MainWindow.WindowState = WindowState.Normal;
        desktop.MainWindow.Activate();
    }

    private void SetupTrayIcon()
    {
        try
        {
            var icon = Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar.DefaultWindowIcon;

            _trayIcon = new TrayIcon
            {
                Icon = icon,
                ToolTipText = "BonxieMux",
                IsVisible = true
            };
            _trayIcon.Clicked += TrayIcon_Clicked;

            var menu = new NativeMenu();

            var showItem = new NativeMenuItem("Show Client");
            showItem.Click += TrayShowClient_Click;
            menu.Add(showItem);

            var hideItem = new NativeMenuItem("Hide Client");
            hideItem.Click += TrayHideClient_Click;
            menu.Add(hideItem);

            menu.Add(new NativeMenuItemSeparator());

            var exitItem = new NativeMenuItem("Exit");
            exitItem.Click += TrayExit_Click;
            menu.Add(exitItem);

            _trayIcon.Menu = menu;

            var trayIcons = new TrayIcons { _trayIcon };
            TrayIcon.SetIcons(this, trayIcons);
        }
        catch { }
    }
}
