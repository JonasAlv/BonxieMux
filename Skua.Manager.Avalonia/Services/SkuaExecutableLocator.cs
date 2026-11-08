using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Skua.Core.Models;
using Skua.Core.Utils;

namespace Skua.Manager.Avalonia.Services;

internal static class SkuaExecutableLocator
{
    public static string ExecutablePath { get; } = Resolve();

    public static string WorkingDirectory =>
        Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory;

    public static ProcessStartInfo CreateProcessStartInfo(IEnumerable<string>? args = null)
    {
        ProcessStartInfo psi = new(ExecutablePath)
        {
            WorkingDirectory = WorkingDirectory
        };
        if (args != null)
        {
            foreach (string arg in args)
                psi.ArgumentList.Add(arg);
        }

        SanitizeAppImageEnvironment(psi);
        return psi;
    }

    public static void SanitizeAppImageEnvironment(ProcessStartInfo psi)
    {
        AppImageUtils.SanitizeAppImageEnvironment(psi);
    }

    private static string? TryFindInDirectory(string dir, string? excludePath = null)
    {
        if (!Directory.Exists(dir))
            return null;

        string currentVersionAppImage = $"BonxieMux-{ClientFileSources.ClientVersion}-x86_64.AppImage";
        string exactVersionPath = Path.Combine(dir, currentVersionAppImage);
        if (File.Exists(exactVersionPath) && !string.Equals(exactVersionPath, excludePath, StringComparison.Ordinal))
            return exactVersionPath;

        try
        {
            string? matchedAppImage = Directory.EnumerateFiles(dir, "BonxieMux-*-x86_64.AppImage")
                .Where(p => !Path.GetFileName(p).StartsWith("BonxieMux-Manager-", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(p, excludePath, StringComparison.Ordinal))
                .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(matchedAppImage))
                return matchedAppImage;
        }
        catch { }

        string[] directCandidates = [ "BonxieMux.AppImage", "BonxieMux" ];
        foreach (string name in directCandidates)
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p) && !string.Equals(p, excludePath, StringComparison.Ordinal))
                return p;
        }

        return null;
    }

    private static string Resolve()
    {
        // If running inside an AppImage, check if a BonxieMux AppImage exists next to this manager AppImage
        string? appImageEnv = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrEmpty(appImageEnv))
        {
            string? hostDir = Path.GetDirectoryName(appImageEnv);
            if (!string.IsNullOrEmpty(hostDir))
            {
                string? foundInHost = TryFindInDirectory(hostDir, appImageEnv);
                if (!string.IsNullOrEmpty(foundInHost))
                    return foundInHost;
            }
        }

        string? foundInBase = TryFindInDirectory(AppContext.BaseDirectory, appImageEnv);
        if (!string.IsNullOrEmpty(foundInBase))
            return foundInBase;

        // Check standard Linux user directories where AppImages might be placed
        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] searchDirs =
        [
            Path.Combine(userHome, "Games", $"BonxieMux-{ClientFileSources.ClientVersion}-x86_64"),
            Path.Combine(userHome, "Games", "BonxieMux"),
            Path.Combine(userHome, "Applications"),
            Path.Combine(userHome, "Desktop"),
            Path.Combine(userHome, "Downloads"),
            Path.Combine(userHome, ".local", "bin"),
            Path.Combine(userHome, ".local", "share", "BonxieMux"),
            "/opt/BonxieMux"
        ];

        foreach (string dir in searchDirs)
        {
            string? found = TryFindInDirectory(dir, appImageEnv);
            if (!string.IsNullOrEmpty(found))
                return found;
        }

        // Check system PATH
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string? found = TryFindInDirectory(dir, appImageEnv);
                if (!string.IsNullOrEmpty(found))
                    return found;
            }
        }

        // Check repository build paths
        string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string? foundInBuild = TryFindInDirectory(Path.Combine(repositoryRoot, "Build"), appImageEnv);
        if (!string.IsNullOrEmpty(foundInBuild))
            return foundInBuild;

        string[] candidates =
        [
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Release", "net10.0", "BonxieMux"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Debug", "net10.0", "BonxieMux")
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(AppContext.BaseDirectory, "BonxieMux");
    }
}
