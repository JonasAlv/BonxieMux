using Avalonia;
using Avalonia.Skia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Shared.Avalonia.Services;
using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Skua.Manager.Avalonia;

internal static class Program
{
    private static FileStream? _singleInstanceLockStream;

    static Program()
    {
        AvaloniaAppBootstrap.InitializeAssemblyResolvers();
    }

    [STAThread]
    public static void Main(string[] args)
    {
        AvaloniaAppBootstrap.IgnoreSighup();
        ClientFileSources.EnsureInitialized();

        string socketPath = Path.Combine(ClientFileSources.TabsDIR, "manager.sock");
        string lockPath = Path.Combine(ClientFileSources.TabsDIR, "manager.lock");

        try
        {
            using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(socketPath));
            client.Send(Encoding.UTF8.GetBytes("SHOW\n"));
            client.Close();
            return;
        }
        catch
        {
            try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { }
        }

        try
        {
            _singleInstanceLockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            ClientFileSources.RestrictFilePermissions(lockPath);
        }
        catch
        {
            return;
        }

        try
        {
            Socket listenerSocket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listenerSocket.Bind(new UnixDomainSocketEndPoint(socketPath));
            ClientFileSources.RestrictFilePermissions(socketPath);
            listenerSocket.Listen(5);

            Task.Run(async () =>
            {
                byte[] buffer = new byte[64];
                while (true)
                {
                    try
                    {
                        using Socket handler = await listenerSocket.AcceptAsync();
                        int read = await handler.ReceiveAsync(buffer, SocketFlags.None);
                        if (read > 0)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                StrongReferenceMessenger.Default.Send(new ShowMainWindowMessage());
                            });
                        }
                    }
                    catch
                    {
                        break;
                    }
                }
            });

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { listenerSocket.Dispose(); } catch { }
                try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { }
                try { _singleInstanceLockStream?.Dispose(); } catch { }
            };
        }
        catch
        {
        }

        RunApp(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunApp(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            try
            {
                Directory.CreateDirectory(ClientFileSources.SkuaDIR);
                File.WriteAllText(Path.Combine(ClientFileSources.SkuaDIR, "crash_manager.txt"), ex.ToString());
            }
            catch
            {
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        AppBuilder builder = AppBuilder.Configure<App>()
            .UseWayland()
#pragma warning disable AVALONIA_WAYLAND_FORCE_CSD
            .With(new WaylandPlatformOptions { ForceDrawnDecorations = true })
#pragma warning restore AVALONIA_WAYLAND_FORCE_CSD
            .UseSkia()
            .UseHarfBuzz();

#if DEBUG
        builder = builder.WithDeveloperTools().LogToTrace();
#endif
        return builder;
    }
}
