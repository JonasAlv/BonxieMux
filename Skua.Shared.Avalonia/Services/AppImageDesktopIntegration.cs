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

/// <summary>
/// Handles Linux desktop integration for BonxieMux and BonxieMux Manager AppImages.
/// Automatically creates and self-heals ~/.local/share/applications/*.desktop entries
/// and ~/.local/share/icons/hicolor/ icons when the AppImage is moved or upgraded.
/// </summary>
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

            string desktopFileName;
            string name;
            string genericName;
            string comment;
            string iconName;
            string categories;
            string wmClass;

            switch (identity)
            {
                case AppIdentity.Manager:
                    desktopFileName = "bonxiemux-manager.desktop";
                    name = "BonxieMux Manager";
                    genericName = "BonxieMux Account & Script Manager";
                    comment = "Account manager, script updater, and launcher for BonxieMux";
                    iconName = "bonxiemux-manager";
                    categories = "Game;";
                    wmClass = "BonxieMux-Manager";
                    break;

                case AppIdentity.BonxieMux:
                default:
                    desktopFileName = "bonxiemux.desktop";
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
            string desktopFilePath = Path.Combine(appsDir, desktopFileName);
            string iconsBase = Path.Combine(xdgDataHome, "icons", "hicolor");

            string currentVersion = ClientFileSources.AssemblyVersion;
            bool needsUpdate = false;

            if (!File.Exists(desktopFilePath))
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

                // If path moved or target no longer exists
                if (!oldExec.Contains(appImagePath, StringComparison.Ordinal) ||
                    (!string.IsNullOrEmpty(oldPath) && !File.Exists(oldPath)))
                {
                    needsUpdate = true;
                }
                // If version changed
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

            // 1. Refresh icon assets in ~/.local/share/icons/hicolor/
            bool copiedAnyIcon = false;
            string? appDir = Environment.GetEnvironmentVariable("APPDIR");
            if (!string.IsNullOrWhiteSpace(appDir) && Directory.Exists(appDir))
            {
                string[] sizes = ["16x16", "32x32", "48x48", "64x64", "128x128", "256x256", "512x512"];
                foreach (string s in sizes)
                {
                    string src = Path.Combine(appDir, "usr", "share", "icons", "hicolor", s, "apps", $"{iconName}.png");
                    if (File.Exists(src))
                    {
                        string destDir = Path.Combine(iconsBase, s, "apps");
                        Directory.CreateDirectory(destDir);
                        string dest = Path.Combine(destDir, $"{iconName}.png");
                        File.Copy(src, dest, overwrite: true);
                        ClientFileSources.RestrictFilePermissions(dest);
                        copiedAnyIcon = true;
                    }
                }

                string rootIcon = Path.Combine(appDir, $"{iconName}.png");
                if (File.Exists(rootIcon))
                {
                    string dest256Dir = Path.Combine(iconsBase, "256x256", "apps");
                    Directory.CreateDirectory(dest256Dir);
                    string dest256 = Path.Combine(dest256Dir, $"{iconName}.png");
                    if (!File.Exists(dest256))
                    {
                        File.Copy(rootIcon, dest256, overwrite: true);
                        ClientFileSources.RestrictFilePermissions(dest256);
                        copiedAnyIcon = true;
                    }
                }
            }

            // Fallback: extract high-res embedded asset from Avalonia resources
            if (!copiedAnyIcon)
            {
                try
                {
                    using var stream = AssetLoader.Open(new Uri("avares://Skua.Shared.Avalonia/Assets/SkuaIcon.png"));
                    string dest256Dir = Path.Combine(iconsBase, "256x256", "apps");
                    Directory.CreateDirectory(dest256Dir);
                    string dest256 = Path.Combine(dest256Dir, $"{iconName}.png");
                    using (var fs = File.Create(dest256))
                    {
                        stream.CopyTo(fs);
                    }
                    ClientFileSources.RestrictFilePermissions(dest256);
                }
                catch { }
            }

            // 2. Write the .desktop file atomically
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

            // 3. Notify the desktop environment to reload application shortcuts
            NotifyDesktopDatabase(appsDir);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AppImageDesktopIntegration] Warning: {ex.Message}");
        }
    }

    private static void NotifyDesktopDatabase(string appsDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "update-desktop-database",
                Arguments = $"\"{appsDir}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
        }
        catch
        {
            // update-desktop-database is optional and may not be installed on all minimal distros
        }
    }
}
