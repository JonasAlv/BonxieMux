using Skua.Core.Models;

namespace Skua.Core.Interfaces;

public delegate void ThemeChangedEventHandler(object? theme);

public delegate void SchemeChangedEventHandler(ColorScheme scheme, object? color);

public interface IThemeService
{
    event ThemeChangedEventHandler ThemeChanged;

    event SchemeChangedEventHandler SchemeChanged;

    bool IsDarkTheme { get; set; }
    bool SyncThemeWithSystem { get; set; }
    bool UseSystemAccentColor { get; set; }
    object? SelectedColor { get; set; }
    ColorScheme ActiveScheme { get; set; }

    void ApplyBaseTheme(bool isDark);

    void ChangeCustomColor(object? obj);

    void ChangeScheme(ColorScheme scheme);

    void SetCurrentTheme(object? theme);
}
