using Skua.Core.Interfaces;
using Skua.Core.Interfaces.Services;
using Skua.Core.Models;
using Skua.Core.Models.Scripts;
using System.Collections.Specialized;

namespace Skua.Core.Services;

public sealed class CustomScriptService : ICustomScriptService
{
    private readonly ISettingsService _settingsService;

    public CustomScriptService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public IReadOnlyList<string> GetSearchRoots()
    {
        string folder = _settingsService.GetClient().UserCustomScriptsFolder;
        if (string.IsNullOrWhiteSpace(folder))
            return Array.Empty<string>();

        return new[] { Canonicalize(folder) };
    }

    public IReadOnlyList<ScriptSource> Discover()
    {
        Dictionary<string, ScriptSource> sources = new(StringComparer.OrdinalIgnoreCase);

        foreach (string folder in GetSearchRoots())
        {
            if (!Directory.Exists(folder))
                continue;

            try
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
                {
                    string path = Canonicalize(file);
                    sources[path] = new(path, ScriptSourceKind.CustomFolder, true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        foreach (string configuredFile in GetConfiguredFiles())
        {
            string path = Canonicalize(configuredFile);
            if (!sources.ContainsKey(path))
                sources[path] = new(path, ScriptSourceKind.CustomFile, File.Exists(path));
        }

        return sources.Values.ToArray();
    }

    public bool IsCustomPath(string path)
    {
        string canonicalPath = Canonicalize(path);

        foreach (string file in GetConfiguredFiles())
        {
            if (string.Equals(Canonicalize(file), canonicalPath, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return GetSearchRoots().Any(root => IsWithin(root, canonicalPath));
    }

    public void SetCustomFolder(string? path)
    {
        _settingsService.Set("UserCustomScriptsFolder", string.IsNullOrWhiteSpace(path) ? string.Empty : Canonicalize(path));
    }

    public void AddCustomFile(string path)
    {
        string canonicalPath = Canonicalize(path);
        StringCollection files = new();
        foreach (string existing in GetConfiguredFiles())
        {
            if (!ContainsPath(files, existing))
                files.Add(existing);
        }

        if (!ContainsPath(files, canonicalPath))
            files.Add(canonicalPath);

        _settingsService.Set("UserCustomScriptsList", files);
    }

    public void RemoveCustomFile(string path)
    {
        string canonicalPath = Canonicalize(path);
        StringCollection files = new();
        foreach (string existing in GetConfiguredFiles())
        {
            if (!string.Equals(Canonicalize(existing), canonicalPath, StringComparison.OrdinalIgnoreCase))
                files.Add(existing);
        }

        _settingsService.Set("UserCustomScriptsList", files);
    }

    private StringCollection GetConfiguredFiles() => _settingsService.GetClient().UserCustomScriptsList ?? new();

    private static bool ContainsPath(StringCollection files, string path)
    {
        foreach (string file in files)
        {
            if (string.Equals(file, path, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsWithin(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string Canonicalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
