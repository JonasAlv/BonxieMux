using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Skua.Core.Models.Auras;
using Skua.Core.Models.Monsters;
using Skua.Core.Models.Skills;
using Skua.Shared.Avalonia.Theming;

namespace Skua.Plugin.Omniscope;

public partial class OmniscopeWindow
{
    private void UpdateEntities(Monster[] monsters)
    {
        _entitySnapshot = monsters
            .OrderByDescending(static monster => monster.Alive)
            .ThenBy(static monster => monster.Name)
            .Take(500)
            .Select(monster => new EntityRow
            {
                Name = monster.Name,
                Cell = $"Cell {NullLabel(monster.Cell)}",
                Health = $"{monster.HP:N0}/{monster.MaxHP:N0}",
                State = monster.Alive ? "Alive" : "Down",
                Id = monster.ID.ToString(),
                Source = monster
            })
            .ToArray();

        ApplyEntityFilter();
    }

    private void EntitySearchBox_TextChanged(object? sender, string text) => ApplyEntityFilter();

    private void AvatarFollowButton_Click(object? sender, RoutedEventArgs e) => SetAvatarLiveFollow(!_avatarLiveFollow);
    private void EntityFollowButton_Click(object? sender, RoutedEventArgs e) => SetEntityLiveFollow(!_entityLiveFollow);
    private void PlayerFollowButton_Click(object? sender, RoutedEventArgs e) => SetPlayerLiveFollow(!_playerLiveFollow);
    private void EventFollowButton_Click(object? sender, RoutedEventArgs e) => SetEventLiveFollow(!_eventLiveFollow);

    private void EntityList_PointerWheelChanged(object? sender, PointerWheelEventArgs e) => SetEntityLiveFollow(false);
    private void EntityList_PointerPressed(object? sender, PointerPressedEventArgs e) => SetEntityLiveFollow(false);
    private void PlayerList_PointerWheelChanged(object? sender, PointerWheelEventArgs e) => SetPlayerLiveFollow(false);
    private void PlayerList_PointerPressed(object? sender, PointerPressedEventArgs e) => SetPlayerLiveFollow(false);
    private void EventList_PointerWheelChanged(object? sender, PointerWheelEventArgs e) => SetEventLiveFollow(false);
    private void EventList_PointerPressed(object? sender, PointerPressedEventArgs e) => SetEventLiveFollow(false);

    private void SetAvatarLiveFollow(bool enabled)
    {
        _avatarLiveFollow = enabled;
        AvatarFollowButton.Content = enabled ? "Pause" : "Resume";
        AvatarFollowStatus.Text = enabled ? "Live" : "Paused";
        AvatarFollowStatus.Foreground = enabled
            ? UiTokens.GetBrush(UiTokens.SkuaSuccessBrush, Brushes.MediumSpringGreen)
            : UiTokens.GetBrush(UiTokens.SkuaWarningBrush, Brushes.Goldenrod);
        if (enabled)
            RefreshTelemetry();
    }

    private void SetEntityLiveFollow(bool enabled)
    {
        _entityLiveFollow = enabled;
        EntityFollowButton.Content = enabled ? "Pause" : "Resume";
        if (enabled)
            ApplyEntityFilter();
    }

    private void SetPlayerLiveFollow(bool enabled)
    {
        _playerLiveFollow = enabled;
        PlayerFollowButton.Content = enabled ? "Pause" : "Resume";
        if (!enabled)
            return;

        _players.Clear();
        foreach (string name in _playerSnapshot)
            _players.Add(name);
    }

    private void SetEventLiveFollow(bool enabled)
    {
        _eventLiveFollow = enabled;
        EventFollowButton.Content = enabled ? "Pause" : "Resume";
        if (enabled)
        {
            _events.Clear();
            foreach (EventRow eventRow in _eventHistory)
                _events.Add(eventRow);
        }

        EventCountText.Text = $"{_eventHistory.Count:N0} Events{(enabled ? string.Empty : "  /  PAUSED")}";
    }

    private void ApplyEntityFilter()
    {
        string filter = EntitySearchBox?.SearchText?.Trim() ?? string.Empty;
        EntityRow[] rows = string.IsNullOrWhiteSpace(filter)
            ? _entitySnapshot
            : _entitySnapshot.Where(row =>
                row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                row.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                row.Cell.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (!_entityLiveFollow)
            return;

        bool changed = _entities.Count != rows.Length;
        if (!changed)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                if (!_entities[i].HasSameDisplay(rows[i]))
                {
                    changed = true;
                    break;
                }
            }
        }

        if (!changed)
            return;

        string? selectedId = (EntityList.SelectedItem as EntityRow)?.Id;
        _entities.Clear();
        foreach (EntityRow row in rows)
            _entities.Add(row);

        EntityList.SelectedItem = selectedId is null ? null : _entities.FirstOrDefault(row => row.Id == selectedId);
    }

    private void UpdatePlayers(bool mapLoaded)
    {
        string[] names = mapLoaded ? _bot.Map.PlayerNames?.Where(static name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static name => name).ToArray() ?? [] : [];
        _playerSnapshot = names;
        RosterMetric.Text = $"Players in instance: {names.Length:N0}";
        if (_lastPlayerCount != names.Length)
        {
            if (_lastPlayerCount >= 0)
                AddEvent("World Roster", $"{names.Length} players in the current instance");
            _lastPlayerCount = names.Length;
        }

        if (!_playerLiveFollow || _players.SequenceEqual(names))
            return;

        _players.Clear();
        foreach (string name in names)
            _players.Add(name);
    }

    private void UpdateAuras(bool loggedIn)
    {
        AuraSnapshot[] snapshots = loggedIn ? _bot.Self.Snapshots.ToArray() : [];
        AuraRow[] rows = snapshots
            .OrderBy(static aura => aura.Name)
            .Select(aura => new AuraRow
            {
                Name = aura.Name,
                Stacks = aura.Stacks > 1 ? $"×{aura.Stacks}" : aura.Persistent ? "Passive" : "Active",
                Duration = aura.Persistent || aura.Duration <= 0 ? "Persistent effect" : $"{Math.Max(0, aura.RemainingTime):0}s remaining"
            })
            .ToArray();

        AuraMetric.Text = $"Self effects: {rows.Length:N0}  /  Target: {_bot.Target.Snapshots.Count:N0}";
        string signature = string.Join("|", rows.Select(static row => $"{row.Name}:{row.Stacks}:{row.Duration}"));
        if (_lastAuraSignature.Length > 0 && _lastAuraSignature != signature)
            AddEvent("Aura Field", $"{rows.Length} self effects currently visible");
        _lastAuraSignature = signature;

        if (_auras.Count == rows.Length && _auras.Zip(rows).All(static pair => pair.First.HasSameDisplay(pair.Second)))
            return;

        _auras.Clear();
        foreach (AuraRow row in rows)
            _auras.Add(row);
    }

    private void UpdateSkills(bool loggedIn)
    {
        SkillInfo[] skills = loggedIn ? _bot.Player.Skills?.ToArray() ?? [] : [];
        SkillRow[] rows = skills.Take(5).Select((skill, index) => new SkillRow
        {
            Slot = index == 0 ? "Aa" : (index + 1).ToString(),
            Name = string.IsNullOrWhiteSpace(skill.Name) ? $"Skill {index + 1}" : skill.Name,
            Cooldown = $"Cd {(skill.Cooldown / 1000d):0.#}s",
            Description = $"MP {skill.MP:N0}  /  Range {skill.Range:N0}  /  {skill.Type}"
        }).ToArray();

        SkillMetric.Text = loggedIn
            ? $"Engine: {(_bot.Skills.TimerRunning ? "RUNNING" : "IDLE")}  /  {_bot.Skills.SkillInterval} ms  /  {_bot.Skills.SkillUseMode}"
            : "Combat engine: offline";

        if (_skills.Count == rows.Length && _skills.Zip(rows).All(static pair => pair.First.HasSameDisplay(pair.Second)))
            return;

        _skills.Clear();
        foreach (SkillRow row in rows)
            _skills.Add(row);
    }

    private void UpdateSessionAnalytics(bool loggedIn)
    {
        TimeSpan sessionTime = _bot.Stats.SessionTime;
        SessionTimeMetric.Text = sessionTime.ToString(@"hh\:mm\:ss");
        KillsMetric.Text = loggedIn ? _bot.Stats.Kills.ToString("N0") : "0";
        DropsMetric.Text = loggedIn ? _bot.Stats.Drops.ToString("N0") : "0";
        DeathsMetric.Text = loggedIn ? _bot.Stats.Deaths.ToString("N0") : "0";
        KpmMetric.Text = loggedIn ? _bot.Stats.KillsPerMinute.ToString("N1") : "0.0";

        int inventoryUsed = loggedIn ? _bot.Stats.InventoryFilledSpace : 0;
        int inventoryCapacity = loggedIn ? _bot.Stats.InventorySpace : 0;
        InventoryMetric.Text = loggedIn ? $"{inventoryUsed:N0} / {inventoryCapacity:N0} slots" : "-- / --";
        SetProgress(InventoryProgressBar, inventoryUsed, inventoryCapacity);

        InventoryRow[] inventoryRows = loggedIn
            ? _bot.Inventory.Items
                .OrderByDescending(static item => item.Equipped)
                .ThenBy(static item => item.Name)
                .Take(12)
                .Select(item => new InventoryRow
                {
                    Name = $"{(item.Equipped ? "EQUIPPED  /  " : string.Empty)}{(_bot.Inventory.IsFavorited(item.ID) ? "FAV  /  " : string.Empty)}{item.Name}",
                    Quantity = $"×{item.Quantity:N0}"
                })
                .ToArray()
            : [];
        if (_inventory.Count != inventoryRows.Length || !_inventory.Zip(inventoryRows).All(static pair => pair.First.HasSameDisplay(pair.Second)))
        {
            _inventory.Clear();
            foreach (InventoryRow row in inventoryRows)
                _inventory.Add(row);
        }

        if (loggedIn && _lastKillCount != _bot.Stats.Kills)
        {
            if (_lastKillCount >= 0)
                AddEvent("Combat Metrics", $"{_bot.Stats.Kills - _lastKillCount:+#;-#;0} kills · {_bot.Stats.KillsPerMinute:N1}/min");
            _lastKillCount = _bot.Stats.Kills;
        }
    }

    private void EntityList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (EntityList.SelectedItem is EntityRow selected)
        {
            ShowEntityDetails(selected);
            return;
        }

        SelectedEntityText.Text = "Select a monster to inspect its live properties.";
        SelectedEntityDetailsText.Text = "Read-only entity inspection";
    }

    private void ShowEntityDetails(EntityRow row)
    {
        Monster? monster = row.Source;
        if (monster is null)
            return;

        SelectedEntityText.Text = $"{monster.Name}  /  ID {monster.ID}";
        SelectedEntityDetailsText.Text =
            $"Race: {NullLabel(monster.Race)}   Map ID: {monster.MapID}   State: {monster.State}\n" +
            $"HP: {monster.HP:N0} / {monster.MaxHP:N0}   Cell: {NullLabel(monster.Cell)}\n" +
            $"Client asset: {NullLabel(monster.FileName)}";
    }

    private void TrackChange(ref string previous, string current, string category)
    {
        if (previous.Length > 0 && previous != current)
            AddEvent(category, current);

        previous = current;
    }

    private void AddEvent(string category, string detail)
    {
        EventRow eventRow = new()
        {
            Time = DateTime.Now.ToString("HH:mm:ss.fff"),
            Category = category,
            Detail = detail
        };

        _eventHistory.Insert(0, eventRow);
        while (_eventHistory.Count > 250)
            _eventHistory.RemoveAt(_eventHistory.Count - 1);

        if (_eventLiveFollow)
        {
            _events.Insert(0, eventRow);
            while (_events.Count > 250)
                _events.RemoveAt(_events.Count - 1);
        }

        EventCountText.Text = $"{_eventHistory.Count:N0} Events{(_eventLiveFollow ? string.Empty : "  /  PAUSED")}";
    }

    private static void SetProgress(Border bar, double current, double maximum)
    {
        double ratio = maximum <= 0 ? 0 : Math.Clamp(current / maximum, 0, 1);
        if (bar.Parent is Border track)
            bar.Width = Math.Max(0, track.Bounds.Width * ratio);
    }

    private static string GetRecordActionHint(GameDataRecord record) => record.Category switch
    {
        "QUEST" => "This is a saved quest definition. Use Check Quest to inspect the live character state, Accept Quest when available, or Turn In Quest when all requirements are met.",
        "SHOP" => "This item belongs to the shop shown above. Load Shop opens that shop in the game. Check Requirements inspects the live client-side purchase gates. Loading a shop does not buy the item.",
        "MAP ITEM" => "This is a map-item to quest association read from saved map data. It is a reference, not an action button. Use the quest ID to find its quest record.",
        "ITEM REFERENCE" => "This item ID is referenced by quests, rewards, shops, or runtime events. Select the related quest or shop result to use its available actions.",
        "DROP" => "This is a drop observation or drop-related record, not a guaranteed loot table. It does not identify a verified source unless the details explicitly say so.",
        "MONSTER" => "This is a monster snapshot or kill observation. It describes data Skua has seen, not a complete server-side monster database.",
        "ACHIEVEMENT" => "This is achievement-related client or cache information. Complete server-side achievement requirements are not exposed here.",
        _ when record.Category.EndsWith("SOURCE", StringComparison.Ordinal) || record.Category == "SWF SOURCE" => "This is decompiled AQW client source indexed for searching. It is reference material for investigation, not a directly executable game action.",
        _ => "This is reference data. Its details show what OMNISCOPE found and where the information came from; no direct game action is available for this record."
    };

    private static string GetPlayerState(int state) => state switch
    {
        0 => "Dead",
        1 => "Idle",
        2 => "In Combat",
        _ => $"State {state}"
    };

    private static string NullLabel(string? value) => string.IsNullOrWhiteSpace(value) ? "Unknown" : value;

    private sealed class EntityRow
    {
        public string Name { get; init; } = string.Empty;
        public string Cell { get; init; } = string.Empty;
        public string Health { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string Id { get; init; } = string.Empty;
        public Monster? Source { get; init; }

        public bool HasSameDisplay(EntityRow other) =>
            Name == other.Name && Cell == other.Cell && Health == other.Health &&
            State == other.State && Id == other.Id;
    }

    private sealed class AuraRow
    {
        public string Name { get; init; } = string.Empty;
        public string Stacks { get; init; } = string.Empty;
        public string Duration { get; init; } = string.Empty;

        public bool HasSameDisplay(AuraRow other) =>
            Name == other.Name && Stacks == other.Stacks && Duration == other.Duration;
    }

    private sealed class SkillRow
    {
        public string Slot { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Cooldown { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;

        public bool HasSameDisplay(SkillRow other) =>
            Slot == other.Slot && Name == other.Name && Cooldown == other.Cooldown &&
            Description == other.Description;
    }

    private sealed class InventoryRow
    {
        public string Name { get; init; } = string.Empty;
        public string Quantity { get; init; } = string.Empty;

        public bool HasSameDisplay(InventoryRow other) => Name == other.Name && Quantity == other.Quantity;
    }

    private sealed record DropObservation(
        DateTimeOffset Timestamp,
        int ItemId,
        string Name,
        int ItemQuantity,
        bool AddedToInventory,
        int QuantityNow,
        int? PrecedingMonsterMapId,
        double? SecondsAfterKill);

    private sealed record MonsterKillObservation(DateTimeOffset Timestamp, int MapId);

    private sealed class SavedMapShopIndexEntry
    {
        public SavedMapShopIndexEntry() { }
        public string MapName { get; set; } = string.Empty;
        public string MapFilePath { get; set; } = string.Empty;
        public int[] ShopIds { get; set; } = [];
    }

    private sealed record ShopLoadResult(bool Loaded, int ShopId, string ShopName, string[] ItemLines);

    private sealed class EventRow
    {
        public string Time { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
    }
}
