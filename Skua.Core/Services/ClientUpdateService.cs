using Skua.Core.Interfaces;
using Skua.Core.Models;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Skua.Core.Services;

public class ClientUpdateService : IClientUpdateService
{
    private const string RepoOwner = "JonasAlv";
    private const string RepoName = "VibeSkua";
    private const string ClientPrefix = "BonxieMux-";
    private const string ManagerPrefix = "BonxieMux-Manager-";
    private const string AppImageSuffix = "-x86_64.AppImage";

    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;

    private string? _remoteTagName;
    private string? _remoteClientFileName;
    private string? _remoteClientUrl;
    private string? _remoteClientDigest;
    private long _remoteClientSize;
    private string? _remoteManagerFileName;
    private string? _remoteManagerUrl;
    private string? _remoteManagerDigest;
    private long _remoteManagerSize;

    public ClientUpdateService(IDialogService dialogService, ISettingsService settingsService)
    {
        _dialogService = dialogService;
        _settingsService = settingsService;
    }

    public string? LatestVersion => _remoteTagName;

    public bool UpdateAvailable { get; private set; }

    public bool UsingNightlyChannel => _settingsService.Get("UseNightlyBuilds", false);

    public async Task CheckForUpdateAsync()
    {
        UpdateAvailable = false;
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
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BonxieMux-ClientUpdateService");

            string apiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            using HttpResponseMessage response = await client.GetAsync(apiUrl);
            if (!response.IsSuccessStatusCode)
                return;

            string json = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out JsonElement tagEl)
                ? tagEl.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrEmpty(tagName))
                return;

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
                        _remoteManagerUrl = asset.TryGetProperty("browser_download_url", out JsonElement urlEl) ? urlEl.GetString() : null;
                        _remoteManagerDigest = asset.TryGetProperty("digest", out JsonElement digestEl) ? digestEl.GetString() : null;
                        _remoteManagerSize = asset.TryGetProperty("size", out JsonElement sizeEl) ? sizeEl.GetInt64() : 0;
                    }
                    else if (name.StartsWith(ClientPrefix, StringComparison.OrdinalIgnoreCase) &&
                             !name.StartsWith(ManagerPrefix, StringComparison.OrdinalIgnoreCase) &&
                             name.EndsWith(AppImageSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        _remoteClientFileName = name;
                        _remoteClientUrl = asset.TryGetProperty("browser_download_url", out JsonElement urlEl) ? urlEl.GetString() : null;
                        _remoteClientDigest = asset.TryGetProperty("digest", out JsonElement digestEl) ? digestEl.GetString() : null;
                        _remoteClientSize = asset.TryGetProperty("size", out JsonElement sizeEl) ? sizeEl.GetInt64() : 0;
                    }
                }
            }

            if (string.IsNullOrEmpty(_remoteClientUrl) || string.IsNullOrEmpty(_remoteClientFileName))
                return;

            _remoteTagName = tagName;

            string remoteVerCore = tagName.TrimStart('v', 'V').Split('-')[0];
            string localVerCore = ClientFileSources.DisplayVersion.TrimStart('v', 'V').Split('-')[0];

            if (Version.TryParse(remoteVerCore, out Version? remoteV) && Version.TryParse(localVerCore, out Version? localV))
                UpdateAvailable = remoteV > localV;
            else
                UpdateAvailable = !string.Equals(remoteVerCore, localVerCore, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            UpdateAvailable = false;
        }
    }

    public async Task DownloadUpdateAsync(IProgress<string>? progress)
    {
        if (string.IsNullOrEmpty(_remoteClientUrl) || string.IsNullOrEmpty(_remoteClientFileName))
            await CheckForUpdateAsync();

        if (!UpdateAvailable || string.IsNullOrEmpty(_remoteClientUrl) || string.IsNullOrEmpty(_remoteClientFileName))
        {
            progress?.Report("You have the latest version.");
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
                        await using FileStream verifyStream = File.OpenRead(filePath);
                        string actualHex = Convert.ToHexString(await SHA256.HashDataAsync(verifyStream));
                        if (!actualHex.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"SHA-256 verification failed for {name} (expected {expectedSha256}, got {actualHex}).");
                    }
                }
            }

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

            progress?.Report("Installing update and restarting...");
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
        catch (Exception e)
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            progress?.Report("Error while updating.");
            _dialogService.ShowMessageBox($"Error Message:\r\n{e.Message}", "Update Error");
        }
    }
}
