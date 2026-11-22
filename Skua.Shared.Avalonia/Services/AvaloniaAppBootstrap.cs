using Avalonia;
using System;
using System.Collections.Generic;
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
        string assembliesDir = Path.Combine(AppContext.BaseDirectory, "Assemblies");
        HashSet<string>? assembliesSet = TryEnumerateFileNames(assembliesDir, "*.dll");

        string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        string runtimeNativeDir = Path.Combine(AppContext.BaseDirectory, "Assemblies", "runtimes", arch, "native");
        HashSet<string>? runtimeNativeSet = TryEnumerateFileNames(runtimeNativeDir);

        AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            if (assemblyName.Name == null || assemblyName.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                return null;

            string fileName = $"{assemblyName.Name}.dll";
            if (assembliesSet != null && assembliesSet.Contains(fileName))
                return context.LoadFromAssemblyPath(Path.Combine(assembliesDir, fileName));

            string rootPath = Path.Combine(AppContext.BaseDirectory, fileName);
            return File.Exists(rootPath) ? context.LoadFromAssemblyPath(rootPath) : null;
        };

        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, unmanagedDllName) =>
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
            if (assembliesSet != null && assembliesSet.Contains(assemblyName))
                return Assembly.LoadFrom(Path.Combine(assembliesDir, assemblyName));

            string rootPath = Path.Combine(AppContext.BaseDirectory, assemblyName);
            return File.Exists(rootPath) ? Assembly.LoadFrom(rootPath) : null;
        };
    }

    private static HashSet<string>? TryEnumerateFileNames(string directory, string searchPattern = "*")
    {
        if (!Directory.Exists(directory))
            return null;

        try
        {
            return new HashSet<string>(
                Directory.EnumerateFiles(directory, searchPattern).Select(Path.GetFileName)!,
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return null;
        }
    }
}
