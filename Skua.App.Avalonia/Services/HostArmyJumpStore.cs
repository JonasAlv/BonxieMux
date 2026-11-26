using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Skua.App.Avalonia.Services;

internal sealed class ArmyJumpDestination
{
    public string Name { get; set; } = "";
    public string Map { get; set; } = "";
    public string Room { get; set; } = "";
    public string Cell { get; set; } = "Enter";
    public string Pad { get; set; } = "Spawn";

    public string DisplayName => $"{Map}{(string.IsNullOrWhiteSpace(Room) ? "" : $"-{Room}")} - {Cell}, {Pad}";
}

internal sealed class ArmyClientLocation
{
    public string Map { get; set; } = "";
    public string Room { get; set; } = "";
    public string Cell { get; set; } = "Enter";
    public string Pad { get; set; } = "Spawn";
    public string Username { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// Persists Army Control jump history, presets, last jump fields, and per-tab client locations.
/// </summary>
internal sealed class HostArmyJumpStore
{
    private sealed class ArmyJumpSettings
    {
        public string Map { get; set; } = "";
        public string Room { get; set; } = "";
        public string Cell { get; set; } = "Enter";
        public string Player { get; set; } = "";
        public List<ArmyJumpDestination> History { get; set; } = new();
        public List<ArmyJumpDestination> Presets { get; set; } = new();
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Skua",
        "army-control-jump.json");

    public string LastJumpMap { get; set; } = "";
    public string LastJumpRoom { get; set; } = "";
    public string LastJumpCell { get; set; } = "";
    public string LastJumpPlayer { get; set; } = "";
    public List<ArmyJumpDestination> History { get; private set; } = new();
    public List<ArmyJumpDestination> Presets { get; private set; } = new();
    public Dictionary<int, ArmyClientLocation> LatestClientLocations { get; } = new();
    private readonly Dictionary<int, string> _lastRecordedLocationByPid = new();

    public void Load()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path)) return;
            ArmyJumpSettings? settings = JsonSerializer.Deserialize<ArmyJumpSettings>(File.ReadAllText(path));
            if (settings == null) return;
            LastJumpMap = settings.Map ?? "";
            LastJumpCell = settings.Cell ?? "Enter";
            LastJumpRoom = settings.Room ?? "";
            LastJumpPlayer = settings.Player ?? "";
            History = settings.History ?? new List<ArmyJumpDestination>();
            Presets = settings.Presets ?? new List<ArmyJumpDestination>();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to load Army Control jump settings: {ex.Message}");
        }
    }

    public void Save()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new ArmyJumpSettings
            {
                Map = LastJumpMap,
                Cell = LastJumpCell,
                Room = LastJumpRoom,
                Player = LastJumpPlayer,
                History = History,
                Presets = Presets
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to save Army Control jump settings: {ex.Message}");
        }
    }

    public void RecordClientLocation(int pid, ArmyClientLocation location)
    {
        LatestClientLocations[pid] = location;
        string signature = $"{location.Map}|{location.Room}";
        if (_lastRecordedLocationByPid.TryGetValue(pid, out string? previousSignature)
            && previousSignature.Equals(signature, StringComparison.OrdinalIgnoreCase))
            return;

        _lastRecordedLocationByPid[pid] = signature;
        History.RemoveAll(item =>
            item.Map.Equals(location.Map, StringComparison.OrdinalIgnoreCase)
            && item.Room.Equals(location.Room, StringComparison.OrdinalIgnoreCase));

        History.Insert(0, new ArmyJumpDestination
        {
            Name = location.Username,
            Map = location.Map,
            Room = location.Room,
            Cell = string.IsNullOrWhiteSpace(location.Cell) ? "Enter" : location.Cell,
            Pad = string.IsNullOrWhiteSpace(location.Pad) ? "Spawn" : location.Pad
        });

        if (History.Count > 15)
            History.RemoveRange(15, History.Count - 15);

        Save();
    }

    public void RemoveLocation(int pid)
    {
        LatestClientLocations.Remove(pid);
        _lastRecordedLocationByPid.Remove(pid);
    }

    public void ClearLocations()
    {
        LatestClientLocations.Clear();
        _lastRecordedLocationByPid.Clear();
    }
}
