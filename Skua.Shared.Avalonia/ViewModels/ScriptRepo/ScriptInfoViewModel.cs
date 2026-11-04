using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Core.Models.GitHub;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace Skua.Shared.Avalonia.ViewModels.ScriptRepo;

public partial class ScriptInfoViewModel : ObservableObject
{
    public ScriptInfoViewModel(ScriptInfo info, string? localFileOverride = null, bool isCustom = false, bool? isDownloaded = null, int? localSize = null, bool isPinned = false)
    {
        Info = info;
        _localFileOverride = localFileOverride;
        IsCustom = isCustom;
        _downloaded = isDownloaded ?? info.CachedDownloaded ?? DownloadedFileExists;
        _localSize = localSize ?? info.CachedLocalSize ?? (Downloaded ? (int)new FileInfo(LocalFile).Length : 0);
        _isPinned = isPinned;

        // Instances are built off the UI thread, so the SHA256 check can run here.
        _infoOutdated = !IsCustom && Info.Outdated;
    }

    public ScriptInfo Info { get; }
    private readonly string? _localFileOverride;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Outdated), nameof(StatusText), nameof(StatusColor), nameof(StatusBackground))]
    private int _localSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinIcon), nameof(PinColor), nameof(PinToolTip))]
    private bool _isPinned;

    public string PinIcon => IsPinned ? "Pin" : "PinOutline";
    public string PinColor => IsPinned ? "#F59E0B" : "#9CA3AF";
    public string PinToolTip => IsPinned ? "Unpin script" : "Pin script to top";
    public string ScriptKey => (IsCustom ? LocalFile : FilePath).Replace('\\', '/').Trim();

    // Search, as in Skua 1.4.5: null when the script doesn't match, else a rank (lower comes first). "All" ranks name,
    // then description, then path, then tag matches; one-field scopes rank exact, then "starts with", then "contains".
    // The path is ListPath (from the Scripts folder), so the local folder above it never matches.
    public int? SearchRank(string query, string scope)
    {
        switch (scope)
        {
            case "Name":
                return TextRank(FileName, query);
            case "Tag":
                return InfoTags.Select(t => TextRank(t, query)).Min();
            case "Description":
                return TextRank(Info.Description, query);
            case "File":
                return new[] { TextRank(ListPath, query), TextRank(Path.GetFileName(ListPath), query) }.Min();
            default:
                if (FileName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                    return 0;
                if (Info.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                    return 1;
                if (ListPath.Contains(query, StringComparison.OrdinalIgnoreCase))
                    return 2;
                return InfoTags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)) ? 3 : null;
        }
    }

    private static int? TextRank(string? text, string query)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        if (text.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 1;
        return text.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : null;
    }
    public string FileName => Info.Name;
    public int Size => Info.Size;
    public string LocalFile => _localFileOverride ?? Info.LocalFile;
    public string FilePath => Info.FilePath;
    public bool IsCustom { get; }
    public string DisplayPath => IsCustom ? LocalFile : ScriptPath;
    // The list's path: repo-relative for repo scripts, the whole path for custom ones (they can be anywhere).
    // A zero-width space after each separator lets it wrap at folders instead of being trimmed.
    public string ListPath => IsCustom ? LocalFile : FilePath;
    public string WrappablePath => ListPath.Replace("\\", "\\\u200B").Replace("/", "/\u200B");

    private bool DownloadedFileExists => File.Exists(LocalFile);
    public string ScriptPath => Path.Combine(ClientFileSources.SkuaScriptsDIR, FilePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
    // Dates come from "Update dates from Git"; until then there is none to show
    public bool HasCreationDate => Info.CreationDate.HasValue && Info.CreationDate.Value > DateTime.MinValue;
    // Follows the path in a list row: size, and the date when there is one
    public string ListMeta => HasCreationDate ? $" · {FormattedSize} · {FormattedCreationDate}" : $" · {FormattedSize}";
    public string FormattedCreationDate => Info.CreationDate.HasValue && Info.CreationDate.Value > DateTime.MinValue ? Info.CreationDate.Value.ToString("MMM dd, yyyy") : "Unknown";

    private ObservableCollection<string>? _infoTags;
    // Blank tags from the repo index are left out
    public ObservableCollection<string> InfoTags => _infoTags ??= new((Info.Tags ?? Array.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Outdated), nameof(StatusText), nameof(StatusColor), nameof(StatusBackground), nameof(CanUpdate))]
    private bool _downloaded;

    // Update re-downloads a local copy of a repo script; custom scripts aren't in the repo, so it's greyed out for them
    public bool CanUpdate => Downloaded && !IsCustom;

    private bool? _infoOutdated;

    partial void OnDownloadedChanged(bool value) => _infoOutdated = null;

    partial void OnLocalSizeChanged(int value) => _infoOutdated = null;

    // Never hashes on the UI thread: a null cache means "not computed yet" and counts as not outdated
    // until RefreshDownloadStateAsync fills it in off the UI thread.
    public bool Outdated => !IsCustom && Downloaded && (_localSize != Info.Size || (_infoOutdated ?? false));

    public async Task RefreshDownloadStateAsync(bool downloaded, int localSize)
    {
        Info.CachedDownloaded = downloaded;
        Info.CachedLocalSize = localSize;
        Downloaded = downloaded;
        LocalSize = localSize;
        _infoOutdated = IsCustom ? false : await Task.Run(() => Info.Outdated);
        OnPropertyChanged(nameof(Outdated));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(StatusBackground));
    }

    public string FormattedSize => Size > 1024 * 1024
        ? $"{Size / (1024.0 * 1024.0):F1} MB"
        : Size > 1024
            ? $"{Size / 1024.0:F1} KB"
            : $"{Size} B";

    public string StatusText => IsCustom ? "Local" : Outdated ? "Update Available" : Downloaded ? "Downloaded" : "Available";
    public string StatusColor => IsCustom ? "#A78BFA" : Outdated ? "#FBBF24" : Downloaded ? "#34D399" : "#9CA3AF";
    public string StatusBackground => IsCustom ? "#2E1065" : Outdated ? "#451A03" : Downloaded ? "#064E3B" : "#1F2937";

    [RelayCommand]
    private void LoadScript(ScriptInfoViewModel selectedScript)
    {
        if (selectedScript is null || !selectedScript.Downloaded)
            return;

        StrongReferenceMessenger.Default.Send<LoadScriptMessage, int>(new(selectedScript.LocalFile), (int)MessageChannels.ScriptStatus);
    }

    [RelayCommand]
    private void QueueScript(ScriptInfoViewModel selectedScript)
    {
        if (selectedScript is null || !selectedScript.Downloaded)
            return;

        StrongReferenceMessenger.Default.Send<QueueScriptMessage, int>(new(selectedScript.LocalFile), (int)MessageChannels.ScriptStatus);
    }

    [RelayCommand]
    private void StartScript(ScriptInfoViewModel selectedScript)
    {
        if (selectedScript is null || !selectedScript.Downloaded)
            return;

        StrongReferenceMessenger.Default.Send<StartScriptMessage, int>(new(selectedScript.LocalFile), (int)MessageChannels.ScriptStatus);
    }

    public override string ToString()
    {
        return FileName;
    }
}
