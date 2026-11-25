using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Skua.Core.Interfaces;
using Skua.Shared.Avalonia.Services;
using Skua.Shared.Avalonia.Theming;

namespace ButlerV5;

/// <summary>Native Avalonia settings and live-log window.</summary>
public sealed partial class SettingsWindow : Window
{
    private readonly ButlerV5Plugin _plugin;
    private readonly DispatcherTimer _logTimer;
    private readonly List<LogLine> _renderedLogs = new();
    private readonly List<TextBlock> _renderedLogControls = new();
    private Border? _selectedRow;

    public SettingsWindow()
    {
        InitializeComponent();
        _plugin = null!;
        _logTimer = new DispatcherTimer();
    }

    public SettingsWindow(ButlerV5Plugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();
        WindowPlacementHelper.CenterOnActiveScreen(this);
        ShellWindowHelper.ConfigureFramelessWindow(this);

        LogTitle.Text = $"Logs - {_plugin.OwnUsername ?? "this account"}";
        Version? version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        MetaText.Text = $"Author: l0newolf12\nVersion: {(version == null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}")}";

        IEnumerable<IOption> options = _plugin.OptionContainer?.Options ?? Enumerable.Empty<IOption>();
        bool first = true;
        foreach (IOption option in options)
        {
            if (!first)
                SettingsPanel.Children.Add(MakeSeparator());
            first = false;
            SettingsPanel.Children.Add(BuildSettingRow(option));
        }

        RefreshLog();
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _logTimer.Tick += (_, _) => RefreshLog();
        _logTimer.Start();
        Closed += (_, _) => _logTimer.Stop();
    }

    private Border BuildSettingRow(IOption option)
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        TextBlock label = new()
        {
            Text = option.DisplayName,
            FontSize = UiTokens.GetDouble(UiTokens.UiFontBody, 12),
            Foreground = UiTokens.GetBrush(UiTokens.SkuaTextBrush, Brushes.White),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 10, 0),
        };
        grid.Children.Add(label);

        Control control = BuildControl(option);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.GotFocus += (_, _) => SelectOption(option);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

        Border row = new()
        {
            Child = grid,
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 0, 0, 2),
            CornerRadius = UiTokens.GetCornerRadius(UiTokens.UiRadiusControl, new CornerRadius(4)),
            Background = Brushes.Transparent,
        };
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
                SelectOption(option, row);
        };
        return row;
    }

    private static Border MakeSeparator() => new()
    {
        Height = 1,
        Margin = new Thickness(4, 1),
        Background = UiTokens.GetBrush(UiTokens.SkuaBorderBrush, Brushes.Gray),
    };

    private Control BuildControl(IOption option)
    {
        IOptionContainer container = _plugin.OptionContainer!;
        string raw = SafeGetDirect(container, option);

        if (option.Type == typeof(bool))
        {
            bool restoring = false;
            CheckBox checkBox = new()
            {
                IsChecked = raw.Equals("True", StringComparison.OrdinalIgnoreCase) || raw == "1",
            };
            checkBox.IsCheckedChanged += (_, _) =>
            {
                if (restoring)
                    return;
                if (checkBox.IsChecked == true)
                {
                    _plugin.SaveSetting(option.Name, true);
                }
                else if (option.Name != "enabled" || _plugin.ConfirmDisablePlugin())
                {
                    _plugin.SaveSetting(option.Name, false);
                }
                else
                {
                    restoring = true;
                    checkBox.IsChecked = true;
                    restoring = false;
                }
            };
            return checkBox;
        }

        if (option.Type.IsEnum)
        {
            List<object> values = Enum.GetValues(option.Type).Cast<object>().ToList();
            ComboBox combo = new()
            {
                MinWidth = 125,
                ItemsSource = values,
                SelectedItem = values.FirstOrDefault(value => value.Equals(TryParseEnum(option.Type, raw) ?? option.DefaultValue))
                    ?? values.FirstOrDefault(),
            };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem != null)
                    _plugin.SaveSetting(option.Name, combo.SelectedItem);
            };
            return combo;
        }

        bool isInt = option.Type == typeof(int);
        TextBox textBox = new()
        {
            Text = raw,
            MinWidth = isInt ? 85 : 160,
            MaxWidth = 240,
        };
        textBox.LostFocus += (_, _) =>
        {
            string value = textBox.Text ?? "";
            if (isInt)
            {
                if (int.TryParse(value.Trim(), out int number))
                    _plugin.SaveSetting(option.Name, number);
                else
                    textBox.Text = SafeGetDirect(container, option);
            }
            else
            {
                _plugin.SaveSetting(option.Name, value);
            }
        };
        return textBox;
    }

    private void SelectOption(IOption option, Border? row = null)
    {
        DescTitle.Text = option.DisplayName;
        DescBody.Text = string.IsNullOrWhiteSpace(option.Description) ? "(no description)" : option.Description;
        if (row == null)
            return;
        if (_selectedRow != null)
            _selectedRow.Background = Brushes.Transparent;
        row.Background = UiTokens.GetBrush(UiTokens.SkuaBorderBrush, Brushes.Gray);
        _selectedRow = row;
    }

    private void RefreshLog()
    {
        try
        {
            List<LogLine> snapshot = DebugLog.Snapshot();
            if (snapshot.Count == _renderedLogs.Count &&
                (snapshot.Count == 0 || (snapshot[^1].Seq == _renderedLogs[^1].Seq && snapshot[^1].Text == _renderedLogs[^1].Text)))
                return;

            bool canAppend = _renderedLogs.Count <= snapshot.Count;
            for (int index = 0; canAppend && index < _renderedLogs.Count; index++)
                canAppend = _renderedLogs[index].Seq == snapshot[index].Seq;

            if (!canAppend)
            {
                _renderedLogs.Clear();
                _renderedLogControls.Clear();
                LogRows.Children.Clear();
            }

            int sharedCount = _renderedLogs.Count;
            for (int index = 0; index < sharedCount; index++)
            {
                if (_renderedLogs[index].Text == snapshot[index].Text)
                    continue;
                _renderedLogs[index].Text = snapshot[index].Text;
                _renderedLogControls[index].Text = snapshot[index].Text;
            }

            for (int index = sharedCount; index < snapshot.Count; index++)
            {
                LogLine copy = snapshot[index].Copy();
                TextBlock line = new()
                {
                    Text = copy.Text,
                    Foreground = LogBrush(copy.Level),
                    FontFamily = UiTokens.GetFontFamily(UiTokens.UiFontMono, FontFamily.Default),
                    FontSize = UiTokens.GetDouble(UiTokens.UiFontMicro, 11),
                    TextWrapping = TextWrapping.NoWrap,
                };
                _renderedLogs.Add(copy);
                _renderedLogControls.Add(line);
                LogRows.Children.Add(line);
            }

            if (FollowTailCheck.IsChecked == true && LogRows.Children.Count > 0)
                LogRows.Children[^1].BringIntoView();
        }
        catch
        {
        }
    }

    private static IBrush LogBrush(LogLevel level) => level switch
    {
        LogLevel.Success => UiTokens.GetBrush(UiTokens.SkuaSuccessBrush, Brushes.SeaGreen),
        LogLevel.Warning => UiTokens.GetBrush(UiTokens.SkuaWarningBrush, Brushes.Goldenrod),
        LogLevel.Error => UiTokens.GetBrush(UiTokens.SkuaDangerBrush, Brushes.IndianRed),
        _ => UiTokens.GetBrush(UiTokens.SkuaMutedTextBrush, Brushes.Gray),
    };

    private async void CopyLog_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            string text = string.Join(Environment.NewLine, _renderedLogs.Select(entry => entry.Text));
            if (text.Length == 0)
                return;
            IClipboard? clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
                throw new InvalidOperationException("The clipboard is not available.");
            await clipboard.SetTextAsync(text);
            DebugLog.Log("UI", $"copied {_renderedLogs.Count} log lines to clipboard", LogLevel.Success);
        }
        catch (Exception ex)
        {
            DebugLog.Log("UI", $"copy failed: {ex.Message}", LogLevel.Error);
        }
    }

    private void SaveLog_Click(object? sender, RoutedEventArgs e)
    {
        string? path = DebugLog.SaveExport(_plugin.OwnUsername);
        DebugLog.Log("UI", path != null ? $"saved log to {Path.GetFileName(path)}" : "save log failed",
            path != null ? LogLevel.Success : LogLevel.Error);
    }

    private void ClearLog_Click(object? sender, RoutedEventArgs e)
    {
        DebugLog.ClearMemory();
        _renderedLogs.Clear();
        _renderedLogControls.Clear();
        LogRows.Children.Clear();
    }

    private void OpenLogFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DebugLog.LogDir);
            Process.Start(new ProcessStartInfo { FileName = DebugLog.LogDir, UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            DebugLog.Log("UI", $"open folder failed: {ex.Message}", LogLevel.Error);
        }
    }

    private static string SafeGetDirect(IOptionContainer container, IOption option)
    {
        try { return container.GetDirect(option) ?? option.DefaultValue?.ToString() ?? ""; }
        catch { return option.DefaultValue?.ToString() ?? ""; }
    }

    private static object? TryParseEnum(Type type, string raw)
    {
        try { return Enum.Parse(type, raw, ignoreCase: true); }
        catch { return null; }
    }
}
