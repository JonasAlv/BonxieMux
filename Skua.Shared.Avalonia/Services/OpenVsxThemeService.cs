using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Skua.Shared.Avalonia.Services;

public sealed record OpenVsxThemeEntry(
    string Namespace,
    string Name,
    string DisplayName,
    string Description,
    string Version,
    long DownloadCount,
    string DownloadUrl)
{
    public string Id => $"{Namespace}.{Name}";
    public string InstallDirectory => Path.Combine(VsCodeThemeLoader.GetUserThemesDirectory(), Id);
    public bool IsInstalled => Directory.Exists(InstallDirectory);
}

public sealed record InstalledThemePackage(
    string Name,
    string Subtitle,
    string DirectoryPath,
    bool IsBuiltIn);

public static class OpenVsxThemeService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    static OpenVsxThemeService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("BonxieMux/1.0");
    }

    public static async Task<IReadOnlyList<OpenVsxThemeEntry>> SearchAsync(string? query, CancellationToken ct = default)
    {
        string encoded = Uri.EscapeDataString(query?.Trim() ?? string.Empty);
        string url = $"https://open-vsx.org/api/-/search?category=Themes&size=30&sortBy=downloadCount&query={encoded}";
        using HttpResponseMessage resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        List<OpenVsxThemeEntry> results = [];
        if (!doc.RootElement.TryGetProperty("extensions", out JsonElement exts) || exts.ValueKind != JsonValueKind.Array)
            return results;

        foreach (JsonElement ext in exts.EnumerateArray())
        {
            string ns = ext.TryGetProperty("namespace", out JsonElement nsEl) ? nsEl.GetString() ?? string.Empty : string.Empty;
            string name = ext.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
            string displayName = ext.TryGetProperty("displayName", out JsonElement dnEl) ? dnEl.GetString() ?? name : name;
            string desc = ext.TryGetProperty("description", out JsonElement descEl) ? descEl.GetString() ?? string.Empty : string.Empty;
            string version = ext.TryGetProperty("version", out JsonElement verEl) ? verEl.GetString() ?? string.Empty : string.Empty;
            long downloads = ext.TryGetProperty("downloadCount", out JsonElement dlCountEl) && dlCountEl.TryGetInt64(out long c) ? c : 0;

            string? downloadUrl = null;
            if (ext.TryGetProperty("files", out JsonElement filesEl) &&
                filesEl.ValueKind == JsonValueKind.Object &&
                filesEl.TryGetProperty("download", out JsonElement dlEl))
            {
                downloadUrl = dlEl.GetString();
            }

            if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl))
                continue;

            // Skip icon-only packs when browsing color themes.
            if (name.Contains("icon-theme", StringComparison.OrdinalIgnoreCase) ||
                displayName.Contains("Icon Theme", StringComparison.OrdinalIgnoreCase) ||
                displayName.EndsWith(" Icons", StringComparison.OrdinalIgnoreCase))
                continue;

            results.Add(new OpenVsxThemeEntry(ns, name, displayName, desc, version, downloads, downloadUrl));
        }

        return results;
    }

    public static async Task<int> InstallAsync(OpenVsxThemeEntry entry, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await Http.GetAsync(entry.DownloadUrl, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using MemoryStream mem = new();
        await resp.Content.CopyToAsync(mem, ct).ConfigureAwait(false);
        mem.Position = 0;

        using ZipArchive zip = new(mem, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry? pkgEntry = zip.GetEntry("extension/package.json");
        if (pkgEntry is null)
            throw new InvalidOperationException("Theme package is missing extension/package.json.");

        List<(string Label, string RelPath)> themeEntries = [];
        await using (Stream pkgStream = pkgEntry.Open())
        using (JsonDocument pkgDoc = await JsonDocument.ParseAsync(pkgStream, cancellationToken: ct).ConfigureAwait(false))
        {
            if (pkgDoc.RootElement.TryGetProperty("contributes", out JsonElement contributes) &&
                contributes.TryGetProperty("themes", out JsonElement themes) &&
                themes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement t in themes.EnumerateArray())
                {
                    if (!t.TryGetProperty("path", out JsonElement pathEl) || pathEl.ValueKind != JsonValueKind.String)
                        continue;

                    string? relPath = pathEl.GetString();
                    if (string.IsNullOrWhiteSpace(relPath))
                        continue;

                    string label = t.TryGetProperty("label", out JsonElement lblEl) && lblEl.ValueKind == JsonValueKind.String
                        ? lblEl.GetString() ?? Path.GetFileNameWithoutExtension(relPath)
                        : Path.GetFileNameWithoutExtension(relPath);
                    themeEntries.Add((label, relPath));
                }
            }
        }

        if (themeEntries.Count == 0)
            throw new InvalidOperationException("This extension does not contain any VS Code color themes.");

        string targetRoot = entry.InstallDirectory;
        if (Directory.Exists(targetRoot))
            Directory.Delete(targetRoot, recursive: true);
        Directory.CreateDirectory(targetRoot);

        int extractedCount = 0;
        HashSet<string> extractedZipPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string _, string relPath) in themeEntries)
        {
            string normalizedRel = relPath.TrimStart('.', '/').Replace('\\', '/');
            string zipPath = normalizedRel.StartsWith("extension/", StringComparison.OrdinalIgnoreCase)
                ? normalizedRel
                : $"extension/{normalizedRel}";

            if (ExtractEntryAndIncludes(zip, zipPath, targetRoot, extractedZipPaths))
                extractedCount++;
        }

        if (extractedCount == 0)
        {
            Directory.Delete(targetRoot, recursive: true);
            throw new InvalidOperationException("No color theme JSON files could be extracted from the package.");
        }

        return extractedCount;
    }

    public static void Uninstall(OpenVsxThemeEntry entry)
    {
        UninstallByDirectory(entry.InstallDirectory);
    }

    public static void UninstallByDirectory(string directoryPath)
    {
        if (Directory.Exists(directoryPath))
            Directory.Delete(directoryPath, recursive: true);
    }

    public static IReadOnlyList<InstalledThemePackage> GetInstalledPackages()
    {
        List<InstalledThemePackage> list =
        [
            new InstalledThemePackage("Dark Modern", "Built-in VS Code Dark Modern theme", string.Empty, IsBuiltIn: true),
            new InstalledThemePackage("Light Modern", "Built-in VS Code Light Modern theme", string.Empty, IsBuiltIn: true)
        ];

        string root = VsCodeThemeLoader.GetUserThemesDirectory();
        if (!Directory.Exists(root))
            return list;

        foreach (string dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string pkgName = Path.GetFileName(dir);
            int themeCount = Directory.EnumerateFiles(dir, "*.json*", SearchOption.AllDirectories)
                .Count(f => (f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                             f.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase)) &&
                            !string.Equals(Path.GetFileName(f), "package.json", StringComparison.OrdinalIgnoreCase));
            list.Add(new InstalledThemePackage(pkgName, $"{themeCount} color theme{(themeCount == 1 ? "" : "s")}", dir, IsBuiltIn: false));
        }

        return list;
    }

    private static bool ExtractEntryAndIncludes(
        ZipArchive zip,
        string zipPath,
        string targetRoot,
        HashSet<string> extractedZipPaths)
    {
        if (!extractedZipPaths.Add(zipPath))
            return true;

        ZipArchiveEntry? zipEntry = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, zipPath, StringComparison.OrdinalIgnoreCase));
        if (zipEntry is null)
            return false;

        string relativeUnderExt = zipEntry.FullName.StartsWith("extension/", StringComparison.OrdinalIgnoreCase)
            ? zipEntry.FullName["extension/".Length..]
            : zipEntry.FullName;

        string destPath = Path.GetFullPath(Path.Combine(targetRoot, relativeUnderExt));
        string rootFull = Path.GetFullPath(targetRoot);
        if (!destPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return false;

        string? destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        using (Stream src = zipEntry.Open())
        using (FileStream dst = File.Create(destPath))
        {
            src.CopyTo(dst);
        }

        // If this theme references an "include" base JSON file, extract that too so inheritance works.
        try
        {
            string text = File.ReadAllText(destPath);
            using JsonDocument doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("include", out JsonElement incEl) &&
                incEl.ValueKind == JsonValueKind.String)
            {
                string? incRel = incEl.GetString();
                if (!string.IsNullOrWhiteSpace(incRel))
                {
                    string baseDir = Path.GetDirectoryName(zipEntry.FullName)?.Replace('\\', '/') ?? "extension";
                    string combined = CombineZipPath(baseDir, incRel);
                    ExtractEntryAndIncludes(zip, combined, targetRoot, extractedZipPaths);
                }
            }
        }
        catch { }

        return true;
    }

    private static string CombineZipPath(string baseDir, string rel)
    {
        List<string> parts = baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (string seg in rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".")
                continue;
            if (seg == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(seg);
            }
        }
        return string.Join('/', parts);
    }
}
