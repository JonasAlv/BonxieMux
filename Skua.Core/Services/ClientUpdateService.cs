using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace Skua.Core.Services;

public class ClientUpdateService : IClientUpdateService
{
    private const string RepoOwner = "JonasAlv";
    private const string RepoName = "BonxieMux";

    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;

    private string? _remoteTagName;
    private DateTimeOffset _remoteReleaseUpdatedAt;
    private string? _remoteClientFileName;
    private string? _remoteClientUrl;
    private string? _remoteClientDigest;
    private long _remoteClientSize;
    private DateTimeOffset _remoteClientUpdatedAt;

    private string? _remoteManagerFileName;
    private string? _remoteManagerUrl;
    private string? _remoteManagerDigest;
    private long _remoteManagerSize;
    private DateTimeOffset _remoteManagerUpdatedAt;

    public ClientUpdateService(IDialogService dialogService, ISettingsService settingsService)
    {
        _dialogService = dialogService;
        _settingsService = settingsService;
    }

    public string? LatestVersion => _remoteTagName;

    public bool UpdateAvailable { get; private set; }

    public string? UpdateStatusMessage { get; private set; }

    public bool UsingNightlyChannel => _settingsService.Get("UseNightlyBuilds", false);

    public async Task CheckForUpdateAsync()
    {
        UpdateAvailable = false;
        UpdateStatusMessage = "Checking for updates...";
        _remoteTagName = null;
        _remoteReleaseUpdatedAt = default;
        _remoteClientFileName = null;
        _remoteClientUrl = null;
        _remoteClientDigest = null;
        _remoteClientSize = 0;
        _remoteClientUpdatedAt = default;
        _remoteManagerFileName = null;
        _remoteManagerUrl = null;
        _remoteManagerDigest = null;
        _remoteManagerSize = 0;
        _remoteManagerUpdatedAt = default;

        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BonxieMux-ClientUpdateService");

            string apiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            using HttpResponseMessage response = await client.GetAsync(apiUrl);
            if (!response.IsSuccessStatusCode)
            {
                UpdateStatusMessage = $"GitHub check failed ({response.StatusCode}).";
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
                UpdateStatusMessage = "No release tag found.";
                return;
            }

            if (root.TryGetProperty("updated_at", out JsonElement relUpEl) &&
                DateTimeOffset.TryParse(relUpEl.GetString(), out DateTimeOffset relUp))
            {
                _remoteReleaseUpdatedAt = relUp;
            }

            if (root.TryGetProperty("assets", out JsonElement assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement asset in assetsEl.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out JsonElement nameEl)
                        ? nameEl.GetString() ?? string.Empty
                        : string.Empty;

                    DateTimeOffset assetUp = default;
                    if (asset.TryGetProperty("updated_at", out JsonElement assetUpEl) &&
                        DateTimeOffset.TryParse(assetUpEl.GetString(), out DateTimeOffset aUp))
                    {
                        assetUp = aUp;
                    }

                    if (name.StartsWith(AppImageUtils.ManagerPrefix, StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(AppImageUtils.AppImageSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        _remoteManagerFileName = name;
                        _remoteManagerUrl = asset.TryGetProperty("browser_download_url", out JsonElement urlEl) ? urlEl.GetString() : null;
                        _remoteManagerDigest = asset.TryGetProperty("digest", out JsonElement digestEl) ? digestEl.GetString() : null;
                        _remoteManagerSize = asset.TryGetProperty("size", out JsonElement sizeEl) ? sizeEl.GetInt64() : 0;
                        _remoteManagerUpdatedAt = assetUp;
                    }
                    else if (name.StartsWith(AppImageUtils.ClientPrefix, StringComparison.OrdinalIgnoreCase) &&
                             !name.StartsWith(AppImageUtils.ManagerPrefix, StringComparison.OrdinalIgnoreCase) &&
                             name.EndsWith(AppImageUtils.AppImageSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        _remoteClientFileName = name;
                        _remoteClientUrl = asset.TryGetProperty("browser_download_url", out JsonElement urlEl) ? urlEl.GetString() : null;
                        _remoteClientDigest = asset.TryGetProperty("digest", out JsonElement digestEl) ? digestEl.GetString() : null;
                        _remoteClientSize = asset.TryGetProperty("size", out JsonElement sizeEl) ? sizeEl.GetInt64() : 0;
                        _remoteClientUpdatedAt = assetUp;
                    }
                }
            }

            if (string.IsNullOrEmpty(_remoteClientUrl) || string.IsNullOrEmpty(_remoteClientFileName))
            {
                UpdateStatusMessage = $"Release {tagName} found, but AppImage asset is missing.";
                return;
            }

            _remoteTagName = tagName;

            string remoteVerClean = tagName.TrimStart('v', 'V');
            string localVerClean = ClientFileSources.DisplayVersion.TrimStart('v', 'V');
            string remoteVerCore = remoteVerClean.Split('-')[0];
            string localVerCore = localVerClean.Split('-')[0];

            bool isTagNewer;
            if (Version.TryParse(remoteVerCore, out Version? remoteV) && Version.TryParse(localVerCore, out Version? localV))
                isTagNewer = remoteV > localV;
            else
                isTagNewer = !string.Equals(remoteVerClean, localVerClean, StringComparison.OrdinalIgnoreCase);

            if (isTagNewer)
            {
                UpdateAvailable = true;
                UpdateStatusMessage = $"Update {tagName} available! (Current: V{ClientFileSources.DisplayVersion})";
                return;
            }

            // Same version tag (or older): check if release assets were recently updated / differ from local files
            string targetDir = AppImageUtils.ResolveAppImageDirectory();
            string? localClient = AppImageUtils.FindLocalAppImage(targetDir, isManager: false, _remoteClientFileName);
            string? localManager = AppImageUtils.FindLocalAppImage(targetDir, isManager: true, _remoteManagerFileName);

            string? runningApp = AppImageUtils.GetRunningAppImagePath();
            if (!string.IsNullOrEmpty(runningApp))
            {
                string runningName = Path.GetFileName(runningApp);
                if (runningName.StartsWith(AppImageUtils.ManagerPrefix, StringComparison.OrdinalIgnoreCase))
                    localManager = runningApp;
                else if (runningName.StartsWith(AppImageUtils.ClientPrefix, StringComparison.OrdinalIgnoreCase))
                    localClient = runningApp;
            }

            bool fileChanged = false;
            string changeReason = string.Empty;

            // Check Manager binary
            if (!string.IsNullOrEmpty(_remoteManagerUrl))
            {
                if (localManager != null && File.Exists(localManager))
                {
                    if (!string.IsNullOrWhiteSpace(_remoteManagerDigest) &&
                        _remoteManagerDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    {
                        string expectedSha = _remoteManagerDigest.Substring("sha256:".Length).Trim();
                        string localSha = await AppImageUtils.ComputeFileSha256Async(localManager);
                        if (!localSha.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
                        {
                            fileChanged = true;
                            changeReason = "Manager build updated on GitHub";
                        }
                    }
                    else if (_remoteManagerSize > 0 && new FileInfo(localManager).Length != _remoteManagerSize)
                    {
                        fileChanged = true;
                        changeReason = "Manager file size changed on GitHub";
                    }
                    else if (_remoteManagerUpdatedAt != default &&
                             _remoteManagerUpdatedAt > File.GetLastWriteTimeUtc(localManager).AddMinutes(2))
                    {
                        fileChanged = true;
                        changeReason = "Manager re-uploaded on GitHub";
                    }
                }
                else
                {
                    fileChanged = true;
                    changeReason = "Manager AppImage not installed locally";
                }
            }

            // Check Client binary
            if (!fileChanged && !string.IsNullOrEmpty(_remoteClientUrl))
            {
                if (localClient != null && File.Exists(localClient))
                {
                    if (!string.IsNullOrWhiteSpace(_remoteClientDigest) &&
                        _remoteClientDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    {
                        string expectedSha = _remoteClientDigest.Substring("sha256:".Length).Trim();
                        string localSha = await AppImageUtils.ComputeFileSha256Async(localClient);
                        if (!localSha.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
                        {
                            fileChanged = true;
                            changeReason = "BonxieMux client build updated on GitHub";
                        }
                    }
                    else if (_remoteClientSize > 0 && new FileInfo(localClient).Length != _remoteClientSize)
                    {
                        fileChanged = true;
                        changeReason = "BonxieMux client file size changed on GitHub";
                    }
                    else if (_remoteClientUpdatedAt != default &&
                             _remoteClientUpdatedAt > File.GetLastWriteTimeUtc(localClient).AddMinutes(2))
                    {
                        fileChanged = true;
                        changeReason = "BonxieMux client re-uploaded on GitHub";
                    }
                }
                else
                {
                    fileChanged = true;
                    changeReason = "BonxieMux client AppImage not installed locally";
                }
            }

            // Check saved settings metadata
            if (!fileChanged && _settingsService != null)
            {
                string lastSavedClientDigest = _settingsService.Get<string>("LastInstalledClientDigest", string.Empty);
                if (!string.IsNullOrEmpty(lastSavedClientDigest) &&
                    !string.IsNullOrEmpty(_remoteClientDigest) &&
                    !string.Equals(lastSavedClientDigest, _remoteClientDigest, StringComparison.OrdinalIgnoreCase))
                {
                    fileChanged = true;
                    changeReason = "New client asset uploaded to release";
                }

                string lastSavedManagerDigest = _settingsService.Get<string>("LastInstalledManagerDigest", string.Empty);
                if (!fileChanged &&
                    !string.IsNullOrEmpty(lastSavedManagerDigest) &&
                    !string.IsNullOrEmpty(_remoteManagerDigest) &&
                    !string.Equals(lastSavedManagerDigest, _remoteManagerDigest, StringComparison.OrdinalIgnoreCase))
                {
                    fileChanged = true;
                    changeReason = "New manager asset uploaded to release";
                }
            }

            if (fileChanged)
            {
                UpdateAvailable = true;
                UpdateStatusMessage = $"New build for {tagName} detected! ({changeReason})";
            }
            else
            {
                UpdateAvailable = false;
                UpdateStatusMessage = $"You are up to date! ({tagName} build is current)";
            }
        }
        catch (Exception ex)
        {
            UpdateAvailable = false;
            UpdateStatusMessage = $"Error checking updates: {ex.Message}";
        }
    }

    public async Task DownloadUpdateAsync(IProgress<string>? progress)
    {
        if (string.IsNullOrEmpty(_remoteClientUrl) || string.IsNullOrEmpty(_remoteClientFileName))
            await CheckForUpdateAsync();

        if (string.IsNullOrEmpty(_remoteClientUrl) || string.IsNullOrEmpty(_remoteClientFileName))
        {
            progress?.Report("Unable to locate release assets on GitHub.");
            return;
        }

        string updatesRoot = Path.Combine(ClientFileSources.SkuaDIR, ".updates");
        string tempDir = Path.Combine(updatesRoot, "update_" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(updatesRoot);
            ClientFileSources.RestrictDirectoryPermissions(updatesRoot);
            Directory.CreateDirectory(tempDir);
            ClientFileSources.RestrictDirectoryPermissions(tempDir);

            string targetDir = AppImageUtils.ResolveAppImageDirectory();

            var assetsToDownload = new List<(string Name, string Url, string? Digest, long Size)>
            {
                (_remoteClientFileName, _remoteClientUrl, _remoteClientDigest, _remoteClientSize)
            };
            if (!string.IsNullOrEmpty(_remoteManagerUrl) && !string.IsNullOrEmpty(_remoteManagerFileName))
                assetsToDownload.Add((_remoteManagerFileName, _remoteManagerUrl, _remoteManagerDigest, _remoteManagerSize));

            using (HttpClient client = new())
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("BonxieMux-ClientUpdateService");
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
                        while ((read = await contentStream.ReadAsync(buffer)) > 0)
                        {
                            await fileStream.WriteAsync(buffer.AsMemory(0, read));
                            totalReadAll += read;
                            if (totalBytesAll > 0)
                                progress?.Report($"Downloading... {(int)((totalReadAll * 100) / totalBytesAll)}%");
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(digest) &&
                        digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    {
                        string expectedSha256 = digest.Substring("sha256:".Length).Trim();
                        progress?.Report($"Verifying {name}...");
                        string actualHex = await AppImageUtils.ComputeFileSha256Async(filePath);
                        if (!actualHex.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"SHA-256 verification failed for {name} (expected {expectedSha256}, got {actualHex}).");
                    }
                }
            }

            foreach (string downloaded in Directory.GetFiles(tempDir, "*.AppImage"))
            {
                string fileName = Path.GetFileName(downloaded);
                bool isManager = fileName.StartsWith(AppImageUtils.ManagerPrefix, StringComparison.OrdinalIgnoreCase);

                try
                {
                    File.SetUnixFileMode(downloaded,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch { }

                // Unlink any older versioned AppImages of the same component in targetDir
                foreach (string existing in Directory.GetFiles(targetDir, "*.AppImage"))
                {
                    string existingName = Path.GetFileName(existing);
                    bool existingIsManager = existingName.StartsWith(AppImageUtils.ManagerPrefix, StringComparison.OrdinalIgnoreCase);
                    bool existingIsClient = existingName.StartsWith(AppImageUtils.ClientPrefix, StringComparison.OrdinalIgnoreCase) && !existingIsManager;

                    if ((isManager && existingIsManager) || (!isManager && existingIsClient))
                    {
                        try { File.Delete(existing); } catch { }
                    }
                }

                string destination = Path.Combine(targetDir, fileName);
                try { if (File.Exists(destination)) File.Delete(destination); } catch { }

                try
                {
                    File.Move(downloaded, destination, overwrite: true);
                }
                catch
                {
                    try { if (File.Exists(destination)) File.Delete(destination); } catch { }
                    File.Copy(downloaded, destination, overwrite: true);
                    try { File.Delete(downloaded); } catch { }
                }

                try
                {
                    File.SetUnixFileMode(destination,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch { }
            }

            try { Directory.Delete(tempDir, true); } catch { }

            // Save installation metadata in settings
            try
            {
                if (!string.IsNullOrEmpty(_remoteTagName))
                    _settingsService.Set("LastInstalledReleaseTag", _remoteTagName);
                if (_remoteReleaseUpdatedAt != default)
                    _settingsService.Set("LastInstalledReleaseUpdated", _remoteReleaseUpdatedAt.ToString("O"));
                if (!string.IsNullOrEmpty(_remoteClientDigest))
                    _settingsService.Set("LastInstalledClientDigest", _remoteClientDigest);
                if (!string.IsNullOrEmpty(_remoteManagerDigest))
                    _settingsService.Set("LastInstalledManagerDigest", _remoteManagerDigest);
                if (_remoteClientUpdatedAt != default)
                    _settingsService.Set("LastInstalledClientUpdated", _remoteClientUpdatedAt.ToString("O"));
                if (_remoteManagerUpdatedAt != default)
                    _settingsService.Set("LastInstalledManagerUpdated", _remoteManagerUpdatedAt.ToString("O"));
            }
            catch { }

            progress?.Report("Installing update and restarting...");
            string restartPath = !string.IsNullOrEmpty(_remoteManagerFileName)
                ? Path.Combine(targetDir, _remoteManagerFileName)
                : Path.Combine(targetDir, _remoteClientFileName);

            if (File.Exists(restartPath))
            {
                ProcessStartInfo psi = new(restartPath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = targetDir
                };
                AppImageUtils.SanitizeAppImageEnvironment(psi);
                Process.Start(psi);
            }

            Environment.Exit(0);
        }
        catch (Exception e)
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            progress?.Report("Error while updating.");
            _dialogService.ShowMessageBox($"Error Message:\r\n{e.Message}", "Update Error");
        }
    }
}
