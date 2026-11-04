using Newtonsoft.Json;
using Skua.Core.Interfaces.Services;
using Skua.Core.Models;
using Skua.Core.Models.GitHub;
using Skua.Core.Models.Scripts;
using Skua.Core.Utils;
using System.Collections.Concurrent;

namespace Skua.Core.Scripts;

public sealed class ScriptPathResolver : IScriptPathResolver
{
    private readonly ICustomScriptService _customScripts;
    private readonly string _repositoryRoot = Canonicalize(ClientFileSources.SkuaScriptsDIR);
    private readonly ConcurrentDictionary<(string? ownerFile, string specifier), ScriptPathResolution> _resolutionCache = new();
    private readonly object _remoteIndexLock = new();
    private Dictionary<string, ScriptInfo>? _remoteScriptsByPath;

    public ScriptPathResolver(ICustomScriptService customScripts)
    {
        _customScripts = customScripts;
    }

    public ScriptPathResolution Resolve(string? ownerFile, string specifier)
    {
        if (string.IsNullOrWhiteSpace(specifier))
            return new(null, Array.Empty<string>());

        var cacheKey = (ownerFile, specifier);
        if (_resolutionCache.TryGetValue(cacheKey, out var cachedResolution))
            return cachedResolution;

        string normalizedSpecifier = specifier.Trim().Replace('/', Path.DirectorySeparatorChar);
        List<string> candidates = new();

        if (Path.IsPathRooted(normalizedSpecifier))
        {
            candidates.Add(normalizedSpecifier);
        }
        else if (normalizedSpecifier.StartsWith($".{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                 normalizedSpecifier.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            AddOwnerRelativeCandidate(candidates, ownerFile, normalizedSpecifier);
        }
        else if (normalizedSpecifier.StartsWith($"Scripts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            string subPath = normalizedSpecifier[("Scripts".Length + 1)..];
            candidates.Add(Path.Combine(_repositoryRoot, subPath));

            int firstSlash = subPath.IndexOf(Path.DirectorySeparatorChar);
            if (firstSlash > 0)
            {
                string folderSegment = subPath[..firstSlash];
                string relativeInFolder = subPath[(firstSlash + 1)..];

                // Check repository root with common GitHub ZIP branch suffixes
                candidates.Add(Path.Combine(_repositoryRoot, $"{folderSegment}-main", relativeInFolder));
                candidates.Add(Path.Combine(_repositoryRoot, $"{folderSegment}-master", relativeInFolder));

                // Check relative to owner file directory and parent directory (e.g. scripts inside subfolders)
                if (!string.IsNullOrWhiteSpace(ownerFile))
                {
                    string? ownerDir = Path.GetDirectoryName(Canonicalize(ownerFile));
                    if (!string.IsNullOrEmpty(ownerDir))
                    {
                        candidates.Add(Path.Combine(ownerDir, relativeInFolder));
                        string? parentDir = Path.GetDirectoryName(ownerDir);
                        if (!string.IsNullOrEmpty(parentDir) && !parentDir.Equals(_repositoryRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            candidates.Add(Path.Combine(parentDir, relativeInFolder));
                        }
                    }
                }

                foreach (string customRoot in _customScripts.GetSearchRoots())
                {
                    if (Directory.Exists(customRoot))
                    {
                        candidates.Add(Path.Combine(customRoot, subPath));
                        candidates.Add(Path.Combine(customRoot, $"{folderSegment}-main", relativeInFolder));
                        candidates.Add(Path.Combine(customRoot, $"{folderSegment}-master", relativeInFolder));
                    }
                }
            }
            else
            {
                foreach (string customRoot in _customScripts.GetSearchRoots())
                {
                    if (Directory.Exists(customRoot))
                        candidates.Add(Path.Combine(customRoot, subPath));
                }
            }

            AddOwnerRelativeCandidate(candidates, ownerFile, subPath);
        }
        else if (_customScripts.IsCustomPath(ownerFile ?? string.Empty))
        {
            AddOwnerRelativeCandidate(candidates, ownerFile, normalizedSpecifier);
            candidates.Add(Path.Combine(_repositoryRoot, normalizedSpecifier));
        }
        else
        {
            candidates.Add(Path.Combine(_repositoryRoot, normalizedSpecifier));
            AddOwnerRelativeCandidate(candidates, ownerFile, normalizedSpecifier);
        }

        if (normalizedSpecifier.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(Path.Combine(AppContext.BaseDirectory, normalizedSpecifier));
            string? runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (!string.IsNullOrEmpty(runtimeDir))
                candidates.Add(Path.Combine(runtimeDir, normalizedSpecifier));
        }

        List<string> existing = candidates
            .Select(Canonicalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<string> resolvedFiles = new();
        List<string> errors = new();
        foreach (string candidate in existing)
        {
            string? resolved = ResolveExistingFile(candidate, out string? error);
            if (resolved is not null)
                resolvedFiles.Add(resolved);
            else if (error is not null)
                errors.Add($"{candidate}: {error}");
        }

        if (resolvedFiles.Count == 0 && normalizedSpecifier.StartsWith($"Scripts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            string subPath = normalizedSpecifier[("Scripts".Length + 1)..];
            string? downloaded = TryDownloadMissingInclude(subPath);
            if (!string.IsNullOrEmpty(downloaded) && File.Exists(downloaded))
                resolvedFiles.Add(Canonicalize(downloaded));
        }

        resolvedFiles = resolvedFiles.Distinct(StringComparer.Ordinal).ToList();

        ScriptPathResolution resolution = new(resolvedFiles.FirstOrDefault(), resolvedFiles, errors.Count > 0 ? string.Join("; ", errors) : null);
        if (resolution.SelectedPath != null)
            _resolutionCache[cacheKey] = resolution;

        return resolution;
    }

    // Linux filesystems distinguish case, while script catalogs commonly use Windows-style paths.
    // Resolve one segment at a time and refuse ambiguous matches instead of guessing.
    private static string? ResolveExistingFile(string path, out string? error)
    {
        error = null;
        if (File.Exists(path))
            return path;

        try
        {
            string fullPath = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                return null;

            string relative = fullPath[root.Length..];
            string[] segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string current = root;

            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                string exactNext = Path.Combine(current, segment);
                bool isLast = i == segments.Length - 1;

                if (isLast ? File.Exists(exactNext) : Directory.Exists(exactNext))
                {
                    current = exactNext;
                    continue;
                }

                if (!Directory.Exists(current))
                    return null;

                string[] matches = Directory.EnumerateFileSystemEntries(current)
                    .Where(entry => string.Equals(
                        Path.GetFileName(entry),
                        segment,
                        StringComparison.OrdinalIgnoreCase))
                    .Where(entry => isLast ? File.Exists(entry) : Directory.Exists(entry))
                    .Take(2)
                    .ToArray();

                // Never guess when a Linux directory contains both names differing only by
                // case; Windows would treat that as one path, but Linux cannot do so safely.
                if (matches.Length != 1)
                    return null;

                current = matches[0];
            }

            return File.Exists(current) ? current : null;
        }
        catch (IOException exception)
        {
            error = exception.Message;
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            error = exception.Message;
            return null;
        }
    }

    private string? TryDownloadMissingInclude(string subPath)
    {
        try
        {
            string normalizedKey = subPath.Replace('\\', '/').TrimStart('/');
            Dictionary<string, ScriptInfo> remoteIndex = GetRemoteScriptsIndex();

            string relativePath = normalizedKey;
            string downloadUrl = $"https://raw.githubusercontent.com/auqw/Scripts/Skua/{normalizedKey}";

            if (remoteIndex.TryGetValue(normalizedKey, out ScriptInfo? info))
            {
                if (!string.IsNullOrWhiteSpace(info.FilePath))
                    relativePath = info.FilePath.Replace('\\', '/').TrimStart('/');
                if (!string.IsNullOrWhiteSpace(info.DownloadUrl))
                    downloadUrl = info.DownloadUrl;
            }

            string localTarget = Path.Combine(_repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string? existingLocal = ResolveExistingFile(localTarget, out _);
            if (existingLocal is not null)
                return existingLocal;

            string? dir = Path.GetDirectoryName(localTarget);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var response = ValidatedHttpExtensions.GetAsync(HttpClients.GitHubRaw, downloadUrl).GetAwaiter().GetResult();
            byte[] bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            if (bytes.Length > 0)
            {
                File.WriteAllBytes(localTarget, bytes);
                return localTarget;
            }
        }
        catch
        {
        }

        return null;
    }

    private Dictionary<string, ScriptInfo> GetRemoteScriptsIndex()
    {
        lock (_remoteIndexLock)
        {
            if (_remoteScriptsByPath is not null)
                return _remoteScriptsByPath;

            Dictionary<string, ScriptInfo> index = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                string cacheFile = ClientFileSources.SkuaScriptsCacheFile;
                string? json = null;

                if (File.Exists(cacheFile))
                {
                    json = File.ReadAllText(cacheFile);
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    using var response = ValidatedHttpExtensions.GetAsync(
                        HttpClients.GitHubRaw,
                        "auqw/Scripts/refs/heads/Skua/scripts.json").GetAwaiter().GetResult();
                    json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        string? dir = Path.GetDirectoryName(cacheFile);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        File.WriteAllText(cacheFile, json);
                    }
                }

                if (!string.IsNullOrWhiteSpace(json))
                {
                    List<ScriptInfo>? scripts = JsonConvert.DeserializeObject<List<ScriptInfo>>(json);
                    if (scripts is not null)
                    {
                        foreach (ScriptInfo script in scripts)
                        {
                            if (!string.IsNullOrWhiteSpace(script.FilePath))
                            {
                                string key = script.FilePath.Replace('\\', '/').TrimStart('/');
                                index[key] = script;
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            _remoteScriptsByPath = index;
            return _remoteScriptsByPath;
        }
    }

    private static void AddOwnerRelativeCandidate(List<string> candidates, string? ownerFile, string specifier)
    {
        if (!string.IsNullOrWhiteSpace(ownerFile))
            candidates.Add(Path.Combine(Path.GetDirectoryName(Canonicalize(ownerFile))!, specifier));
    }

    private static string Canonicalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
