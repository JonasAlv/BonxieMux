using Avalonia;
using Avalonia.Skia;
using Avalonia.X11;
using Skua.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Skua.App.Avalonia;

internal static class Program
{
    [DllImport("libX11.so.6")]
    private static extern int XInitThreads();

    [STAThread]
    public static void Main(string[] args)
    {
        XInitThreads();
        InitializeAssemblyResolvers();
        RunApp(args);
    }

    private static void InitializeAssemblyResolvers()
    {
        string assembliesDir = Path.Combine(AppContext.BaseDirectory, "Assemblies");
        HashSet<string>? assembliesSet = null;
        if (Directory.Exists(assembliesDir))
        {
            try
            {
                assembliesSet = new HashSet<string>(
                    Directory.EnumerateFiles(assembliesDir, "*.dll").Select(Path.GetFileName)!,
                    StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
            }
        }

        string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";

        string runtimeNativeDir = Path.Combine(AppContext.BaseDirectory, "Assemblies", "runtimes", arch, "native");
        HashSet<string>? runtimeNativeSet = null;
        if (Directory.Exists(runtimeNativeDir))
        {
            try
            {
                runtimeNativeSet = new HashSet<string>(
                    Directory.EnumerateFiles(runtimeNativeDir).Select(Path.GetFileName)!,
                    StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
            }
        }

        AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            if (assemblyName.Name == null || assemblyName.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                return null;

            string fileName = $"{assemblyName.Name}.dll";
            if (assembliesSet != null && assembliesSet.Contains(fileName))
            {
                return context.LoadFromAssemblyPath(Path.Combine(assembliesDir, fileName));
            }

            string rootPath = Path.Combine(AppContext.BaseDirectory, fileName);
            if (File.Exists(rootPath))
                return context.LoadFromAssemblyPath(rootPath);

            return null;
        };

        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (assembly, unmanagedDllName) =>
        {
            const string ext = ".so";
            string fileName = unmanagedDllName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                ? unmanagedDllName
                : $"{unmanagedDllName}{ext}";

            if (assembliesSet != null && assembliesSet.Contains(fileName))
                return NativeLibrary.Load(Path.Combine(assembliesDir, fileName));

            if (runtimeNativeSet != null && runtimeNativeSet.Contains(fileName))
                return NativeLibrary.Load(Path.Combine(runtimeNativeDir, fileName));

            string rootNative = Path.Combine(AppContext.BaseDirectory, "runtimes", arch, "native", fileName);
            if (File.Exists(rootNative))
                return NativeLibrary.Load(rootNative);

            return IntPtr.Zero;
        };

        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            if (args.Name.Contains(".resources"))
                return null;

            Assembly? existing = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.FullName == args.Name);
            if (existing != null)
                return existing;

            string assemblyName = new AssemblyName(args.Name).Name + ".dll";
            if (assembliesSet != null && assembliesSet.Contains(assemblyName))
            {
                return Assembly.LoadFrom(Path.Combine(assembliesDir, assemblyName));
            }

            string rootPath = Path.Combine(AppContext.BaseDirectory, assemblyName);
            if (File.Exists(rootPath))
                return Assembly.LoadFrom(rootPath);

            return null;
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunApp(string[] args)
    {
        try
        {
            ClientFileSources.EnsureInitialized();
            System.Threading.Tasks.Task.Run(() => Skua.App.Avalonia.Flash.FlashUtil.PreloadSwf());
            Environment.CurrentDirectory = AppContext.BaseDirectory;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash_program.txt"), ex.ToString());
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        AppBuilder builder = AppBuilder.Configure<App>()
            .UseX11()
            .UseSkia();

#if DEBUG
        builder = builder.WithDeveloperTools();
#endif
        return builder.LogToTrace();
    }
}
