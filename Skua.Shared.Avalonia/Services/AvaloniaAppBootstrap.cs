using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Skua.Shared.Avalonia.Services;

public static class AvaloniaAppBootstrap
{
    public static void IgnoreSighup()
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => ctx.Cancel = true);
        }
        catch
        {
        }
    }

    public static void InitializeAssemblyResolvers()
    {
        string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";

        AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            if (assemblyName.Name == null || assemblyName.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                return null;

            string rootPath = Path.Combine(AppContext.BaseDirectory, $"{assemblyName.Name}.dll");
            return File.Exists(rootPath) ? context.LoadFromAssemblyPath(rootPath) : null;
        };

        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, unmanagedDllName) =>
        {
            const string ext = ".so";
            string fileName = unmanagedDllName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                ? unmanagedDllName
                : $"{unmanagedDllName}{ext}";

            string rootNative = Path.Combine(AppContext.BaseDirectory, "runtimes", arch, "native", fileName);
            return File.Exists(rootNative) ? NativeLibrary.Load(rootNative) : IntPtr.Zero;
        };

        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            if (args.Name.Contains(".resources"))
                return null;

            Assembly? existing = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.FullName == args.Name);
            if (existing != null)
                return existing;

            string assemblyName = new AssemblyName(args.Name).Name + ".dll";
            string rootPath = Path.Combine(AppContext.BaseDirectory, assemblyName);
            return File.Exists(rootPath) ? Assembly.LoadFrom(rootPath) : null;
        };
    }
}
