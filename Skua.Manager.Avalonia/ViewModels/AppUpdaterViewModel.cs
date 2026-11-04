using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.Manager.Avalonia.ViewModels;

public partial class AppUpdaterViewModel : ObservableObject
{
    private readonly IDispatcherService _dispatcherService;
    private const string RepoOwner = "JonasAlv";
    private const string RepoName = "VibeSkua";
    private const string ClientPrefix = "BonxieMux-";
    private const string ManagerPrefix = "BonxieMux-Manager-";
    private const string AppImageSuffix = "-x86_64.AppImage";

    public AppUpdaterViewModel(IDispatcherService dispatcherService)
    {
        _dispatcherService = dispatcherService;
    }

    [ObservableProperty]
    private string _updateStatus = "Ready to check for updates.";

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private int _progressValue;

    private string? _remoteTagName;
    private string? _remoteClientFileName;
    private string? _remoteClientUrl;
    private string? _remoteClientDigest;
    private long _remoteClientSize;
    private string? _remoteManagerFileName;
    private string? _remoteManagerUrl;
    private string? _remoteManagerDigest;
    private long _remoteManagerSize;

    public async Task CheckForUpdateAsync()
    {
        _dispatcherService.Invoke(() =>
        {
            IsChecking = true;
            UpdateStatus = "Checking for updates...";
            UpdateAvailable = false;
            ProgressValue = 0;
        });

        _remoteTagName = null;
        _remoteClientFileName = null;
        _remoteClientUrl = null;
        _remoteClientDigest = null;
        _remoteClientSize = 0;
        _remoteManagerFileName = null;
        _remoteManagerUrl = null;
        _remoteManagerDigest = null;
        _remoteManagerSize = 0;

        try
        {
            using HttpClient client = new();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BonxieMux-Manager-Updater");
            client.Timeout = TimeSpan.FromSeconds(15);

            string apiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            using HttpResponseMessage response = await client.GetAsync(apiUrl);

            if (!response.IsSuccessStatusCode)
            {
                _dispatcherService.Invoke(() =>
                {
                    UpdateStatus = response.StatusCode == System.Net.HttpStatusCode.NotFound
                        ? "No releases found on GitHub."
                        : $"GitHub check failed: {response.ReasonPhrase}";
                    UpdateAvailable = false;
                });
                return;
            }

            string json = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out JsonElement tagEl)
                ? tagEl.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrEmpty(tagName))
            {
                _dispatcherService.Invoke(() =>
                {
                    UpdateStatus = "No valid release tag found.";
                    UpdateAvailable = false;
                });
                return;
            }

            if (root.TryGetProperty("assets", out JsonElement assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement asset in assetsEl.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out JsonElement nameEl)
                        ? nameEl.GetString() ?? string.Empty
                        : string.Empty;

                    if (name.StartsWith(ManagerPrefix, StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(AppImageSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        _remoteManagerFileName = name;
                        _remoteManagerUrl = asset.TryGetProperty("browser_download_url", out JsonElement urlEl)
                            ? urlEl.GetString()
                            : null;
                        _remoteManagerDigest = asset.TryGetProperty("digest", out JsonElement digestEl)
                            ? digestEl.GetString()
                            : null;
                        _remoteManagerSize = asset.TryGetProperty("size", out JsonElement sizeEl)
                            ? sizeEl.GetInt64()
                            : 0;
                    }
                    else if (name.StartsWith(ClientPrefix, StringComparison.OrdinalIgnoreCase) &&
                             !name.StartsWith(ManagerPrefix, StringComparison.OrdinalIgnoreCase) &&
                             name.EndsWith(AppImageSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        _remoteClientFileName = name;
                        _remoteClientUrl = asset.TryGetProperty("browser_download_url", out JsonElement urlEl)
                            ? urlEl.GetString()
                            : null;
                        _remoteClientDigest = asset.TryGetProperty("digest", out JsonElement digestEl)
                            ? digestEl.GetString()
                            : null;
                        _remoteClientSize = asset.TryGetProperty("size", out JsonElement sizeEl)
                            ? sizeEl.GetInt64()
                            : 0;
                    }
                }
            }

            if (string.IsNullOrEmpty(_remoteClientUrl) || string.IsNullOrEmpty(_remoteClientFileName))
            {
                _dispatcherService.Invoke(() =>
                {
                    UpdateStatus = $"Release {tagName} found, but AppImage asset is missing.";
                    UpdateAvailable = false;
                });
                return;
            }

            _remoteTagName = tagName;

            string remoteVerClean = tagName.TrimStart('v', 'V');
            string localVerClean = ClientFileSources.DisplayVersion.TrimStart('v', 'V');

            bool isNewer = false;
            string remoteVerCore = remoteVerClean.Split('-')[0];
            string localVerCore = localVerClean.Split('-')[0];

            if (Version.TryParse(remoteVerCore, out Version? remoteV) && Version.TryParse(localVerCore, out Version? localV))
            {
                isNewer = remoteV > localV;
            }
            else
            {
                isNewer = !string.Equals(remoteVerClean, localVerClean, StringComparison.OrdinalIgnoreCase);
            }

            _dispatcherService.Invoke(() =>
            {
                if (isNewer)
                {
                    UpdateStatus = $"Update {tagName} available! (Current: V{ClientFileSources.DisplayVersion})";
                    UpdateAvailable = true;
                }
                else
                {
                    UpdateStatus = $"You are up to date! (V{ClientFileSources.DisplayVersion})";
                    UpdateAvailable = false;
                }
            });
        }
        catch (Exception ex)
        {
            _dispatcherService.Invoke(() =>
            {
                UpdateStatus = $"Error checking updates: {ex.Message}";
                UpdateAvailable = false;
            });
        }
        finally
        {
            _dispatcherService.Invoke(() => IsChecking = false);
        }
    }

    [RelayCommand]
    private async Task CheckForUpdate()
    {
        await CheckForUpdateAsync();
    }

    public async Task DownloadAndInstallAsync()
    {
        if (string.IsNullOrEmpty(_remoteClientUrl) || string.IsNullOrEmpty(_remoteClientFileName))
            return;

        _dispatcherService.Invoke(() =>
        {
            IsChecking = true;
            UpdateStatus = "Starting download...";
            ProgressValue = 0;
        });

        string updatesRoot = Path.Combine(ClientFileSources.SkuaDIR, ".updates");
        string tempDir = Path.Combine(updatesRoot, "update_" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(updatesRoot);
            ClientFileSources.RestrictDirectoryPermissions(updatesRoot);
            Directory.CreateDirectory(tempDir);
            ClientFileSources.RestrictDirectoryPermissions(tempDir);

            string? runningAppImage = Environment.GetEnvironmentVariable("APPIMAGE");
            string targetDir = !string.IsNullOrEmpty(runningAppImage) && File.Exists(runningAppImage)
                ? Path.GetDirectoryName(runningAppImage)!
                : AppContext.BaseDirectory;

            var assetsToDownload = new List<(string Name, string Url, string? Digest, long Size)>
            {
                (_remoteClientFileName, _remoteClientUrl, _remoteClientDigest, _remoteClientSize)
            };
            if (!string.IsNullOrEmpty(_remoteManagerUrl) && !string.IsNullOrEmpty(_remoteManagerFileName))
                assetsToDownload.Add((_remoteManagerFileName, _remoteManagerUrl, _remoteManagerDigest, _remoteManagerSize));

            using (HttpClient client = new())
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("BonxieMux-Manager-Updater");
                long totalBytesAll = assetsToDownload.Sum(a => a.Size);
                long totalReadAll = 0;

                foreach (var (name, url, digest, _) in assetsToDownload)
                {
                    string filePath = Path.Combine(tempDir, name);
                    using HttpResponseMessage resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();

                    await using Stream contentStream = await resp.Content.ReadAsStreamAsync();
                    await using (FileStream fileStream = new(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        ClientFileSources.RestrictFilePermissions(filePath);

                        byte[] buffer = new byte[81920];
                        int read;
                        while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer.AsMemory(0, read));
                            totalReadAll += read;
                            if (totalBytesAll > 0)
                            {
                                int pct = (int)((totalReadAll * 100) / totalBytesAll);
                                _dispatcherService.Invoke(() =>
                                {
                                    ProgressValue = pct;
                                    UpdateStatus = $"Downloading {pct}% ({totalReadAll / (1024 * 1024)} MB / {totalBytesAll / (1024 * 1024)} MB)...";
                                });
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(digest) &&
                        digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    {
                        string expectedSha256 = digest.Substring("sha256:".Length).Trim();
                        _dispatcherService.Invoke(() => UpdateStatus = $"Verifying SHA-256 for {name}...");
                        await using FileStream verifyStream = File.OpenRead(filePath);
                        byte[] actualHash = await SHA256.HashDataAsync(verifyStream);
                        string actualHex = Convert.ToHexString(actualHash);
                        if (!actualHex.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException($"SHA-256 verification failed for {name} (expected {expectedSha256}, got {actualHex}).");
                        }
                    }
                }
            }

            _dispatcherService.Invoke(() => UpdateStatus = "Applying update...");

            foreach (string downloaded in Directory.GetFiles(tempDir, "*.AppImage"))
            {
                string fileName = Path.GetFileName(downloaded);
                bool isManager = fileName.StartsWith(ManagerPrefix, StringComparison.OrdinalIgnoreCase);

                try
                {
                    File.SetUnixFileMode(downloaded,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch { }

                // Remove any older versioned AppImages of the same component in targetDir
                foreach (string existing in Directory.GetFiles(targetDir, "*.AppImage"))
                {
                    string existingName = Path.GetFileName(existing);
                    bool existingIsManager = existingName.StartsWith(ManagerPrefix, StringComparison.OrdinalIgnoreCase);
                    bool existingIsClient = existingName.StartsWith(ClientPrefix, StringComparison.OrdinalIgnoreCase) && !existingIsManager;

                    if ((isManager && existingIsManager) || (!isManager && existingIsClient))
                    {
                        try { File.Delete(existing); } catch { }
                    }
                }

                string destination = Path.Combine(targetDir, fileName);
                try
                {
                    File.Move(downloaded, destination, overwrite: true);
                }
                catch
                {
                    File.Copy(downloaded, destination, overwrite: true);
                    try { File.Delete(downloaded); } catch { }
                }
            }

            try { Directory.Delete(tempDir, true); } catch { }

            _dispatcherService.Invoke(() =>
            {
                UpdateStatus = "Update complete! Restarting BonxieMux Manager...";
                ProgressValue = 100;
            });

            await Task.Delay(1000);

            string restartPath = !string.IsNullOrEmpty(_remoteManagerFileName)
                ? Path.Combine(targetDir, _remoteManagerFileName)
                : Path.Combine(targetDir, _remoteClientFileName);

            if (File.Exists(restartPath))
            {
                Process.Start(new ProcessStartInfo(restartPath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = targetDir
                });
            }

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }

            _dispatcherService.Invoke(() =>
            {
                UpdateStatus = $"Error updating: {ex.Message}";
                UpdateAvailable = true;
            });
        }
        finally
        {
            _dispatcherService.Invoke(() => IsChecking = false);
        }
    }

    [RelayCommand]
    private async Task DownloadAndInstall()
    {
        await DownloadAndInstallAsync();
    }
}
