using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Skua.Core.Models.Items;
using Skua.Core.Models.Monsters;
using Skua.Core.Models.Quests;
using Skua.Core.Models.Shops;

namespace Skua.Plugin.Omniscope.Services;

internal sealed record DropObservation(
    DateTimeOffset Timestamp,
    int ItemId,
    string Name,
    int ItemQuantity,
    bool AddedToInventory,
    int QuantityNow,
    int? PrecedingMonsterMapId,
    double? SecondsAfterKill);

/// <summary>
/// UI-free backend service for Omniscope catalog record building and JSON export.
/// </summary>
internal static class OmniscopeCatalogService
{
    public static string NullLabel(string? value) => string.IsNullOrWhiteSpace(value) ? "Unknown" : value;

    public static async Task<string> ExportCatalogAsync(
        IReadOnlyCollection<GameDataRecord> gameData,
        IReadOnlyCollection<GameDataRecord> mapData)
    {
        string exportDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BonxieMux",
            "Omniscope",
            "Exports");
        Directory.CreateDirectory(exportDirectory);
        string exportPath = Path.Combine(exportDirectory, $"omniscope-data-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        object export = new
        {
            ExportedAt = DateTimeOffset.Now,
            GameData = gameData,
            MapData = mapData
        };
        string json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(exportPath, json).ConfigureAwait(false);
        return exportPath;
    }

    public static GameDataRecord CreateQuestRecord(QuestData quest)
    {
        ItemBase[] acceptItems = quest.AcceptRequirements?.ToArray() ?? [];
        ItemBase[] turnInItems = quest.Requirements?.ToArray() ?? [];
        ItemBase[] rewardsItems = quest.Rewards?.ToArray() ?? [];
        SimpleReward[] simpleRewards = quest.SimpleRewards?.ToArray() ?? [];
        string acceptRequirements = FormatItems(acceptItems);
        string turnInRequirements = FormatItems(turnInItems);
        string rewards = FormatItems(rewardsItems);
        string rewardRates = simpleRewards.Length == 0
            ? "No simple reward-rate entries."
            : string.Join(Environment.NewLine, simpleRewards.Select(static item => $"{NullLabel(item.Name)} [ID {item.ID}] ×{item.Quantity} / {item.Rate:0.###}% / type {item.Type}"));
        string details =
            $"Quest: {quest.Name}{Environment.NewLine}" +
            $"Quest ID: {quest.ID}{Environment.NewLine}" +
            $"Level Gate: {quest.Level}{Environment.NewLine}" +
            $"Membership: {(quest.Upgrade ? "Member" : "Free")}{Environment.NewLine}" +
            $"Class Gate: {quest.RequiredClassID} / {quest.RequiredClassPoints:N0} class points{Environment.NewLine}" +
            $"Faction Gate: {quest.RequiredFactionId} / {quest.RequiredFactionRep:N0} reputation{Environment.NewLine}" +
            $"Repeatable: {!quest.Once}{Environment.NewLine}" +
            $"Gold Reward: {quest.Gold:N0}{Environment.NewLine}" +
            $"XP Reward: {quest.XP:N0}{Environment.NewLine}{Environment.NewLine}" +
            $"Accept Requirements:{Environment.NewLine}{acceptRequirements}{Environment.NewLine}{Environment.NewLine}" +
            $"Turn-In Requirements:{Environment.NewLine}{turnInRequirements}{Environment.NewLine}{Environment.NewLine}" +
            $"Rewards:{Environment.NewLine}{rewards}{Environment.NewLine}{Environment.NewLine}" +
            $"Simple Reward Rates:{Environment.NewLine}{rewardRates}";

        return new GameDataRecord
        {
            Category = "QUEST",
            Name = NullLabel(quest.Name),
            Summary = $"Level {quest.Level}  /  {acceptItems.Length:N0} accept items  /  {turnInItems.Length:N0} turn-in items  /  {rewardsItems.Length:N0} rewards",
            Metadata = $"QuestData.json  /  ID {quest.ID}",
            Details = details,
            SearchText = $"{details} {acceptRequirements} {turnInRequirements} {rewards} {rewardRates}",
            SourceQuestId = quest.ID
        };
    }

    public static GameDataRecord CreateShopRecord(int shopId, string shopName, ShopItem item)
    {
        ItemBase[] requirementItems = item.Requirements?.ToArray() ?? [];
        string requirements = FormatItems(requirementItems);
        string currency = item.Coins ? "Adventure Coins" : "Gold";
        string token = item.TokenID > 0 || !string.IsNullOrWhiteSpace(item.TokenName)
            ? $"{item.TokenName} [ID {item.TokenID}] ×{item.TokenQuantity:0.###}"
            : "None";
        string details =
            $"Shop: {shopName} [{shopId}]{Environment.NewLine}" +
            $"Item: {item.Name}{Environment.NewLine}" +
            $"Item ID: {item.ID}  /  Shop Item ID: {item.ShopItemID}{Environment.NewLine}" +
            $"Cost: {item.Cost:N0} {currency}{Environment.NewLine}" +
            $"Required Level: {item.Level}{Environment.NewLine}" +
            $"Faction: {NullLabel(item.Faction)}{Environment.NewLine}" +
            $"Required Reputation: {item.RequiredReputation:N0}{Environment.NewLine}" +
            $"Token: {token}{Environment.NewLine}" +
            $"Merge / Turn-In Materials:{Environment.NewLine}{requirements}";

        return new GameDataRecord
        {
            Category = "SHOP",
            Name = NullLabel(item.Name),
            Summary = $"{item.Cost:N0} {currency}  /  {NullLabel(item.Faction)}  /  {requirementItems.Length:N0} materials",
            Metadata = $"Shop {shopName} [{shopId}]  /  ShopItemID {item.ShopItemID}",
            Details = details,
            SearchText = $"{details} {requirements} {item.TokenName}",
            SourceShopId = shopId,
            SourceObject = item
        };
    }

    public static GameDataRecord CreateMapShopRecord(int shopId, string mapName, string mapFilePath)
    {
        string details =
            $"Map: /{mapName}{Environment.NewLine}" +
            $"Map SWF: {mapFilePath}{Environment.NewLine}" +
            $"Shop ID: {shopId}{Environment.NewLine}{Environment.NewLine}" +
            "This shop ID was extracted from ActionScript in the map SWF. Load Shop opens it without buying anything. " +
            "Generate Merge Shop runs MergeTemplateHelperV3 with this map and shop ID prefilled.";
        return new GameDataRecord
        {
            Category = "MAP SHOP",
            Name = $"Shop {shopId}",
            Summary = $"Shop ID {shopId}  /  /{mapName}",
            Metadata = mapFilePath,
            Details = details,
            SearchText = $"{details} {shopId} {mapName} {mapFilePath}",
            SourceShopId = shopId,
            SourceMapName = mapName
        };
    }

    public static string GetMapScriptName(string? mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
            return string.Empty;

        string value = mapName.Trim().Replace('\\', '/');
        int mapsIndex = value.LastIndexOf("maps/", StringComparison.OrdinalIgnoreCase);
        if (mapsIndex >= 0)
            value = value[(mapsIndex + 5)..];

        string map = Path.GetFileNameWithoutExtension(value).Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(map) ? value.ToLowerInvariant() : map;
    }

    public static GameDataRecord CreateMapItemRecord(MapItem mapItem, string questName, string mapKey)
    {
        string mapName = string.IsNullOrWhiteSpace(mapItem.MapName) ? mapKey : mapItem.MapName;
        string mapFilePath = string.IsNullOrWhiteSpace(mapItem.MapFilePath) ? mapKey : mapItem.MapFilePath;
        string details =
            $"Map Item ID: {mapItem.ID}{Environment.NewLine}" +
            $"Quest ID: {mapItem.QuestID}{Environment.NewLine}" +
            $"Quest Name: {questName}{Environment.NewLine}" +
            $"Map: {mapName}{Environment.NewLine}" +
            $"Map File: {mapFilePath}{Environment.NewLine}" +
            $"{Environment.NewLine}This association was extracted from BonxieMux's saved map-SWF index.";
        return new GameDataRecord
        {
            Category = "MAP ITEM",
            Name = $"Map link #{mapItem.ID}",
            Summary = $"Quest {mapItem.QuestID}  /  {mapName}",
            Metadata = mapFilePath,
            Details = details,
            SearchText = $"{details} {mapItem.ID} {mapItem.QuestID} {questName} {mapName} {mapKey}",
            SourceMapName = GetMapScriptName(mapName),
            SourceObject = mapItem
        };
    }

    public static GameDataRecord CreateDropRecord(ItemBase item)
    {
        string details =
            $"Current Drop: {NullLabel(item.Name)}{Environment.NewLine}" +
            $"Item ID: {item.ID}{Environment.NewLine}" +
            $"Quantity: {item.Quantity:N0}{Environment.NewLine}" +
            $"Category: {item.CategoryString}{Environment.NewLine}" +
            $"Temporary: {item.Temp}{Environment.NewLine}" +
            $"AC Item: {item.Coins}{Environment.NewLine}" +
            $"{Environment.NewLine}This is an observed runtime drop queue entry, not a complete monster drop table.";
        return new GameDataRecord
        {
            Category = "DROP",
            Name = NullLabel(item.Name),
            Summary = $"Observed quantity ×{item.Quantity:N0}  /  {item.CategoryString}",
            Metadata = "Runtime drop queue",
            Details = details,
            SearchText = details
        };
    }

    public static GameDataRecord CreateObservedDropRecord(DropObservation observation)
    {
        string killContext = observation.PrecedingMonsterMapId.HasValue
            ? $"Preceding kill MapID: {observation.PrecedingMonsterMapId.Value} / {observation.SecondsAfterKill:0.0}s earlier (temporal correlation only)"
            : "No recent monster-kill event was observed within the 12-second context window.";
        string details =
            $"Observed Drop: {observation.Name}{Environment.NewLine}" +
            $"Item ID: {observation.ItemId}{Environment.NewLine}" +
            $"Item Quantity: {observation.ItemQuantity:N0}{Environment.NewLine}" +
            $"Added To Inventory: {observation.AddedToInventory}{Environment.NewLine}" +
            $"Inventory Quantity After Event: {observation.QuantityNow:N0}{Environment.NewLine}" +
            $"Timestamp: {observation.Timestamp:O}{Environment.NewLine}" +
            $"{killContext}{Environment.NewLine}{Environment.NewLine}" +
            "A nearby kill is only temporal context. The game event does not identify the exact monster responsible for this item.";
        return new GameDataRecord
        {
            Category = "DROP",
            Name = NullLabel(observation.Name),
            Summary = $"{observation.Timestamp:HH:mm:ss}  /  ×{observation.ItemQuantity:N0}  /  Added: {observation.AddedToInventory}",
            Metadata = "Runtime ItemDropped event history",
            Details = details,
            SearchText = details
        };
    }

    public static GameDataRecord CreateMonsterRecord(Monster monster)
    {
        string details =
            $"Monster: {monster.Name}{Environment.NewLine}" +
            $"Entity ID: {monster.ID}{Environment.NewLine}" +
            $"Map ID: {monster.MapID}{Environment.NewLine}" +
            $"Race: {NullLabel(monster.Race)}{Environment.NewLine}" +
            $"Cell: {NullLabel(monster.Cell)}{Environment.NewLine}" +
            $"State: {monster.State}  /  Alive: {monster.Alive}{Environment.NewLine}" +
            $"Health: {monster.HP:N0} / {monster.MaxHP:N0}{Environment.NewLine}" +
            $"Client Asset: {NullLabel(monster.FileName)}{Environment.NewLine}{Environment.NewLine}" +
            "A complete mob loot table is not supplied by the public monster snapshot.";
        return new GameDataRecord
        {
            Category = "MONSTER",
            Name = NullLabel(monster.Name),
            Summary = $"Map ID {monster.MapID}  /  {NullLabel(monster.Cell)}  /  {(monster.Alive ? "ALIVE" : "DOWN")}",
            Metadata = "Current map monster snapshot",
            Details = details,
            SearchText = details
        };
    }

    public static GameDataRecord CreateStatusRecord(string name, string message, string category)
    {
        return new GameDataRecord
        {
            Category = category,
            Name = name,
            Summary = message,
            Metadata = "Source status",
            Details = message,
            SearchText = $"{name} {message}"
        };
    }

    public static string FormatItems(IEnumerable<ItemBase>? items)
    {
        if (items is null)
            return "None";

        string[] lines = items
            .Where(static item => item is not null)
            .Select(static item => $"{NullLabel(item.Name)} [ID {item.ID}] ×{item.Quantity:N0}")
            .ToArray();
        return lines.Length == 0 ? "None" : string.Join(Environment.NewLine, lines);
    }

    public static void AddItemReferences(Dictionary<int, List<string>> references, IEnumerable<ItemBase>? items, string source)
    {
        if (items is null)
            return;

        foreach (ItemBase item in items)
        {
            if (item is not null)
                AddItemReference(references, item, source);
        }
    }

    public static void AddItemReference(Dictionary<int, List<string>> references, ItemBase item, string source)
    {
        if (item.ID <= 0)
            return;

        if (!references.TryGetValue(item.ID, out List<string>? entries) || entries is null)
        {
            entries = new List<string>();
            references[item.ID] = entries;
        }

        entries.Add($"{NullLabel(item.Name)} / {source}");
    }
}
