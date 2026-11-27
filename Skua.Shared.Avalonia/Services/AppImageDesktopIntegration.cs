using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Platform;
using Skua.Core.Models;

namespace Skua.Shared.Avalonia.Services;

public enum AppIdentity
{
    BonxieMux,
    Manager
}

public static class AppImageDesktopIntegration
{
    public static void EnsureIntegrated(AppIdentity identity)
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            string? appImagePath = Environment.GetEnvironmentVariable("APPIMAGE");
            if (string.IsNullOrWhiteSpace(appImagePath) || !File.Exists(appImagePath))
                return;

            appImagePath = Path.GetFullPath(appImagePath);

            string name;
            string genericName;
            string comment;
            string iconName;
            string categories;
            string wmClass;

            switch (identity)
            {
                case AppIdentity.Manager:
                    name = "BonxieMux Manager";
                    genericName = "BonxieMux Account & Script Manager";
                    comment = "Account manager, script updater, and launcher for BonxieMux";
                    iconName = "bonxiemux-manager";
                    categories = "Game;";
                    wmClass = "BonxieMux.Manager";
                    break;

                case AppIdentity.BonxieMux:
                default:
                    name = "BonxieMux";
                    genericName = "AQW Bot & Game Client";
                    comment = "Linux AQW client with advanced scripting and multi-account tabs";
                    iconName = "bonxiemux";
                    categories = "Game;RolePlaying;";
                    wmClass = "BonxieMux";
                    break;
            }

            string xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            string appsDir = Path.Combine(xdgDataHome, "applications");
            string desktopFilePath = Path.Combine(appsDir, $"{wmClass}.desktop");
            string iconsBase = Path.Combine(xdgDataHome, "icons", "hicolor");

            string currentVersion = ClientFileSources.AssemblyVersion;
            bool needsUpdate = false;

            string dest256 = Path.Combine(iconsBase, "256x256", "apps", $"{iconName}.png");
            string dest256Wm = Path.Combine(iconsBase, "256x256", "apps", $"{wmClass}.png");

            if (!File.Exists(desktopFilePath) || !File.Exists(dest256) || !File.Exists(dest256Wm))
            {
                needsUpdate = true;
            }
            else
            {
                string oldExec = string.Empty;
                string oldVersion = string.Empty;
                string oldPath = string.Empty;

                foreach (string line in File.ReadAllLines(desktopFilePath))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("Exec=", StringComparison.OrdinalIgnoreCase))
                        oldExec = trimmed.Substring(5).Trim();
                    else if (trimmed.StartsWith("X-BonxieMux-Version=", StringComparison.OrdinalIgnoreCase))
                        oldVersion = trimmed.Substring(20).Trim();
                    else if (trimmed.StartsWith("X-BonxieMux-Path=", StringComparison.OrdinalIgnoreCase))
                        oldPath = trimmed.Substring(17).Trim();
                }

                if (!oldExec.Contains(appImagePath, StringComparison.Ordinal) ||
                    (!string.IsNullOrEmpty(oldPath) && !File.Exists(oldPath)))
                {
                    needsUpdate = true;
                }
                else if (File.GetLastWriteTimeUtc(appImagePath) > File.GetLastWriteTimeUtc(desktopFilePath))
                {
                    needsUpdate = true;
                }
                else if (string.IsNullOrWhiteSpace(oldVersion) ||
                         (Version.TryParse(oldVersion, out var oldV) &&
                          Version.TryParse(currentVersion, out var curV) &&
                          curV > oldV) ||
                         !string.Equals(oldVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
                {
                    needsUpdate = true;
                }
            }

            if (!needsUpdate)
                return;

            bool copiedAnyIcon = false;
            string? appDir = Environment.GetEnvironmentVariable("APPDIR");
            if (!string.IsNullOrWhiteSpace(appDir) && Directory.Exists(appDir))
            {
                string[] sizes = ["16x16", "32x32", "48x48", "64x64", "128x128", "256x256", "512x512"];
                foreach (string s in sizes)
                {
                    string src = Path.Combine(appDir, "usr", "share", "icons", "hicolor", s, "apps", $"{iconName}.png");
                    if (!File.Exists(src))
                        src = Path.Combine(appDir, "usr", "share", "icons", "hicolor", s, "apps", $"{wmClass}.png");

                    if (File.Exists(src))
                    {
                        string destDir = Path.Combine(iconsBase, s, "apps");
                        Directory.CreateDirectory(destDir);
                        string destLower = Path.Combine(destDir, $"{iconName}.png");
                        string destClass = Path.Combine(destDir, $"{wmClass}.png");

                        File.Copy(src, destLower, overwrite: true);
                        ClientFileSources.RestrictFilePermissions(destLower);

                        File.Copy(src, destClass, overwrite: true);
                        ClientFileSources.RestrictFilePermissions(destClass);
                        copiedAnyIcon = true;
                    }
                }

                string rootIcon = Path.Combine(appDir, $"{iconName}.png");
                if (!File.Exists(rootIcon))
                    rootIcon = Path.Combine(appDir, $"{wmClass}.png");

                if (File.Exists(rootIcon))
                {
                    string dest256Dir = Path.Combine(iconsBase, "256x256", "apps");
                    Directory.CreateDirectory(dest256Dir);
                    string dLower = Path.Combine(dest256Dir, $"{iconName}.png");
                    string dClass = Path.Combine(dest256Dir, $"{wmClass}.png");

                    File.Copy(rootIcon, dLower, overwrite: true);
                    ClientFileSources.RestrictFilePermissions(dLower);

                    File.Copy(rootIcon, dClass, overwrite: true);
                    ClientFileSources.RestrictFilePermissions(dClass);
                    copiedAnyIcon = true;
                }
            }

            if (!copiedAnyIcon)
            {
                try
                {
                    string assetUri = identity == AppIdentity.Manager
                        ? "avares://Skua.Shared.Avalonia/Assets/ManagerIcon.png"
                        : "avares://Skua.Shared.Avalonia/Assets/AppIcon.png";
                    using var stream = AssetLoader.Open(new Uri(assetUri));
                    string dest256Dir = Path.Combine(iconsBase, "256x256", "apps");
                    Directory.CreateDirectory(dest256Dir);
                    string dLower = Path.Combine(dest256Dir, $"{iconName}.png");
                    string dClass = Path.Combine(dest256Dir, $"{wmClass}.png");

                    using (var fs = File.Create(dLower))
                    {
                        stream.CopyTo(fs);
                    }
                    ClientFileSources.RestrictFilePermissions(dLower);

                    File.Copy(dLower, dClass, overwrite: true);
                    ClientFileSources.RestrictFilePermissions(dClass);
                }
                catch { }
            }

            Directory.CreateDirectory(appsDir);
            ClientFileSources.RestrictDirectoryPermissions(appsDir);

            string desktopContent =
                $"[Desktop Entry]\n" +
                $"Type=Application\n" +
                $"Name={name}\n" +
                $"GenericName={genericName}\n" +
                $"Comment={comment}\n" +
                $"Exec=\"{appImagePath}\" %U\n" +
                $"Icon={iconName}\n" +
                $"Terminal=false\n" +
                $"Categories={categories}\n" +
                $"StartupWMClass={wmClass}\n" +
                $"X-BonxieMux-Version={currentVersion}\n" +
                $"X-BonxieMux-Path={appImagePath}\n";

            string tempFile = desktopFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tempFile, desktopContent);
                ClientFileSources.RestrictFilePermissions(tempFile);
                File.Move(tempFile, desktopFilePath, overwrite: true);
                ClientFileSources.RestrictFilePermissions(desktopFilePath);
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }

            NotifyDesktopDatabase(appsDir, iconsBase);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AppImageDesktopIntegration] Warning: {ex.Message}");
        }
    }

    private static void NotifyDesktopDatabase(string appsDir, string iconsBase)
    {
        RunUtility("update-desktop-database", $"\"{appsDir}\"");
        RunUtility("gtk-update-icon-cache", $"-f -t \"{iconsBase}\"");
        RunUtility("kbuildsycoca6", "--noincremental");
        RunUtility("kbuildsycoca5", "--noincremental");
    }

    private static void RunUtility(string fileName, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(2000);
        }
        catch { }
    }
}
