using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.App.Avalonia.Services;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Shared.Avalonia.ViewModels;
using Skua.Shared.Avalonia.ViewModels.Dialogs;
using Skua.Shared.Avalonia.ViewModels.ScriptRepo;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Skua.App.Avalonia.Views;

public partial class TabbedHostWindow
{
    private void OnArmySchedulerStart(ScriptSchedulerViewModel.ArmySchedulerMessage m)
    {
        m.Handled = true;
        try
        {
            var dialogService = Ioc.Default.GetRequiredService<IDialogService>();

            int notLoggedInCount = 0;
            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero)
                {
                    IntPtr res = SendChildMessage(info, (uint)SkuaIpcMessages.CheckLogin, IntPtr.Zero, IntPtr.Zero);
                    if (res.ToInt32() == 0)
                        notLoggedInCount++;
                }
            }

            if (notLoggedInCount > 0)
            {
                dialogService.ShowMessageBox($"Cannot start Army Scheduler! {notLoggedInCount} account(s) are not logged in. Please log them in first.", "Army Scheduler Error");
                return;
            }

            string tempFile = System.IO.Path.Combine(ClientFileSources.TabsDIR, "skua_global_playlist.json");
            var data = m.Queue.Select(x => new ScriptSchedulerViewModel.SavedScriptItem { Path = x.Path, Id = x.Id, Name = x.Name }).ToList();
            System.IO.File.WriteAllText(tempFile, System.Text.Json.JsonSerializer.Serialize(data));
            
            foreach (var info in _tabs.Values)
                if (info.ChildHwnd != IntPtr.Zero)
                    PostChildMessage(info.ChildHwnd, SkuaIpcMessages.ArmyScheduler, IntPtr.Zero, IntPtr.Zero);
                    
            dialogService.ShowMessageBox("Army Scheduler payload has been broadcasted. All active accounts will now seamlessly rebuild their queues and begin executing the playlist.", "Army Scheduler Started");
        }
        catch { }
    }

    private void OnArmySchedulerStop(ScriptSchedulerViewModel.ArmySchedulerStopMessage m)
    {
        m.Handled = true;
        try
        {
            foreach (var info in _tabs.Values)
                if (info.ChildHwnd != IntPtr.Zero)
                    PostChildMessage(info.ChildHwnd, SkuaIpcMessages.ArmySchedulerStop, IntPtr.Zero, IntPtr.Zero);
                    
            var dialogService = Ioc.Default.GetRequiredService<IDialogService>();
            dialogService.ShowMessageBox("Stop signal has been successfully broadcasted. All accounts are now halting their schedulers.", "Army Scheduler Stopped");
        }
        catch { }
    }

    private void MenuItem_ArmyScheduler_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var windowService = Ioc.Default.GetRequiredService<IWindowService>();
            windowService.RegisterManagedWindow("Scheduler", Ioc.Default.GetRequiredService<ScriptSchedulerViewModel>());
            windowService.RegisterManagedWindow("Script Repo", Ioc.Default.GetRequiredService<ScriptRepoViewModel>());
            windowService.ShowManagedWindow("Scheduler");
        }
        catch (Exception ex)
        {
            Ioc.Default.GetRequiredService<IDialogService>().ShowMessageBox(ex.ToString(), "Error Opening Scheduler");
        }
    }

    private async void MenuItem_StartAllScripts_Click(object? sender, RoutedEventArgs e)
    {
        if (MenuStartScripts != null) MenuStartScripts.IsEnabled = false;
        if (MenuStopScripts != null) MenuStopScripts.IsEnabled = false;
        
        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero)
                PostChildMessage(info.ChildHwnd, SkuaIpcMessages.SetOption, new IntPtr(99), new IntPtr(1));
        
        await Task.Delay(2000);
        
        if (MenuStartScripts != null) MenuStartScripts.IsEnabled = true;
        if (MenuStopScripts != null) MenuStopScripts.IsEnabled = true;
    }

    private async void MenuItem_StopAllScripts_Click(object? sender, RoutedEventArgs e)
    {
        if (MenuStartScripts != null) MenuStartScripts.IsEnabled = false;
        if (MenuStopScripts != null) MenuStopScripts.IsEnabled = false;
        
        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero)
                PostChildMessage(info.ChildHwnd, SkuaIpcMessages.SetOption, new IntPtr(99), new IntPtr(0));
        
        await Task.Delay(4000);
        
        if (MenuStartScripts != null) MenuStartScripts.IsEnabled = true;
        if (MenuStopScripts != null) MenuStopScripts.IsEnabled = true;
    }

    private async void MenuItem_StartAutoAttackAll_Click(object? sender, RoutedEventArgs e)
    {
        if (MenuStartAutoAttack != null) MenuStartAutoAttack.IsEnabled = false;
        if (MenuStopAutoAttack != null) MenuStopAutoAttack.IsEnabled = false;

        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero)
                PostChildMessage(info.ChildHwnd, SkuaIpcMessages.AutoAttack, new IntPtr(1), IntPtr.Zero);

        await Task.Delay(2000);

        if (MenuStartAutoAttack != null) MenuStartAutoAttack.IsEnabled = true;
        if (MenuStopAutoAttack != null) MenuStopAutoAttack.IsEnabled = true;
    }

    private async void MenuItem_StopAutoAttackAll_Click(object? sender, RoutedEventArgs e)
    {
        if (MenuStartAutoAttack != null) MenuStartAutoAttack.IsEnabled = false;
        if (MenuStopAutoAttack != null) MenuStopAutoAttack.IsEnabled = false;

        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero)
                PostChildMessage(info.ChildHwnd, SkuaIpcMessages.AutoAttack, new IntPtr(0), IntPtr.Zero);

        await Task.Delay(2000);

        if (MenuStartAutoAttack != null) MenuStartAutoAttack.IsEnabled = true;
        if (MenuStopAutoAttack != null) MenuStopAutoAttack.IsEnabled = true;
    }

    private void BroadcastScriptAction(string scriptPath, int msg)
    {
        try
        {
            string tempFile = System.IO.Path.Combine(ClientFileSources.TabsDIR, "skua_global_script.txt");
            System.IO.File.WriteAllText(tempFile, scriptPath);

            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero)
                {
                    PostChildMessage(info.ChildHwnd, (uint)msg, IntPtr.Zero, IntPtr.Zero);
                }
            }
        }
        catch { }
    }

    private void MenuItem_LoadScriptAll_Click(object? sender, RoutedEventArgs e)
    {
        var windowService = Ioc.Default.GetRequiredService<IWindowService>();
        windowService.RegisterManagedWindow("Script Repo", Ioc.Default.GetRequiredService<ScriptRepoViewModel>());
        windowService.ShowManagedWindow("Script Repo");
    }

    private async void MenuItem_LoginAll_Click(object? sender, RoutedEventArgs e)
    {
        var menuItem = sender as MenuItem;
        string originalHeader = menuItem?.Header?.ToString() ?? "Login All Clients";
        if (menuItem != null) menuItem.IsEnabled = false;

        int count = 1;
        int total = _tabs.Values.Count(i => i.ChildHwnd != IntPtr.Zero);

        foreach (var info in _tabs.Values)
        {
            if (info.ChildHwnd != IntPtr.Zero)
            {
                if (menuItem != null) menuItem.Header = $"Logging in... ({count}/{total})";
                PostChildMessage(info.ChildHwnd, SkuaIpcMessages.Login, IntPtr.Zero, IntPtr.Zero);
                await Task.Delay(2000);
                count++;
            }
        }

        if (menuItem != null)
        {
            menuItem.Header = originalHeader;
            menuItem.IsEnabled = true;
        }
    }

    private void MenuItem_LogoutAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero)
                PostChildMessage(info.ChildHwnd, SkuaIpcMessages.Logout, IntPtr.Zero, IntPtr.Zero);
    }

    private string _lastJumpMap = "";
    private string _lastJumpCell = "";
    private string _lastJumpPlayer = "";
    private static string ArmyJumpSettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Skua", "army-control-jump.json");

    private sealed class ArmyJumpSettings
    {
        public string Map { get; set; } = "";
        public string Room { get; set; } = "";
        public string Cell { get; set; } = "Enter";
        public string Player { get; set; } = "";
        public List<ArmyJumpDestination> History { get; set; } = new();
        public List<ArmyJumpDestination> Presets { get; set; } = new();
    }

    private sealed class ArmyJumpDestination
    {
        public string Name { get; set; } = "";
        public string Map { get; set; } = "";
        public string Room { get; set; } = "";
        public string Cell { get; set; } = "Enter";
        public string Pad { get; set; } = "Spawn";

        public string DisplayName => $"{Map}{(string.IsNullOrWhiteSpace(Room) ? "" : $"-{Room}")} - {Cell}, {Pad}";
    }

    private sealed class ArmyClientLocation
    {
        public ArmyClientLocation() { }

        public string Map { get; set; } = "";
        public string Room { get; set; } = "";
        public string Cell { get; set; } = "Enter";
        public string Pad { get; set; } = "Spawn";
        public string Username { get; set; } = "";
        public DateTime UpdatedUtc { get; set; }
    }

    private readonly Dictionary<int, ArmyClientLocation> _latestClientLocations = new();
    private readonly Dictionary<int, string> _lastRecordedLocationByPid = new();

    private List<ArmyJumpDestination> _jumpMapHistory = new();
    private List<ArmyJumpDestination> _jumpMapPresets = new();
    private string _lastJumpRoom = "";

    private void LoadArmyJumpSettings()
    {
        try
        {
            string path = ArmyJumpSettingsPath;
            if (!File.Exists(path)) return;
            ArmyJumpSettings? settings = JsonSerializer.Deserialize<ArmyJumpSettings>(File.ReadAllText(path));
            if (settings == null) return;
            _lastJumpMap = settings.Map ?? "";
            _lastJumpCell = settings.Cell ?? "Enter";
            _lastJumpRoom = settings.Room ?? "";
            _lastJumpPlayer = settings.Player ?? "";
            _jumpMapHistory = settings.History ?? new List<ArmyJumpDestination>();
            _jumpMapPresets = settings.Presets ?? new List<ArmyJumpDestination>();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to load Army Control jump settings: {ex.Message}");
        }
    }

    private void SaveArmyJumpSettings()
    {
        try
        {
            string path = ArmyJumpSettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new ArmyJumpSettings
            {
                Map = _lastJumpMap,
                Cell = _lastJumpCell,
                Room = _lastJumpRoom,
                Player = _lastJumpPlayer,
                History = _jumpMapHistory,
                Presets = _jumpMapPresets
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to save Army Control jump settings: {ex.Message}");
        }
    }

    private Window CreateStyledDialog(string title, double width, double? minHeight = null)
    {
        Window dialog = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            WindowDecorations = WindowDecorations.None,
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaTitleBarHeightHint = 32,
            Background = this.Background,
        };
        if (minHeight.HasValue)
            dialog.MinHeight = minHeight.Value;
        dialog.Classes.Add("skua");
        return dialog;
    }

    private static void SetDialogContent(Window dialog, Control body)
    {
        var titleBar = new Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar
        {
            TitleText = dialog.Title ?? string.Empty,
        };
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(body, 1);

        var shell = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
        };
        shell.Children.Add(titleBar);
        shell.Children.Add(body);

        var frame = new Border();
        frame.Classes.Add("window-frame");
        frame.Child = shell;
        dialog.Content = frame;
    }

    private async void MenuItem_MapTravel_Click(object? sender, RoutedEventArgs e)
    {
        Window dialog = CreateStyledDialog("Map Travel", 640);

        StackPanel panel = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "Choose a recent destination or a saved preset to jump all clients.",
            TextWrapping = TextWrapping.Wrap
        });

        TextBlock historyLabel = new TextBlock { Text = "Recent destinations" };
        ComboBox historyPicker = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 360
        };
        StackPanel historyButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        Button jumpHistoryButton = new Button { Content = "Select / Jump", MinWidth = 110 };
        Button clearHistoryButton = new Button { Content = "Clear History", MinWidth = 100 };

        void RefreshHistory()
        {
            List<ArmyJumpDestination> history = _jumpMapHistory.ToList();
            historyPicker.ItemsSource = history.Select(item => item.DisplayName).ToList();
            historyPicker.SelectedIndex = history.Count > 0 ? 0 : -1;
            jumpHistoryButton.IsEnabled = history.Count > 0;
            clearHistoryButton.IsEnabled = history.Count > 0;
        }

        jumpHistoryButton.Click += (_, _) =>
        {
            List<ArmyJumpDestination> history = _jumpMapHistory.ToList();
            int index = historyPicker.SelectedIndex;
            if (index >= 0 && index < history.Count)
                JumpArmyToDestination(history[index]);
        };
        clearHistoryButton.Click += (_, _) =>
        {
            _jumpMapHistory.Clear();
            SaveArmyJumpSettings();
            RefreshHistory();
        };
        historyButtons.Children.Add(jumpHistoryButton);
        historyButtons.Children.Add(clearHistoryButton);
        panel.Children.Add(historyLabel);
        panel.Children.Add(historyPicker);
        panel.Children.Add(historyButtons);

        panel.Children.Add(new Separator());

        TextBlock presetLabel = new TextBlock { Text = "Saved presets" };
        ComboBox presetPicker = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 360
        };
        StackPanel presetButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        Button jumpPresetButton = new Button { Content = "Select / Jump", MinWidth = 110 };
        Button createPresetButton = new Button { Content = "Create", MinWidth = 80 };
        Button editPresetButton = new Button { Content = "Edit", MinWidth = 70 };
        Button deletePresetButton = new Button { Content = "Delete", MinWidth = 80 };

        void RefreshPresets(int selectedIndex = 0)
        {
            presetPicker.ItemsSource = _jumpMapPresets.Select(item => $"{item.Name} — {item.DisplayName}").ToList();
            presetPicker.SelectedIndex = _jumpMapPresets.Count == 0 ? -1 : Math.Clamp(selectedIndex, 0, _jumpMapPresets.Count - 1);
            bool hasPresets = _jumpMapPresets.Count > 0;
            jumpPresetButton.IsEnabled = hasPresets;
            editPresetButton.IsEnabled = hasPresets;
            deletePresetButton.IsEnabled = hasPresets;
        }

        jumpPresetButton.Click += (_, _) =>
        {
            int index = presetPicker.SelectedIndex;
            if (index >= 0 && index < _jumpMapPresets.Count)
                JumpArmyToDestination(_jumpMapPresets[index]);
        };
        createPresetButton.Click += async (_, _) =>
        {
            dialog.Hide();
            await OpenJumpMapPresetEditor(null);
            RefreshPresets(_jumpMapPresets.Count - 1);
            dialog.Show();
        };
        editPresetButton.Click += async (_, _) =>
        {
            int index = presetPicker.SelectedIndex;
            if (index < 0 || index >= _jumpMapPresets.Count)
                return;

            ArmyJumpDestination selectedPreset = _jumpMapPresets[index];
            dialog.Hide();
            await OpenJumpMapPresetEditor(selectedPreset);
            RefreshPresets(index);
            dialog.Show();
        };
        deletePresetButton.Click += (_, _) =>
        {
            int index = presetPicker.SelectedIndex;
            if (index < 0 || index >= _jumpMapPresets.Count)
                return;

            _jumpMapPresets.RemoveAt(index);
            SaveArmyJumpSettings();
            RefreshPresets(index);
        };

        presetButtons.Children.Add(jumpPresetButton);
        presetButtons.Children.Add(createPresetButton);
        presetButtons.Children.Add(editPresetButton);
        presetButtons.Children.Add(deletePresetButton);
        panel.Children.Add(presetLabel);
        panel.Children.Add(presetPicker);
        panel.Children.Add(presetButtons);

        StackPanel bottomButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        Button manualJumpButton = new Button { Content = "Jump to Map / Cell...", MinWidth = 140 };
        Button closeButton = new Button { Content = "Close", MinWidth = 84, IsCancel = true };
        manualJumpButton.Click += (_, _) =>
        {
            dialog.Hide();
            MenuItem_JumpMap_Click(null, new RoutedEventArgs());
            dialog.Show();
        };
        closeButton.Click += (_, _) => dialog.Close();
        bottomButtons.Children.Add(manualJumpButton);
        bottomButtons.Children.Add(closeButton);
        panel.Children.Add(bottomButtons);

        RefreshHistory();
        RefreshPresets();
        SetDialogContent(dialog, panel);
        await dialog.ShowDialog(this);
    }

    private void MenuItem_JumpMap_Click(object? sender, RoutedEventArgs e)
    {
        InputDialogViewModel vm = new("Jump Army to Map / Cell", "Enter destination as Map,Room,Cell (room is optional):", "e.g., yulgar,12345,Enter", false);
        vm.DialogTextInput = $"{_lastJumpMap},{_lastJumpRoom},{_lastJumpCell}";

        if (Ioc.Default.GetRequiredService<IDialogService>().ShowDialog(vm) != true)
            return;

        string[] parts = (vm.DialogTextInput ?? "").Split(',');
        string targetMap = parts.Length > 0 ? parts[0].Trim() : "";
        string targetRoom = parts.Length > 2 ? parts[1].Trim() : "";
        string targetCell = parts.Length > 2 ? parts[2].Trim() : (parts.Length > 1 ? parts[1].Trim() : "Enter");

        if (!string.IsNullOrWhiteSpace(targetMap))
            JumpArmyToDestination(new ArmyJumpDestination { Map = targetMap, Room = targetRoom, Cell = string.IsNullOrWhiteSpace(targetCell) ? "Enter" : targetCell });
    }

    private void JumpArmyToDestination(ArmyJumpDestination destination)
    {
        _lastJumpMap = destination.Map.Trim();
        _lastJumpRoom = destination.Room.Trim();
        _lastJumpCell = string.IsNullOrWhiteSpace(destination.Cell) ? "Enter" : destination.Cell.Trim();

        SaveArmyJumpSettings();
        string mapToJoin = string.IsNullOrWhiteSpace(_lastJumpRoom) ? _lastJumpMap : $"{_lastJumpMap}-{_lastJumpRoom}";
        string tempFile = Path.Combine(ClientFileSources.TabsDIR, "skua_global_jump.txt");
        File.WriteAllLines(tempFile, new[] { mapToJoin, _lastJumpCell, string.IsNullOrWhiteSpace(destination.Pad) ? "Spawn" : destination.Pad.Trim() });

        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero)
                PostChildMessage(info.ChildHwnd, SkuaIpcMessages.JumpMap, IntPtr.Zero, IntPtr.Zero);
    }

    private async void MenuItem_JumpMapHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (_jumpMapHistory.Count == 0)
        {
            Ioc.Default.GetRequiredService<IDialogService>().ShowMessageBox("No recently visited maps yet. Maps will appear here as your open clients travel.", "Jump Map History");
            return;
        }

        Window dialog = CreateStyledDialog("Jump to Recent Destination", 440);

        StackPanel panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Select a recent map, room and cell:", TextWrapping = TextWrapping.Wrap });

        List<ArmyJumpDestination> history = _jumpMapHistory.ToList();
        ComboBox historyPicker = new ComboBox
        {
            ItemsSource = history.Select(item => item.DisplayName).ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(historyPicker);

        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button clearButton = new Button { Content = "Clear History", MinWidth = 100 };
        Button cancelButton = new Button { Content = "Cancel", MinWidth = 84 };
        Button jumpButton = new Button { Content = "Jump", MinWidth = 84, IsDefault = true };
        clearButton.Click += (_, _) =>
        {
            _jumpMapHistory.Clear();
            SaveArmyJumpSettings();
            dialog.Close(false);
        };
        cancelButton.Click += (_, _) => dialog.Close(false);
        jumpButton.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(clearButton);
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(jumpButton);
        panel.Children.Add(buttons);
        SetDialogContent(dialog, panel);

        bool? confirmed = await dialog.ShowDialog<bool?>(this);
        if (confirmed != true || historyPicker.SelectedIndex < 0 || historyPicker.SelectedIndex >= history.Count)
            return;

        JumpArmyToDestination(history[historyPicker.SelectedIndex]);
    }

    private async void MenuItem_SaveJumpMapPreset_Click(object? sender, RoutedEventArgs e)
    {
        await OpenJumpMapPresetEditor(null);
    }

    private async Task OpenJumpMapPresetEditor(ArmyJumpDestination? existingPreset)
    {
        Window dialog = CreateStyledDialog(existingPreset == null ? "Create Map Preset" : "Edit Map Preset", 460);

        StackPanel panel = new StackPanel { Spacing = 10, Margin = new Thickness(18) };
        TextBox nameInput = new TextBox { Watermark = "Preset name", Text = existingPreset?.Name ?? "" };
        TextBox mapInput = new TextBox { Watermark = "Map name", Text = existingPreset?.Map ?? "" };
        TextBox roomInput = new TextBox { Watermark = "Room number (optional)", Text = existingPreset?.Room ?? "" };
        TextBox cellInput = new TextBox { Watermark = "Cell", Text = existingPreset?.Cell ?? "Enter" };
        TextBox padInput = new TextBox { Watermark = "Pad", Text = existingPreset?.Pad ?? "Spawn" };

        if (existingPreset == null
            && InstancesTabControl.SelectedItem is TabItem currentTab
            && _tabs.TryGetValue(currentTab, out TabInfo? currentInfo)
            && currentInfo.Process != null
            && _latestClientLocations.TryGetValue(currentInfo.Process.Id, out ArmyClientLocation? currentLocation)
            && currentLocation != null
            && currentLocation.UpdatedUtc >= DateTime.UtcNow.AddSeconds(-5)
            && !string.IsNullOrWhiteSpace(currentLocation.Map))
        {
            mapInput.Text = currentLocation.Map;
            roomInput.Text = currentLocation.Room;
            cellInput.Text = currentLocation.Cell;
            padInput.Text = currentLocation.Pad;
            nameInput.Text = currentLocation.Map + (string.IsNullOrWhiteSpace(currentLocation.Room) ? "" : "-" + currentLocation.Room);
        }

        panel.Children.Add(new TextBlock { Text = "Preset name" });
        panel.Children.Add(nameInput);
        panel.Children.Add(new TextBlock { Text = "Map" });
        panel.Children.Add(mapInput);
        panel.Children.Add(new TextBlock { Text = "Room number (optional)" });
        panel.Children.Add(roomInput);
        panel.Children.Add(new TextBlock { Text = "Cell" });
        panel.Children.Add(cellInput);
        panel.Children.Add(new TextBlock { Text = "Pad" });
        panel.Children.Add(padInput);

        Button saveCurrentButton = new Button { Content = "Use Current Client Location", HorizontalAlignment = HorizontalAlignment.Left };
        saveCurrentButton.Click += (_, _) =>
        {
            if (InstancesTabControl.SelectedItem is not TabItem selectedTab
                || !_tabs.TryGetValue(selectedTab, out TabInfo? selectedInfo)
                || selectedInfo.Process == null
                || !_latestClientLocations.TryGetValue(selectedInfo.Process.Id, out ArmyClientLocation? location)
                || location == null
                || location.UpdatedUtc < DateTime.UtcNow.AddSeconds(-5)
                || string.IsNullOrWhiteSpace(location.Map))
            {
                Ioc.Default.GetRequiredService<IDialogService>().ShowMessageBox("The selected client has not reported a current location yet. Log in and wait a moment, then try again.", "Current Location");
                return;
            }

            mapInput.Text = location.Map;
            roomInput.Text = location.Room;
            cellInput.Text = location.Cell;
            padInput.Text = location.Pad;
            if (string.IsNullOrWhiteSpace(nameInput.Text))
                nameInput.Text = location.Map + (string.IsNullOrWhiteSpace(location.Room) ? "" : "-" + location.Room);
        };
        panel.Children.Add(saveCurrentButton);

        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button cancelButton = new Button { Content = "Cancel", MinWidth = 84 };
        Button saveButton = new Button { Content = "Save Preset", MinWidth = 100, IsDefault = true };
        cancelButton.Click += (_, _) => dialog.Close(false);
        saveButton.Click += (_, _) =>
        {
            string name = nameInput.Text?.Trim() ?? "";
            string map = mapInput.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(map))
            {
                Ioc.Default.GetRequiredService<IDialogService>().ShowMessageBox("Enter both a preset name and map name.", "Create Map Preset");
                return;
            }

            ArmyJumpDestination preset = existingPreset ?? new ArmyJumpDestination();
            preset.Name = name;
            preset.Map = map;
            preset.Room = roomInput.Text?.Trim() ?? "";
            preset.Cell = string.IsNullOrWhiteSpace(cellInput.Text) ? "Enter" : cellInput.Text.Trim();
            preset.Pad = string.IsNullOrWhiteSpace(padInput.Text) ? "Spawn" : padInput.Text.Trim();

            _jumpMapPresets.RemoveAll(item => !ReferenceEquals(item, preset) && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existingPreset == null)
                _jumpMapPresets.Add(preset);
            SaveArmyJumpSettings();
            dialog.Close(true);
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(saveButton);
        panel.Children.Add(buttons);
        SetDialogContent(dialog, panel);

        await dialog.ShowDialog<bool?>(this);
    }

    private async void MenuItem_JumpMapPreset_Click(object? sender, RoutedEventArgs e)
    {
        if (_jumpMapPresets.Count == 0)
        {
            Ioc.Default.GetRequiredService<IDialogService>().ShowMessageBox("No map presets saved yet. Use Save Current Map as Preset to add one.", "Map Presets");
            return;
        }

        Window dialog = CreateStyledDialog("Jump to Map Preset", 440);

        StackPanel panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Select a saved map, room and cell preset:", TextWrapping = TextWrapping.Wrap });

        List<ArmyJumpDestination> presets = _jumpMapPresets.ToList();
        ComboBox presetPicker = new ComboBox
        {
            ItemsSource = presets.Select(item => $"{item.Name} — {item.DisplayName}").ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(presetPicker);

        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button cancelButton = new Button { Content = "Cancel", MinWidth = 84 };
        Button jumpButton = new Button { Content = "Jump", MinWidth = 84, IsDefault = true };
        cancelButton.Click += (_, _) => dialog.Close(false);
        jumpButton.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(jumpButton);
        panel.Children.Add(buttons);
        SetDialogContent(dialog, panel);

        bool? confirmed = await dialog.ShowDialog<bool?>(this);
        if (confirmed != true || presetPicker.SelectedIndex < 0 || presetPicker.SelectedIndex >= presets.Count)
            return;

        JumpArmyToDestination(presets[presetPicker.SelectedIndex]);
    }

    private async void MenuItem_ManageJumpMapPresets_Click(object? sender, RoutedEventArgs e)
    {
        Window dialog = CreateStyledDialog("Manage Map Presets", 600, minHeight: 280);

        StackPanel panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Create, inspect, edit, or delete saved map presets.", TextWrapping = TextWrapping.Wrap });

        ListBox presetList = new ListBox
        {
            ItemsSource = _jumpMapPresets.Select(item => $"{item.Name} — {item.DisplayName}").ToList(),
            SelectedIndex = _jumpMapPresets.Count > 0 ? 0 : -1,
            MinHeight = 100,
            MaxHeight = 320
        };
        panel.Children.Add(presetList);

        TextBlock destinationText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void UpdateDestination()
        {
            int index = presetList.SelectedIndex;
            destinationText.Text = index >= 0 && index < _jumpMapPresets.Count
                ? $"Destination: {_jumpMapPresets[index].DisplayName}"
                : "No preset selected.";
        }

        presetList.SelectionChanged += (_, _) => UpdateDestination();
        panel.Children.Add(destinationText);
        UpdateDestination();

        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button createButton = new Button { Content = "Create Preset...", MinWidth = 110 };
        Button editButton = new Button { Content = "Edit Selected...", MinWidth = 110, IsEnabled = _jumpMapPresets.Count > 0 };
        Button deleteButton = new Button { Content = "Delete Selected", MinWidth = 120, IsEnabled = _jumpMapPresets.Count > 0 };
        Button closeButton = new Button { Content = "Close", MinWidth = 84, IsCancel = true };

        void RefreshPresets(int selectedIndex)
        {
            presetList.ItemsSource = _jumpMapPresets.Select(item => $"{item.Name} — {item.DisplayName}").ToList();
            presetList.SelectedIndex = _jumpMapPresets.Count == 0 ? -1 : Math.Clamp(selectedIndex, 0, _jumpMapPresets.Count - 1);
            editButton.IsEnabled = _jumpMapPresets.Count > 0;
            deleteButton.IsEnabled = _jumpMapPresets.Count > 0;
            UpdateDestination();
        }

        createButton.Click += async (_, _) =>
        {
            dialog.Hide();
            await OpenJumpMapPresetEditor(null);
            dialog.Show();
            RefreshPresets(_jumpMapPresets.Count - 1);
        };
        editButton.Click += async (_, _) =>
        {
            int index = presetList.SelectedIndex;
            if (index < 0 || index >= _jumpMapPresets.Count)
                return;

            ArmyJumpDestination selectedPreset = _jumpMapPresets[index];
            dialog.Hide();
            await OpenJumpMapPresetEditor(selectedPreset);
            dialog.Show();
            RefreshPresets(index);
        };
        deleteButton.Click += (_, _) =>
        {
            int index = presetList.SelectedIndex;
            if (index < 0 || index >= _jumpMapPresets.Count)
                return;

            _jumpMapPresets.RemoveAt(index);
            SaveArmyJumpSettings();
            RefreshPresets(index);
        };
        closeButton.Click += (_, _) => dialog.Close();
        buttons.Children.Add(createButton);
        buttons.Children.Add(editButton);
        buttons.Children.Add(deleteButton);
        buttons.Children.Add(closeButton);
        panel.Children.Add(buttons);
        SetDialogContent(dialog, panel);

        await dialog.ShowDialog(this);
    }

    private async void MenuItem_JumpPlayer_Click(object? sender, RoutedEventArgs e)
    {
        SyncTabTitles();
        List<string> players = _tabs
            .Select(entry => GetTabLoggedInUsername(entry.Key, entry.Value, GetTabTitle(entry.Key)))
            .Where(username => !string.IsNullOrWhiteSpace(username))
            .Select(username => username!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(username => username, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (players.Count == 0)
        {
            Ioc.Default.GetService<IDialogService>()?.ShowMessageBox("No logged-in player names were found in the open client tabs. Log in to at least one tab and try again.", "Jump All to Player");
            return;
        }

        Window dialog = CreateStyledDialog("Jump Army to Player", 360);
        StackPanel panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Select a player from the open client tabs:", TextWrapping = TextWrapping.Wrap });
        string initialPlayer = players.FirstOrDefault(player => player.Equals(_lastJumpPlayer, StringComparison.OrdinalIgnoreCase)) ?? players[0];
        ComboBox playerPicker = new ComboBox
        {
            ItemsSource = players,
            SelectedItem = initialPlayer,
            Text = initialPlayer,
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(playerPicker);
        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button cancelButton = new Button { Content = "Cancel", MinWidth = 84 };
        Button jumpButton = new Button { Content = "Jump", MinWidth = 84, IsDefault = true };
        cancelButton.Click += (_, _) => dialog.Close(false);
        jumpButton.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(jumpButton);
        panel.Children.Add(buttons);
        SetDialogContent(dialog, panel);

        bool? confirmed = await dialog.ShowDialog<bool?>(this);
        string targetPlayer = playerPicker.Text?.Trim() ?? "";
        if (confirmed != true || string.IsNullOrWhiteSpace(targetPlayer))
            return;

        _lastJumpPlayer = targetPlayer;
        SaveArmyJumpSettings();
        string tempFile = Path.Combine(ClientFileSources.TabsDIR, "skua_global_jump_player.txt");
        File.WriteAllText(tempFile, targetPlayer);

        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero)
                PostChildMessage(info.ChildHwnd, SkuaIpcMessages.JumpPlayer, IntPtr.Zero, IntPtr.Zero);
    }

    private string GetTabTitle(TabItem tab)
    {
        if (tab.Header is StackPanel headerPanel)
        {
            var text = headerPanel.Children.OfType<TextBlock>().FirstOrDefault(block => block.Text != "✕")?.Text;
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        string? headerStr = tab.Header?.ToString();
        return !string.IsNullOrWhiteSpace(headerStr) ? headerStr : "Skua Client";
    }

    private void MenuItem_JumpHouse_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero)
                PostChildMessage(info.ChildHwnd, SkuaIpcMessages.JumpHouse, IntPtr.Zero, IntPtr.Zero);
    }

    private string _lastAcceptQuestId = "";

    private async void MenuItem_ChangeServerAll_Click(object? sender, RoutedEventArgs e)
    {
        List<string> serverNames;
        try
        {
            IScriptServers? scriptServers = Ioc.Default.GetService<IScriptServers>();
            if (scriptServers is null)
            {
                Ioc.Default.GetService<IDialogService>()?.ShowMessageBox("The server service is unavailable. Please restart BonxieMux and try again.", "Change Server for All");
                return;
            }

            List<Skua.Core.Models.Servers.Server> servers = await scriptServers.GetServers(true);
            serverNames = servers
                .Where(server => server.Online && !string.IsNullOrWhiteSpace(server.Name) && !string.IsNullOrWhiteSpace(server.IP))
                .Select(server => server.Name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to load Army Control servers: {ex.GetType().Name}: {ex.Message}");
            Ioc.Default.GetService<IDialogService>()?.ShowMessageBox("Could not load the current server list. Check your connection and try again.", "Change Server for All");
            return;
        }

        if (serverNames.Count == 0)
        {
            Ioc.Default.GetService<IDialogService>()?.ShowMessageBox("The server API did not return any online servers.", "Change Server for All");
            return;
        }

        Window dialog = CreateStyledDialog("Change Server for All", 360);
        StackPanel panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Select the server for all open clients:", TextWrapping = TextWrapping.Wrap });

        string initialServer = serverNames.FirstOrDefault(name => name.Equals("Artix", StringComparison.OrdinalIgnoreCase)) ?? serverNames[0];
        ComboBox serverPicker = new ComboBox
        {
            ItemsSource = serverNames,
            SelectedItem = initialServer,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(serverPicker);

        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button cancelButton = new Button { Content = "Cancel", MinWidth = 84 };
        Button changeButton = new Button { Content = "Change Server", MinWidth = 110, IsDefault = true };
        cancelButton.Click += (_, _) => dialog.Close(false);
        changeButton.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(changeButton);
        panel.Children.Add(buttons);
        SetDialogContent(dialog, panel);

        bool? confirmed = await dialog.ShowDialog<bool?>(this);
        string targetServer = serverPicker.SelectedItem?.ToString()?.Trim() ?? string.Empty;
        if (confirmed != true || string.IsNullOrWhiteSpace(targetServer))
            return;

        try
        {
            string tempFile = Path.Combine(ClientFileSources.TabsDIR, "skua_global_change_server.txt");
            File.WriteAllText(tempFile, targetServer);
            int sentCount = 0;
            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero)
                {
                    PostChildMessage(info.ChildHwnd, SkuaIpcMessages.ChangeServer, IntPtr.Zero, IntPtr.Zero);
                    sentCount++;
                }
            }

            if (sentCount == 0)
                Ioc.Default.GetService<IDialogService>()?.ShowMessageBox("No active client windows were available to receive the server change.", "Change Server for All");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to broadcast Army Control server change: {ex.GetType().Name}: {ex.Message}");
            Ioc.Default.GetService<IDialogService>()?.ShowMessageBox("Could not send the server change to the clients.", "Change Server for All");
        }
    }

    private void MenuItem_AcceptQuest_Click(object? sender, RoutedEventArgs e)
    {
        var vm = new InputDialogViewModel("Accept Quest (Army)", "Enter quest ID to accept/register on all accounts:", "e.g., 1907", false);
        vm.DialogTextInput = _lastAcceptQuestId;
        
        if (Ioc.Default.GetRequiredService<IDialogService>().ShowDialog(vm) == true)
        {
            string questId = vm.DialogTextInput?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(questId))
            {
                _lastAcceptQuestId = questId;
                string tempFile = System.IO.Path.Combine(ClientFileSources.TabsDIR, "skua_global_accept_quest.txt");
                System.IO.File.WriteAllLines(tempFile, new[] { questId, "-1" });
                
                foreach (var info in _tabs.Values)
                    if (info.ChildHwnd != IntPtr.Zero)
                        PostChildMessage(info.ChildHwnd, SkuaIpcMessages.AcceptQuest, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }
}
