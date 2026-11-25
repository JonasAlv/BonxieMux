using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.App.Avalonia.Services;
using Skua.App.Avalonia.Views.Dialogs;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Shared.Avalonia.Controls.Shell;
using Skua.Shared.Avalonia.Theming;
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

    private async void MenuItem_MapTravel_Click(object? sender, RoutedEventArgs e)
    {
        ModalDialogWindow dialog = new("Map Travel", 640)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        ArmyMapTravelView view = new();

        void RefreshHistory() => view.RefreshHistory(_jumpMapHistory.Select(item => item.DisplayName));
        void RefreshPresets(int selectedIndex = 0) => view.RefreshPresets(_jumpMapPresets.Select(item => $"{item.Name} — {item.DisplayName}"), selectedIndex);

        view.JumpHistoryClicked += index =>
        {
            if (index >= 0 && index < _jumpMapHistory.Count)
                JumpArmyToDestination(_jumpMapHistory[index]);
        };
        view.ClearHistoryClicked += () =>
        {
            _jumpMapHistory.Clear();
            SaveArmyJumpSettings();
            RefreshHistory();
        };
        view.JumpPresetClicked += index =>
        {
            if (index >= 0 && index < _jumpMapPresets.Count)
                JumpArmyToDestination(_jumpMapPresets[index]);
        };
        view.CreatePresetClicked += async () =>
        {
            dialog.Hide();
            await OpenJumpMapPresetEditor(null);
            RefreshPresets(_jumpMapPresets.Count - 1);
            dialog.Show();
        };
        view.EditPresetClicked += async index =>
        {
            if (index < 0 || index >= _jumpMapPresets.Count)
                return;
            ArmyJumpDestination selectedPreset = _jumpMapPresets[index];
            dialog.Hide();
            await OpenJumpMapPresetEditor(selectedPreset);
            RefreshPresets(index);
            dialog.Show();
        };
        view.DeletePresetClicked += index =>
        {
            if (index < 0 || index >= _jumpMapPresets.Count)
                return;
            _jumpMapPresets.RemoveAt(index);
            SaveArmyJumpSettings();
            RefreshPresets(index);
        };

        dialog.AddButton("Jump to Map / Cell...", () =>
        {
            dialog.Hide();
            MenuItem_JumpMap_Click(null, new RoutedEventArgs());
            dialog.Show();
        }, minWidth: 140);
        dialog.AddButton("Close", () => dialog.Close(), isCancel: true);

        RefreshHistory();
        RefreshPresets();
        dialog.SetBody(view);
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

    private async Task<(bool Confirmed, int SelectedIndex, string SelectedText)> ShowArmyComboDialogAsync(
        string title,
        string prompt,
        IReadOnlyList<string> items,
        string? selectedItem,
        bool isEditable,
        string confirmText,
        double width = 440,
        double confirmMinWidth = 84,
        string? leftActionText = null,
        Action<ModalDialogWindow>? onLeftAction = null)
    {
        ModalDialogWindow dialog = new(title, width)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        ComboBox picker = new()
        {
            ItemsSource = items,
            IsEditable = isEditable,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (selectedItem is not null)
        {
            picker.SelectedItem = selectedItem;
            if (isEditable)
                picker.Text = selectedItem;
        }
        else if (items.Count > 0)
        {
            picker.SelectedIndex = 0;
        }

        if (!string.IsNullOrWhiteSpace(leftActionText) && onLeftAction is not null)
        {
            Button leftBtn = new() { Content = leftActionText, MinWidth = 100, Classes = { "skua-button" } };
            leftBtn.Click += (_, _) => onLeftAction(dialog);
            dialog.SetLeftFooter(leftBtn);
        }

        dialog.AddButton("Cancel", () => dialog.Close(false), isCancel: true);
        dialog.AddButton(confirmText, () => dialog.Close(true), isPrimary: true, isDefault: true, minWidth: confirmMinWidth);

        dialog.SetBody(new StackPanel
        {
            Spacing = UiTokens.GetDouble(UiTokens.UiGapLg, 12),
            Children = { ModalDialogWindow.CreateBodyText(prompt), picker }
        });

        bool? confirmed = await dialog.ShowDialog<bool?>(this);
        string text = isEditable ? (picker.Text ?? string.Empty) : (picker.SelectedItem?.ToString() ?? string.Empty);
        return (confirmed == true, picker.SelectedIndex, text.Trim());
    }

    private async void MenuItem_JumpMapHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (_jumpMapHistory.Count == 0)
        {
            Ioc.Default.GetRequiredService<IDialogService>().ShowMessageBox("No recently visited maps yet. Maps will appear here as your open clients travel.", "Jump Map History");
            return;
        }

        List<ArmyJumpDestination> history = _jumpMapHistory.ToList();
        var (confirmed, selectedIndex, _) = await ShowArmyComboDialogAsync(
            "Jump to Recent Destination",
            "Select a recent map, room and cell:",
            history.Select(item => item.DisplayName).ToList(),
            null,
            isEditable: false,
            confirmText: "Jump",
            leftActionText: "Clear History",
            onLeftAction: dlg =>
            {
                _jumpMapHistory.Clear();
                SaveArmyJumpSettings();
                dlg.Close(false);
            });

        if (!confirmed || selectedIndex < 0 || selectedIndex >= history.Count)
            return;

        JumpArmyToDestination(history[selectedIndex]);
    }

    private async void MenuItem_SaveJumpMapPreset_Click(object? sender, RoutedEventArgs e)
    {
        await OpenJumpMapPresetEditor(null);
    }

    private async Task OpenJumpMapPresetEditor(ArmyJumpDestination? existingPreset)
    {
        ModalDialogWindow dialog = new(existingPreset == null ? "Create Map Preset" : "Edit Map Preset", 460)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        ArmyMapPresetEditorView view = new()
        {
            PresetName = existingPreset?.Name ?? string.Empty,
            MapName = existingPreset?.Map ?? string.Empty,
            RoomNumber = existingPreset?.Room ?? string.Empty,
            CellName = existingPreset?.Cell ?? "Enter",
            PadName = existingPreset?.Pad ?? "Spawn"
        };

        if (existingPreset == null
            && InstancesTabControl.SelectedItem is TabItem currentTab
            && _tabs.TryGetValue(currentTab, out TabInfo? currentInfo)
            && currentInfo.Process != null
            && _latestClientLocations.TryGetValue(currentInfo.Process.Id, out ArmyClientLocation? currentLocation)
            && currentLocation != null
            && currentLocation.UpdatedUtc >= DateTime.UtcNow.AddSeconds(-5)
            && !string.IsNullOrWhiteSpace(currentLocation.Map))
        {
            view.MapName = currentLocation.Map;
            view.RoomNumber = currentLocation.Room;
            view.CellName = currentLocation.Cell;
            view.PadName = currentLocation.Pad;
            view.PresetName = currentLocation.Map + (string.IsNullOrWhiteSpace(currentLocation.Room) ? "" : "-" + currentLocation.Room);
        }

        view.UseCurrentLocationClicked += () =>
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

            view.MapName = location.Map;
            view.RoomNumber = location.Room;
            view.CellName = location.Cell;
            view.PadName = location.Pad;
            if (string.IsNullOrWhiteSpace(view.PresetName))
                view.PresetName = location.Map + (string.IsNullOrWhiteSpace(location.Room) ? "" : "-" + location.Room);
        };

        dialog.AddButton("Cancel", () => dialog.Close(false), isCancel: true);
        dialog.AddButton("Save Preset", () =>
        {
            string name = view.PresetName.Trim();
            string map = view.MapName.Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(map))
            {
                Ioc.Default.GetRequiredService<IDialogService>().ShowMessageBox("Enter both a preset name and map name.", "Create Map Preset");
                return;
            }

            ArmyJumpDestination preset = existingPreset ?? new ArmyJumpDestination();
            preset.Name = name;
            preset.Map = map;
            preset.Room = view.RoomNumber.Trim();
            preset.Cell = string.IsNullOrWhiteSpace(view.CellName) ? "Enter" : view.CellName.Trim();
            preset.Pad = string.IsNullOrWhiteSpace(view.PadName) ? "Spawn" : view.PadName.Trim();

            _jumpMapPresets.RemoveAll(item => !ReferenceEquals(item, preset) && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existingPreset == null)
                _jumpMapPresets.Add(preset);
            SaveArmyJumpSettings();
            dialog.Close(true);
        }, isPrimary: true, isDefault: true, minWidth: 100);

        dialog.SetBody(view);
        await dialog.ShowDialog<bool?>(this);
    }

    private async void MenuItem_JumpMapPreset_Click(object? sender, RoutedEventArgs e)
    {
        if (_jumpMapPresets.Count == 0)
        {
            Ioc.Default.GetRequiredService<IDialogService>().ShowMessageBox("No map presets saved yet. Use Save Current Map as Preset to add one.", "Map Presets");
            return;
        }

        List<ArmyJumpDestination> presets = _jumpMapPresets.ToList();
        var (confirmed, selectedIndex, _) = await ShowArmyComboDialogAsync(
            "Jump to Map Preset",
            "Select a saved map, room and cell preset:",
            presets.Select(item => $"{item.Name} — {item.DisplayName}").ToList(),
            null,
            isEditable: false,
            confirmText: "Jump");

        if (!confirmed || selectedIndex < 0 || selectedIndex >= presets.Count)
            return;

        JumpArmyToDestination(presets[selectedIndex]);
    }

    private async void MenuItem_ManageJumpMapPresets_Click(object? sender, RoutedEventArgs e)
    {
        ModalDialogWindow dialog = new("Manage Map Presets", 600)
        {
            MinHeight = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        ArmyManagePresetsView view = new();

        void UpdateDestination(int index)
        {
            view.SetDestinationSummary(index >= 0 && index < _jumpMapPresets.Count
                ? $"Destination: {_jumpMapPresets[index].DisplayName}"
                : "No preset selected.");
        }

        Button? editButton = null;
        Button? deleteButton = null;

        void RefreshPresets(int selectedIndex)
        {
            view.RefreshPresets(_jumpMapPresets.Select(item => $"{item.Name} — {item.DisplayName}"), selectedIndex);
            bool hasPresets = _jumpMapPresets.Count > 0;
            if (editButton != null) editButton.IsEnabled = hasPresets;
            if (deleteButton != null) deleteButton.IsEnabled = hasPresets;
            UpdateDestination(view.SelectedIndex);
        }

        view.SelectionChangedIndex += UpdateDestination;

        dialog.AddButton("Create Preset...", async () =>
        {
            dialog.Hide();
            await OpenJumpMapPresetEditor(null);
            dialog.Show();
            RefreshPresets(_jumpMapPresets.Count - 1);
        }, minWidth: 110);

        editButton = dialog.AddButton("Edit Selected...", async () =>
        {
            int index = view.SelectedIndex;
            if (index < 0 || index >= _jumpMapPresets.Count)
                return;

            ArmyJumpDestination selectedPreset = _jumpMapPresets[index];
            dialog.Hide();
            await OpenJumpMapPresetEditor(selectedPreset);
            dialog.Show();
            RefreshPresets(index);
        }, minWidth: 110);

        deleteButton = dialog.AddButton("Delete Selected", () =>
        {
            int index = view.SelectedIndex;
            if (index < 0 || index >= _jumpMapPresets.Count)
                return;

            _jumpMapPresets.RemoveAt(index);
            SaveArmyJumpSettings();
            RefreshPresets(index);
        }, minWidth: 120);

        dialog.AddButton("Close", () => dialog.Close(), isCancel: true);

        RefreshPresets(_jumpMapPresets.Count > 0 ? 0 : -1);
        dialog.SetBody(view);
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

        string initialPlayer = players.FirstOrDefault(player => player.Equals(_lastJumpPlayer, StringComparison.OrdinalIgnoreCase)) ?? players[0];
        var (confirmed, _, targetPlayer) = await ShowArmyComboDialogAsync(
            "Jump Army to Player",
            "Select a player from the open client tabs:",
            players,
            initialPlayer,
            isEditable: true,
            confirmText: "Jump",
            width: 360);

        if (!confirmed || string.IsNullOrWhiteSpace(targetPlayer))
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

        string initialServer = serverNames.FirstOrDefault(name => name.Equals("Artix", StringComparison.OrdinalIgnoreCase)) ?? serverNames[0];
        var (confirmed, _, targetServer) = await ShowArmyComboDialogAsync(
            "Change Server for All",
            "Select the server for all open clients:",
            serverNames,
            initialServer,
            isEditable: false,
            confirmText: "Change Server",
            width: 360,
            confirmMinWidth: 110);

        if (!confirmed || string.IsNullOrWhiteSpace(targetServer))
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
