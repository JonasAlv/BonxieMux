using Skua.Core.Models;
using System.Reflection;
using System.Runtime.Loader;

namespace Skua.Core;

public class ScriptLoadContext : AssemblyLoadContext
{
    private static readonly string _cacheDirectory = Path.Combine(ClientFileSources.SkuaScriptsDIR, "Cached-Scripts");
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _knownAssemblyPaths = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _isUnloading;

    public ScriptLoadContext() : base(isCollectible: true)
    {
        Unloading += context => _isUnloading = true;
    }

    public void RegisterKnownAssembly(string assemblyName, string dllPath)
    {
        _knownAssemblyPaths[assemblyName] = dllPath;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name == null)
            return null;

        if (_isUnloading)
            return null;

        try
        {
            return Default.LoadFromAssemblyName(assemblyName);
        }
        catch
        {
        }

        if (_isUnloading)
            return null;

        if (_knownAssemblyPaths.TryGetValue(assemblyName.Name, out string? knownPath) && File.Exists(knownPath))
        {
            try
            {
                using FileStream stream = File.OpenRead(knownPath);
                return LoadFromStream(stream);
            }
            catch
            {
            }
        }

        if (!Directory.Exists(_cacheDirectory))
            return null;

        string[] matchingFiles = Directory.GetFiles(_cacheDirectory, $"*-{assemblyName.Name}.dll");

        if (matchingFiles.Length > 0)
        {
            string latestFile = matchingFiles.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();

            if (!File.Exists(latestFile))
                return null;

            try
            {
                using FileStream stream = File.OpenRead(latestFile);
                return LoadFromStream(stream);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}