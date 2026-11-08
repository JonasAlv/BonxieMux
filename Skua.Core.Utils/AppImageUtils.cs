using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Skua.Core.Utils;

public static class AppImageUtils
{
    public const string ClientPrefix = "BonxieMux-";
    public const string ManagerPrefix = "BonxieMux-Manager-";
    public const string AppImageSuffix = "-x86_64.AppImage";

    /// <summary>
    /// Gets the path to the currently running AppImage, if any.
    /// </summary>
    public static string? GetRunningAppImagePath()
    {
        string? appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        return !string.IsNullOrEmpty(appImage) && File.Exists(appImage) ? appImage : null;
    }

    /// <summary>
    /// Resolves the host directory where AppImages are located.
    /// Prefers the running AppImage's directory, falling back to standard user directories.
    /// </summary>
    public static string ResolveAppImageDirectory()
    {
        string? running = GetRunningAppImagePath();
        if (!string.IsNullOrEmpty(running))
        {
            string? dir = Path.GetDirectoryName(running);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                return dir;
        }

        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] searchDirs =
        [
            Path.Combine(userHome, "Applications"),
            Path.Combine(userHome, "Games", "BonxieMux"),
            Path.Combine(userHome, ".local", "bin"),
            Path.Combine(userHome, "Desktop"),
            Path.Combine(userHome, "Downloads")
        ];

        foreach (string dir in searchDirs)
        {
            try
            {
                if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "BonxieMux*.AppImage").Any())
                    return dir;
            }
            catch { }
        }

        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Finds a local AppImage matching the component prefix (Client vs Manager) in the specified directory.
    /// </summary>
    public static string? FindLocalAppImage(string directory, bool isManager, string? exactFileName = null)
    {
        if (!Directory.Exists(directory))
            return null;

        if (!string.IsNullOrEmpty(exactFileName))
        {
            string exactPath = Path.Combine(directory, exactFileName);
            if (File.Exists(exactPath))
                return exactPath;
        }

        try
        {
            return Directory.EnumerateFiles(directory, "BonxieMux-*.AppImage")
                .Where(f =>
                {
                    string name = Path.GetFileName(f);
                    bool fileIsManager = name.StartsWith(ManagerPrefix, StringComparison.OrdinalIgnoreCase);
                    return isManager ? fileIsManager : !fileIsManager;
                })
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Computes the uppercase hexadecimal SHA-256 hash of a file asynchronously.
    /// </summary>
    public static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        await using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(fs, cancellationToken);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Detaches AppImage-specific environment variables so a child process/AppImage
    /// manages its own lifecycle and mount without inheriting the parent AppImage's mount.
    /// </summary>
    public static void SanitizeAppImageEnvironment(ProcessStartInfo psi)
    {
        string? appDir = Environment.GetEnvironmentVariable("APPDIR");

        psi.Environment.Remove("APPDIR");
        psi.Environment.Remove("APPIMAGE");
        psi.Environment.Remove("ARGV0");
        psi.Environment.Remove("OWD");

        if (!string.IsNullOrEmpty(appDir))
        {
            string? ldLibPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
            if (!string.IsNullOrEmpty(ldLibPath))
            {
                var cleanedLd = ldLibPath.Split(':')
                    .Where(p => !string.IsNullOrEmpty(p) && !p.StartsWith(appDir, StringComparison.Ordinal));
                psi.Environment["LD_LIBRARY_PATH"] = string.Join(':', cleanedLd);
            }

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                var cleanedPath = pathEnv.Split(':')
                    .Where(p => !string.IsNullOrEmpty(p) && !p.StartsWith(appDir, StringComparison.Ordinal));
                psi.Environment["PATH"] = string.Join(':', cleanedPath);
            }
        }
    }
}
