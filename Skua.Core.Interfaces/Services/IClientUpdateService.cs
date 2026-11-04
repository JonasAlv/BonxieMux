namespace Skua.Core.Interfaces;

/// <summary>
/// Defines the Linux AppImage client update workflow.
/// </summary>
public interface IClientUpdateService
{
    string? LatestVersion { get; }

    bool UpdateAvailable { get; }

    bool UsingNightlyChannel { get; }

    Task CheckForUpdateAsync();

    Task DownloadUpdateAsync(IProgress<string>? progress);
}
