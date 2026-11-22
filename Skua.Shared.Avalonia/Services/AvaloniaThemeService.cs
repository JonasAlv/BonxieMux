using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Skua.Shared.Avalonia.Services;

public class AvaloniaThemeService : IThemeService, INotifyPropertyChanged
{
    private static readonly Color DefaultAccentColor = Color.Parse("#0078D4");

    private static readonly Dictionary<string, Color> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Teal"] = Color.Parse("#10B981"),
        ["Blue"] = Color.Parse("#2563EB"),
        ["BlueGray"] = Color.Parse("#607D8B"),
        ["Amber"] = Color.Parse("#D97706"),
        ["Purple"] = Color.Parse("#7C3AED"),
        ["Pink"] = Color.Parse("#DB2777"),
        ["Green"] = Color.Parse("#16A34A"),
        ["Orange"] = Color.Parse("#D8842E"),
        ["Red"] = Color.Parse("#C94747"),
        ["Gray"] = Color.Parse("#6E7685")
    };

    private static bool IsManagerProcess =>
        Assembly.GetEntryAssembly()?.GetName().Name?.Contains("Manager", StringComparison.OrdinalIgnoreCase) ?? false;

    private static string ProcessThemeFilePath => IsManagerProcess
        ? Path.Combine(ClientFileSources.ManagerDIR, "manager-theme.json")
        : Path.Combine(ClientFileSources.SkuaDIR, "app-theme.json");

    private readonly ISettingsService _settingsService;
    private readonly DispatcherTimer _applyThrottleTimer;
    private readonly DispatcherTimer _saveDebounceTimer;

    private bool _isDarkTheme = true;
    private bool _syncThemeWithSystem = true;
    private bool _useSystemAccentColor;
    private bool _useThemeAccentColor = true;
    private bool _useAccentOnMainTitleBar;
    private bool _useAccentOnManagerTitleBar;
    private string _activeThemeName = VsCodeThemeLoader.DarkModernName;
    private string _preferredDarkTheme = VsCodeThemeLoader.DarkModernName;
    private string _preferredLightTheme = VsCodeThemeLoader.LightModernName;
    private VsCodeThemePalette _activePalette = VsCodeThemeLoader.DarkModern;
    private IReadOnlyList<string> _availableThemes;
    private IReadOnlyList<string> _availableDarkThemes;
    private IReadOnlyList<string> _availableLightThemes;
    private Color _primaryColor = DefaultAccentColor;
    private Color _primaryForegroundColor = Colors.White;
    private Color _secondaryColor = DefaultAccentColor;
    private Color _secondaryForegroundColor = Colors.White;
    private Color _selectedColor = DefaultAccentColor;
    private ColorScheme _activeScheme = ColorScheme.Primary;
    private bool _suppressSelectedColorApply;

    private string? _darkAccentColorHex;
    private string? _darkTextOnAccentColorHex;
    private string? _lightAccentColorHex;
    private string? _lightTextOnAccentColorHex;

    public AvaloniaThemeService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        (_availableDarkThemes, _availableLightThemes) = VsCodeThemeLoader.DiscoverThemesByKind();
        _availableThemes = VsCodeThemeLoader.DiscoverAvailableThemeNames();
        _useAccentOnMainTitleBar = ReadExtensionFlag("UseAccentOnMainTitleBar");
        _useAccentOnManagerTitleBar = ReadExtensionFlag("UseAccentOnManagerTitleBar");
        _syncThemeWithSystem = _settingsService.Get("SyncThemeWithSystem", true);
        _useSystemAccentColor = _settingsService.Get("UseSystemAccentColor", false);

        _applyThrottleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        _applyThrottleTimer.Tick += (_, _) =>
        {
            _applyThrottleTimer.Stop();
            FlushColorChange();
        };

        _saveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveDebounceTimer.Tick += (_, _) =>
        {
            _saveDebounceTimer.Stop();
            SaveProcessThemeFile();
        };

        LoadProcessThemeFile();
        _activeThemeName = _isDarkTheme ? _preferredDarkTheme : _preferredLightTheme;
        _activePalette = VsCodeThemeLoader.LoadByName(_activeThemeName);
        SetSelectedColorSilently(GetColorForScheme(_activeScheme));
        HookSystemThemeChanges();

        if (_syncThemeWithSystem)
            SyncWithSystemTheme();

        if (_useSystemAccentColor)
        {
            _useThemeAccentColor = false;
            SyncWithSystemAccentColor();
        }
        else if (_useThemeAccentColor)
        {
            ApplyPaletteAccent(_activePalette, raiseEvents: false);
        }

        if (Application.Current is { } app)
            ThemeResourceApplicator.ApplyVsCodePalette(app, _activePalette);

        SaveProcessThemeFile();
    }

    public event ThemeChangedEventHandler? ThemeChanged;
    public event SchemeChangedEventHandler? SchemeChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (_isDarkTheme == value)
                return;

            SaveModeColors(_isDarkTheme, _primaryColor, _primaryForegroundColor);
            _isDarkTheme = value;
            OnPropertyChanged();

            _activeThemeName = value ? _preferredDarkTheme : _preferredLightTheme;
            _activePalette = VsCodeThemeLoader.LoadByName(_activeThemeName);
            OnPropertyChanged(nameof(ActiveThemeName));

            if (Application.Current is { } app)
                ThemeResourceApplicator.ApplyVsCodePalette(app, _activePalette);

            if (_useThemeAccentColor && !_useSystemAccentColor)
            {
                ApplyPaletteAccent(_activePalette, raiseEvents: false);
            }
            else
            {
                (Color nextAccent, Color nextTextOnAccent) = LoadModeColors(value);
                if (!_useSystemAccentColor)
                {
                    _primaryColor = nextAccent;
                    _secondaryColor = nextAccent;
                }
                _primaryForegroundColor = nextTextOnAccent;
                _secondaryForegroundColor = nextTextOnAccent;
            }
            SaveModeColors(_isDarkTheme, _primaryColor, _primaryForegroundColor);
            SetSelectedColorSilently(GetColorForScheme(_activeScheme));

            ThemeChanged?.Invoke(_primaryColor);
            SchemeChanged?.Invoke(_activeScheme, GetColorForScheme(_activeScheme));
            SaveProcessThemeFile();
        }
    }

    public bool SyncThemeWithSystem
    {
        get => _syncThemeWithSystem;
        set
        {
            if (_syncThemeWithSystem == value)
                return;

            _syncThemeWithSystem = value;
            _settingsService.Set("SyncThemeWithSystem", value);
            try { _settingsService.GetShared().SyncThemeWithSystem = value; } catch { }
            OnPropertyChanged();

            if (_syncThemeWithSystem)
                SyncWithSystemTheme();

            SaveProcessThemeFile();
        }
    }

    public bool UseSystemAccentColor
    {
        get => _useSystemAccentColor;
        set
        {
            if (_useSystemAccentColor == value)
                return;

            _useSystemAccentColor = value;
            _settingsService.Set("UseSystemAccentColor", value);
            try { _settingsService.GetShared().UseSystemAccentColor = value; } catch { }
            OnPropertyChanged();

            if (value && _useThemeAccentColor)
            {
                _useThemeAccentColor = false;
                OnPropertyChanged(nameof(UseThemeAccentColor));
            }

            if (_useSystemAccentColor)
                SyncWithSystemAccentColor();

            SaveProcessThemeFile();
        }
    }

    public bool UseThemeAccentColor
    {
        get => _useThemeAccentColor;
        set
        {
            if (_useThemeAccentColor == value)
                return;

            _useThemeAccentColor = value;
            OnPropertyChanged();

            if (value)
            {
                if (_useSystemAccentColor)
                {
                    _useSystemAccentColor = false;
                    _settingsService.Set("UseSystemAccentColor", false);
                    try { _settingsService.GetShared().UseSystemAccentColor = false; } catch { }
                    OnPropertyChanged(nameof(UseSystemAccentColor));
                }

                ApplyPaletteAccent(_activePalette, raiseEvents: true);
            }

            SaveProcessThemeFile();
        }
    }

    public IReadOnlyList<string> AvailableThemes => _availableThemes;
    public IReadOnlyList<string> AvailableDarkThemes => _availableDarkThemes;
    public IReadOnlyList<string> AvailableLightThemes => _availableLightThemes;
    public VsCodeThemePalette ActivePalette => _activePalette;

    public void RefreshAvailableThemes()
    {
        (_availableDarkThemes, _availableLightThemes) = VsCodeThemeLoader.DiscoverThemesByKind();
        _availableThemes = VsCodeThemeLoader.DiscoverAvailableThemeNames();
        OnPropertyChanged(nameof(AvailableThemes));
        OnPropertyChanged(nameof(AvailableDarkThemes));
        OnPropertyChanged(nameof(AvailableLightThemes));
    }

    public string PreferredDarkTheme
    {
        get => _preferredDarkTheme;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? VsCodeThemeLoader.DarkModernName : value.Trim();
            if (string.Equals(_preferredDarkTheme, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            _preferredDarkTheme = normalized;
            OnPropertyChanged();
            if (_isDarkTheme)
                ApplyActiveThemeByName(normalized);
            SaveProcessThemeFile();
        }
    }

    public string PreferredLightTheme
    {
        get => _preferredLightTheme;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? VsCodeThemeLoader.LightModernName : value.Trim();
            if (string.Equals(_preferredLightTheme, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            _preferredLightTheme = normalized;
            OnPropertyChanged();
            if (!_isDarkTheme)
                ApplyActiveThemeByName(normalized);
            SaveProcessThemeFile();
        }
    }

    public string ActiveThemeName
    {
        get => _activeThemeName;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? VsCodeThemeLoader.DarkModernName : value.Trim();
            if (string.Equals(_activeThemeName, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            ApplyActiveThemeByName(normalized);
            SaveProcessThemeFile();
        }
    }

    private void ApplyActiveThemeByName(string normalized)
    {
        _activeThemeName = normalized;
        _activePalette = VsCodeThemeLoader.LoadByName(normalized);
        OnPropertyChanged(nameof(ActiveThemeName));

        if (Application.Current is { } app)
            ThemeResourceApplicator.ApplyVsCodePalette(app, _activePalette);

        if (_isDarkTheme != _activePalette.IsDark)
        {
            _isDarkTheme = _activePalette.IsDark;
            OnPropertyChanged(nameof(IsDarkTheme));
        }

        if (_useThemeAccentColor && !_useSystemAccentColor)
            ApplyPaletteAccent(_activePalette, raiseEvents: true);
        else
            ThemeChanged?.Invoke(_primaryColor);
    }

    public bool UseAccentOnMainTitleBar
    {
        get => _useAccentOnMainTitleBar;
        set
        {
            if (_useAccentOnMainTitleBar == value)
                return;

            _useAccentOnMainTitleBar = value;
            PersistExtensionFlag("UseAccentOnMainTitleBar", value);
            OnPropertyChanged();
            ThemeChanged?.Invoke(_primaryColor);
            SaveProcessThemeFile();
        }
    }

    public bool UseAccentOnManagerTitleBar
    {
        get => _useAccentOnManagerTitleBar;
        set
        {
            if (_useAccentOnManagerTitleBar == value)
                return;

            _useAccentOnManagerTitleBar = value;
            PersistExtensionFlag("UseAccentOnManagerTitleBar", value);
            OnPropertyChanged();
            ThemeChanged?.Invoke(_primaryColor);
            SaveProcessThemeFile();
        }
    }

    public ColorScheme ActiveScheme
    {
        get => _activeScheme;
        set
        {
            if (_activeScheme == value)
                return;

            _activeScheme = value;
            OnPropertyChanged();
            SetSelectedColorSilently(GetColorForScheme(value));
        }
    }

    public object? SelectedColor
    {
        get => _selectedColor;
        set
        {
            Color color = ResolveAccentColor(value);
            if (_selectedColor == color)
                return;

            _selectedColor = color;
            OnPropertyChanged();
            if (!_suppressSelectedColorApply)
                ApplyColorToActiveScheme(color, immediate: false);
        }
    }

    public Color PrimaryColor => _primaryColor;
    public Color PrimaryForegroundColor => _primaryForegroundColor;
    public Color SecondaryColor => _secondaryColor;
    public Color SecondaryForegroundColor => _secondaryForegroundColor;

    public string PrimaryHex => ToHex(_primaryColor);
    public string ForegroundHex => ToHex(_primaryForegroundColor);
    public string SecondaryHex => ToHex(_secondaryColor);
    public string SecondaryForegroundHex => ToHex(_secondaryForegroundColor);

    public void ApplyBaseTheme(bool isDark) => IsDarkTheme = isDark;

    public void ChangeCustomColor(object? obj)
    {
        Color color = ResolveAccentColor(obj);
        SetSelectedColorSilently(color);
        ApplyColorToActiveScheme(color, immediate: true);
    }

    public void ChangeScheme(ColorScheme scheme)
    {
        ActiveScheme = scheme;
        SetSelectedColorSilently(GetColorForScheme(scheme));
    }

    public void SetCurrentTheme(object? theme)
    {
        if (theme is null)
            return;

        Color resolved = ResolveAccentColor(theme);
        _primaryColor = resolved;
        _secondaryColor = resolved;
        SaveModeColors(_isDarkTheme, _primaryColor, _primaryForegroundColor);
        SetSelectedColorSilently(GetColorForScheme(_activeScheme));
        ThemeChanged?.Invoke(_primaryColor);
        SchemeChanged?.Invoke(_activeScheme, GetColorForScheme(_activeScheme));
        SaveProcessThemeFile();
    }

    public void ApplyExternalThemeSnapshot(bool isDark, Color primary, Color foreground, bool useAccentOnMainTitleBar, string? activeThemeName = null)
    {
        _useAccentOnMainTitleBar = useAccentOnMainTitleBar;
        _isDarkTheme = isDark;
        if (!string.IsNullOrWhiteSpace(activeThemeName))
            _activeThemeName = activeThemeName;
        else
            _activeThemeName = isDark ? _preferredDarkTheme : _preferredLightTheme;

        _activePalette = VsCodeThemeLoader.LoadByName(_activeThemeName);
        if (Application.Current is { } app)
            ThemeResourceApplicator.ApplyVsCodePalette(app, _activePalette);

        _primaryColor = primary;
        _secondaryColor = primary;
        _primaryForegroundColor = foreground;
        _secondaryForegroundColor = foreground;
        SetSelectedColorSilently(GetColorForScheme(_activeScheme));
        OnPropertyChanged(nameof(UseAccentOnMainTitleBar));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(ActiveThemeName));
        OnPropertyChanged(nameof(SelectedColor));
        ThemeChanged?.Invoke(_primaryColor);
        SchemeChanged?.Invoke(ColorScheme.Primary, _primaryColor);
    }

    public void SyncWithSystemTheme()
    {
        if (!_syncThemeWithSystem || Application.Current?.PlatformSettings is not { } platformSettings)
            return;

        ApplySystemThemeVariant(platformSettings.GetColorValues().ThemeVariant);
    }

    public void SyncWithSystemAccentColor(Color? knownAccent = null)
    {
        if (!_useSystemAccentColor)
            return;

        Color? systemAccent = knownAccent is { A: > 0 } ? knownAccent : ReadSystemAccentColor();
        if (systemAccent is not { A: > 0 } accent)
            return;

        Color opaque = Color.FromArgb(255, accent.R, accent.G, accent.B);
        Color fg = VsCodeThemeLoader.GetContrastForeground(opaque);
        if (_primaryColor == opaque && _secondaryColor == opaque && _primaryForegroundColor == fg)
            return;

        _primaryColor = opaque;
        _secondaryColor = opaque;
        _primaryForegroundColor = fg;
        _secondaryForegroundColor = fg;
        if (_activeScheme is ColorScheme.Primary or ColorScheme.Secondary)
            SetSelectedColorSilently(opaque);

        SaveModeColors(_isDarkTheme, _primaryColor, _primaryForegroundColor);
        ThemeChanged?.Invoke(_primaryColor);
        SchemeChanged?.Invoke(_activeScheme, GetColorForScheme(_activeScheme));
        SaveProcessThemeFile();
    }

    private void ApplyPaletteAccent(VsCodeThemePalette palette, bool raiseEvents)
    {
        _primaryColor = palette.Accent;
        _secondaryColor = palette.Accent;
        _primaryForegroundColor = palette.AccentForeground;
        _secondaryForegroundColor = palette.AccentForeground;
        if (_activeScheme is ColorScheme.Primary or ColorScheme.Secondary)
            SetSelectedColorSilently(palette.Accent);
        else
            SetSelectedColorSilently(palette.AccentForeground);

        SaveModeColors(_isDarkTheme, _primaryColor, _primaryForegroundColor);
        if (raiseEvents)
        {
            ThemeChanged?.Invoke(_primaryColor);
            SchemeChanged?.Invoke(_activeScheme, GetColorForScheme(_activeScheme));
        }
    }

    public void FlushPendingSave()
    {
        if (_applyThrottleTimer.IsEnabled)
        {
            _applyThrottleTimer.Stop();
            FlushColorChange();
        }
        if (_saveDebounceTimer.IsEnabled)
        {
            _saveDebounceTimer.Stop();
            SaveProcessThemeFile();
        }
    }

    private void ApplySystemThemeVariant(PlatformThemeVariant variant)
    {
        bool isDark = variant != PlatformThemeVariant.Light;
        if (IsDarkTheme != isDark)
            ApplyBaseTheme(isDark);
    }

    private void HookSystemThemeChanges()
    {
        if (Application.Current?.PlatformSettings is not { } settings)
            return;

        settings.ColorValuesChanged += (_, values) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (SyncThemeWithSystem)
                    ApplySystemThemeVariant(values.ThemeVariant);
                if (UseSystemAccentColor)
                    SyncWithSystemAccentColor(values.AccentColor1);
            });
        };
    }

    private void ApplyColorToActiveScheme(Color color, bool immediate)
    {
        if (_activeScheme is ColorScheme.PrimaryForeground or ColorScheme.SecondaryForeground)
        {
            _primaryForegroundColor = color;
            _secondaryForegroundColor = color;
        }
        else
        {
            if (_useSystemAccentColor)
            {
                _useSystemAccentColor = false;
                _settingsService.Set("UseSystemAccentColor", false);
                try { _settingsService.GetShared().UseSystemAccentColor = false; } catch { }
                OnPropertyChanged(nameof(UseSystemAccentColor));
            }
            if (_useThemeAccentColor)
            {
                _useThemeAccentColor = false;
                OnPropertyChanged(nameof(UseThemeAccentColor));
            }

            _primaryColor = color;
            _secondaryColor = color;
            Color contrastFg = VsCodeThemeLoader.GetContrastForeground(color);
            _primaryForegroundColor = contrastFg;
            _secondaryForegroundColor = contrastFg;
        }

        SaveModeColors(_isDarkTheme, _primaryColor, _primaryForegroundColor);

        if (immediate)
        {
            _applyThrottleTimer.Stop();
            _saveDebounceTimer.Stop();
            FlushColorChange();
            SaveProcessThemeFile();
        }
        else
        {
            if (!_applyThrottleTimer.IsEnabled)
                _applyThrottleTimer.Start();

            _saveDebounceTimer.Stop();
            _saveDebounceTimer.Start();
        }
    }

    private void FlushColorChange()
    {
        ThemeChanged?.Invoke(_primaryColor);
        SchemeChanged?.Invoke(_activeScheme, GetColorForScheme(_activeScheme));
    }

    private void SaveModeColors(bool isDark, Color accent, Color textOnAccent)
    {
        string accentHex = ToHex(accent);
        string textHex = ToHex(textOnAccent);
        if (isDark)
        {
            _darkAccentColorHex = accentHex;
            _darkTextOnAccentColorHex = textHex;
        }
        else
        {
            _lightAccentColorHex = accentHex;
            _lightTextOnAccentColorHex = textHex;
        }

        if (!IsManagerProcess)
        {
            string prefix = isDark ? "Dark" : "Light";
            _settingsService.Set($"{prefix}AccentColor", accentHex);
            _settingsService.Set($"{prefix}TextOnAccentColor", textHex);
        }
    }

    private (Color Accent, Color TextOnAccent) LoadModeColors(bool isDark)
    {
        string? accentHex = isDark ? _darkAccentColorHex : _lightAccentColorHex;
        string? textHex = isDark ? _darkTextOnAccentColorHex : _lightTextOnAccentColorHex;

        if (!IsManagerProcess)
        {
            string prefix = isDark ? "Dark" : "Light";
            accentHex ??= _settingsService.Get<string?>($"{prefix}AccentColor", null);
            textHex ??= _settingsService.Get<string?>($"{prefix}TextOnAccentColor", null);
        }

        Color accent = !string.IsNullOrWhiteSpace(accentHex) ? ResolveAccentColor(accentHex) : _primaryColor;
        Color textOnAccent = !string.IsNullOrWhiteSpace(textHex) ? ResolveAccentColor(textHex) : _primaryForegroundColor;
        return (accent, textOnAccent);
    }

    private Color GetColorForScheme(ColorScheme scheme) => scheme switch
    {
        ColorScheme.Primary => _primaryColor,
        ColorScheme.Secondary => _secondaryColor,
        ColorScheme.PrimaryForeground => _primaryForegroundColor,
        ColorScheme.SecondaryForeground => _secondaryForegroundColor,
        _ => _primaryColor
    };

    private void SetSelectedColorSilently(Color color)
    {
        if (_selectedColor == color)
            return;

        _suppressSelectedColorApply = true;
        try
        {
            _selectedColor = color;
            OnPropertyChanged(nameof(SelectedColor));
        }
        finally
        {
            _suppressSelectedColorApply = false;
        }
    }

    private static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color ResolveAccentColor(object? value)
    {
        if (value is Color c)
            return c;

        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            string trimmed = s.Trim();
            if (Color.TryParse(trimmed, out Color parsed))
                return parsed;
            if (NamedColors.TryGetValue(trimmed, out Color named))
                return named;
        }

        return DefaultAccentColor;
    }

    private static Color? ReadSystemAccentColor()
    {
        try
        {
            if (Application.Current?.PlatformSettings is { } platformSettings)
            {
                Color accent = platformSettings.GetColorValues().AccentColor1;
                if (accent.A > 0)
                    return accent;
            }
        }
        catch { }

        return OperatingSystem.IsLinux() ? ReadLinuxPortalAccentColor() : null;
    }

    private static Color? ReadLinuxPortalAccentColor()
    {
        try
        {
            using Process? proc = Process.Start(new ProcessStartInfo
            {
                FileName = "gdbus",
                Arguments = "call --session --dest org.freedesktop.portal.Desktop --object-path /org/freedesktop/portal/desktop --method org.freedesktop.portal.Settings.Read org.freedesktop.appearance accent-color",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc is null)
                return null;

            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(1000);
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            Match match = Regex.Match(output, @"\(([0-9.]+),\s*([0-9.]+),\s*([0-9.]+)\)");
            if (match.Success &&
                double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) &&
                double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double g) &&
                double.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
            {
                return Color.FromRgb(
                    (byte)Math.Clamp(Math.Round(r * 255.0), 0, 255),
                    (byte)Math.Clamp(Math.Round(g * 255.0), 0, 255),
                    (byte)Math.Clamp(Math.Round(b * 255.0), 0, 255));
            }
        }
        catch { }

        return null;
    }

    private bool ReadExtensionFlag(string key)
    {
        try
        {
            Dictionary<string, object>? ext = _settingsService.GetShared().ExtensionData;
            if (ext is not null && ext.TryGetValue(key, out object? val) && bool.TryParse(val?.ToString(), out bool b))
                return b;
        }
        catch { }
        return false;
    }

    private void PersistExtensionFlag(string key, bool enabled)
    {
        try
        {
            SharedSettings shared = _settingsService.GetShared();
            shared.ExtensionData ??= new Dictionary<string, object>();
            shared.ExtensionData[key] = enabled;
            _settingsService.Set("TitleBarArtworkPath", shared.TitleBarArtworkPath);
        }
        catch { }
    }

    private sealed class ProcessThemeFileState
    {
        public bool IsDarkTheme { get; set; }
        public bool SyncThemeWithSystem { get; set; }
        public bool UseSystemAccentColor { get; set; }
        public bool? UseThemeAccentColor { get; set; }
        public string? ActiveThemeName { get; set; }
        public string? PreferredDarkTheme { get; set; }
        public string? PreferredLightTheme { get; set; }
        public bool UseAccentOnMainTitleBar { get; set; }
        public bool UseAccentOnManagerTitleBar { get; set; }
        public string? PrimaryColor { get; set; }
        public string? TextOnAccentColor { get; set; }
        public string? DarkAccentColor { get; set; }
        public string? DarkTextOnAccentColor { get; set; }
        public string? LightAccentColor { get; set; }
        public string? LightTextOnAccentColor { get; set; }
    }

    private void LoadProcessThemeFile()
    {
        try
        {
            if (!File.Exists(ProcessThemeFilePath))
                return;

            ProcessThemeFileState? state = JsonSerializer.Deserialize<ProcessThemeFileState>(File.ReadAllText(ProcessThemeFilePath));
            if (state is null)
                return;

            _syncThemeWithSystem = state.SyncThemeWithSystem;
            _useSystemAccentColor = state.UseSystemAccentColor;
            _useThemeAccentColor = state.UseThemeAccentColor ?? !_useSystemAccentColor;
            if (!string.IsNullOrWhiteSpace(state.ActiveThemeName))
                _activeThemeName = state.ActiveThemeName;
            if (!string.IsNullOrWhiteSpace(state.PreferredDarkTheme))
                _preferredDarkTheme = state.PreferredDarkTheme;
            if (!string.IsNullOrWhiteSpace(state.PreferredLightTheme))
                _preferredLightTheme = state.PreferredLightTheme;
            _useAccentOnMainTitleBar = state.UseAccentOnMainTitleBar;
            _useAccentOnManagerTitleBar = state.UseAccentOnManagerTitleBar;
            _isDarkTheme = state.IsDarkTheme;
            _darkAccentColorHex = state.DarkAccentColor;
            _darkTextOnAccentColorHex = state.DarkTextOnAccentColor;
            _lightAccentColorHex = state.LightAccentColor;
            _lightTextOnAccentColorHex = state.LightTextOnAccentColor;

            if (!string.IsNullOrWhiteSpace(state.PrimaryColor))
            {
                Color accent = ResolveAccentColor(state.PrimaryColor);
                _primaryColor = accent;
                _secondaryColor = accent;
            }
            if (!string.IsNullOrWhiteSpace(state.TextOnAccentColor))
            {
                Color fg = ResolveAccentColor(state.TextOnAccentColor);
                _primaryForegroundColor = fg;
                _secondaryForegroundColor = fg;
            }
        }
        catch { }
    }

    private void SaveProcessThemeFile()
    {
        try
        {
            string? dir = Path.GetDirectoryName(ProcessThemeFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            ProcessThemeFileState state = new()
            {
                IsDarkTheme = _isDarkTheme,
                SyncThemeWithSystem = _syncThemeWithSystem,
                UseSystemAccentColor = _useSystemAccentColor,
                UseThemeAccentColor = _useThemeAccentColor,
                ActiveThemeName = _activeThemeName,
                PreferredDarkTheme = _preferredDarkTheme,
                PreferredLightTheme = _preferredLightTheme,
                UseAccentOnMainTitleBar = _useAccentOnMainTitleBar,
                UseAccentOnManagerTitleBar = _useAccentOnManagerTitleBar,
                PrimaryColor = PrimaryHex,
                TextOnAccentColor = ForegroundHex,
                DarkAccentColor = _darkAccentColorHex,
                DarkTextOnAccentColor = _darkTextOnAccentColorHex,
                LightAccentColor = _lightAccentColorHex,
                LightTextOnAccentColor = _lightTextOnAccentColorHex
            };

            string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            string tmpPath = ProcessThemeFilePath + $".{Environment.ProcessId}.tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, ProcessThemeFilePath, overwrite: true);
        }
        catch { }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
