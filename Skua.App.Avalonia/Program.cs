using Avalonia;
using Avalonia.Skia;
using Skua.Core.Models;
using Skua.Shared.Avalonia.Services;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Skua.App.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AvaloniaAppBootstrap.IgnoreSighup();
        AvaloniaAppBootstrap.InitializeAssemblyResolvers();
        RunApp(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunApp(string[] args)
    {
        try
        {
            ClientFileSources.EnsureInitialized();
            Task.Run(() => Skua.Backend.Flash.FlashUtil.PreloadSwf());
            Environment.CurrentDirectory = AppContext.BaseDirectory;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            try
            {
                Directory.CreateDirectory(ClientFileSources.SkuaDIR);
                File.WriteAllText(Path.Combine(ClientFileSources.SkuaDIR, "crash_program.txt"), ex.ToString());
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
