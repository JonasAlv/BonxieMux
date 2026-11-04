using Newtonsoft.Json;
using System;
using System.IO;
using System.Security.Cryptography;

namespace Skua.Core.Models.GitHub;

public class ScriptInfo
{
    private string _name = string.Empty;
    [JsonProperty("name")]
    public string Name
    {
        get => !string.IsNullOrWhiteSpace(_name) && !_name.Equals("null", StringComparison.OrdinalIgnoreCase)
            ? _name
            : !string.IsNullOrWhiteSpace(FileName) && !FileName.Equals("null", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(FileName)
                : Path.GetFileNameWithoutExtension(FilePath);
        set => _name = value;
    }

    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    [JsonProperty("tags")]
    public string[] Tags { get; set; } = Array.Empty<string>();

    [JsonProperty("path")]
    public string FilePath { get; set; } = string.Empty;

    [JsonProperty("size")]
    public int Size { get; set; }

    [JsonProperty("creationDate")]
    public DateTime? CreationDate { get; set; }

    [JsonProperty("sha256")]
    public string? Sha256 { get; set; }

    private string _fileName = string.Empty;
    [JsonProperty("fileName")]
    public string FileName
    {
        get => !string.IsNullOrWhiteSpace(_fileName) && !_fileName.Equals("null", StringComparison.OrdinalIgnoreCase)
            ? _fileName
            : Path.GetFileName(FilePath);
        set => _fileName = value;
    }

    private string _downloadUrl = string.Empty;
    [JsonProperty("downloadUrl")]
    public string DownloadUrl
    {
        get => !string.IsNullOrWhiteSpace(_downloadUrl)
            ? _downloadUrl
            : $"https://raw.githubusercontent.com/auqw/Scripts/refs/heads/Skua/{FilePath.Replace('\\', '/')}";
        set => _downloadUrl = value;
    }

    public string RelativePath => FilePath == FileName ? "Scripts/" : $"Scripts/{FilePath.Replace(FileName, "")}";

    public string LocalFile => Path.GetFullPath(Path.Combine(ClientFileSources.SkuaScriptsDIR, FilePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));

    public string ManagerLocalFile => Path.GetFullPath(Path.Combine(ClientFileSources.SkuaScriptsDIR, FilePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));

    [JsonIgnore]
    public bool? CachedDownloaded { get; set; }

    [JsonIgnore]
    public int? CachedLocalSize { get; set; }

    public bool Downloaded => CachedDownloaded ?? File.Exists(LocalFile);

    public int LocalSize => CachedLocalSize ?? (Downloaded ? (int)new FileInfo(LocalFile).Length : 0);

    private string? _cachedSha256;
    private long _cachedSha256Size = -1;
    private DateTime _cachedSha256Time = DateTime.MinValue;

    public string? LocalSha256
    {
        get
        {
            if (!Downloaded) return null;
            try
            {
                var fileInfo = new FileInfo(LocalFile);
                if (!fileInfo.Exists) return null;
                if (_cachedSha256 != null && _cachedSha256Size == fileInfo.Length && _cachedSha256Time == fileInfo.LastWriteTimeUtc)
                    return _cachedSha256;

                using SHA256 sha256 = System.Security.Cryptography.SHA256.Create();
                using FileStream stream = File.OpenRead(LocalFile);
                byte[] hash = sha256.ComputeHash(stream);
                _cachedSha256 = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                _cachedSha256Size = fileInfo.Length;
                _cachedSha256Time = fileInfo.LastWriteTimeUtc;
                return _cachedSha256;
            }
            catch { return null; }
        }
    }

    public bool Outdated
    {
        get
        {
            if (!Downloaded) return false;
            if (LocalSize != Size) return true;
            if (!string.IsNullOrEmpty(Sha256) && LocalSha256 != null)
                return !string.Equals(LocalSha256, Sha256, StringComparison.OrdinalIgnoreCase);
            return false;
        }
    }

    public override string ToString()
    {
        return FileName;
    }
}