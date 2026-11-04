using CommunityToolkit.Mvvm.ComponentModel;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.Core.Services;

public class BackgroundThemeService : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private string[] defaultBackgrounds = {
        "Black", "Generic2.swf", "Skyguard.swf", "Kezeroth.swf", "Mirror.swf", "DageScorn.swf", "ravenloss2.swf"
    };

    public BackgroundThemeService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        EnsureThemesFolderExists();
    }

    private void EnsureThemesFolderExists()
    {
        if (!Directory.Exists(ClientFileSources.SkuaThemesDIR))
        {
            Directory.CreateDirectory(ClientFileSources.SkuaThemesDIR);
        }
    }

    public List<string> GetAvailableBackgrounds()
    {
        List<string> backgrounds = new(defaultBackgrounds);

        if (Directory.Exists(ClientFileSources.SkuaThemesDIR))
        {
            string[] swfPaths = Directory.GetFiles(ClientFileSources.SkuaThemesDIR, "*.swf");
            foreach (string path in swfPaths)
            {
                string? fileName = Path.GetFileName(path);
                if (fileName != null)
                    backgrounds.Add(fileName);
            }
        }

        return backgrounds.Distinct().ToList();
    }

    public string CurrentBackground
    {
        get
        {
            string sBG = _settingsService.Get<string>("sBG", "Generic2.swf");
            string? customPath = _settingsService.Get<string?>("CustomBackgroundPath", null);

            return !string.IsNullOrEmpty(customPath) ? Path.GetFileName(customPath.Replace("file:///", "").Replace("/", "\\")) : sBG;
        }
        set
        {
            UpdateBackgroundSettings(value);
            OnPropertyChanged();
        }
    }

    public string sBG => _settingsService.Get<string>("sBG", "Generic2.swf");

    public string? CustomBackgroundPath => _settingsService.Get<string?>("CustomBackgroundPath", null);

    public bool IsLocalBackgroundFile(string backgroundName)
    {
        string localPath = Path.Combine(ClientFileSources.SkuaThemesDIR, backgroundName);
        return File.Exists(localPath);
    }

    private bool IsDefaultAQWBackground(string background)
    {
        return defaultBackgrounds.Contains(background);
    }

    private void UpdateBackgroundSettings(string backgroundName)
    {
        if (IsLocalBackgroundFile(backgroundName) && !IsDefaultAQWBackground(backgroundName))
        {
            _settingsService.Set("sBG", "hideme.swf");
            string localPath = Path.Combine(ClientFileSources.SkuaThemesDIR, backgroundName);
            _settingsService.Set<string>("CustomBackgroundPath", $"file:///{localPath.Replace('\\', '/')}");
        }
        else
        {
            _settingsService.Set("sBG", backgroundName);
            _settingsService.Set<string?>("CustomBackgroundPath", null);
        }
    }
}
