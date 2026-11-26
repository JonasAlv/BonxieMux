using Avalonia.Interactivity;
using Skua.Core.Models.Items;
using Skua.Core.Models.Monsters;
using Skua.Core.Models.Quests;
using Skua.Core.Models.Shops;
using Skua.Plugin.Omniscope.Services;

namespace Skua.Plugin.Omniscope;

public partial class OmniscopeWindow
{
    private async void ExportData_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            string exportPath = await OmniscopeCatalogService.ExportCatalogAsync(_dataSnapshot, _mapSnapshot);
            DataStatusText.Text = $"Exported {_dataSnapshot.Count:N0} Game Records + {_mapSnapshot.Count:N0} Map References  /  {exportPath}";
        }
        catch (Exception ex)
        {
            DataStatusText.Text = $"Catalog Export Failed  /  {ex.Message}";
        }
    }

    private static GameDataRecord CreateQuestRecord(QuestData quest) => OmniscopeCatalogService.CreateQuestRecord(quest);
    private static GameDataRecord CreateShopRecord(int shopId, string shopName, ShopItem item) => OmniscopeCatalogService.CreateShopRecord(shopId, shopName, item);
    private static GameDataRecord CreateMapShopRecord(int shopId, string mapName, string mapFilePath) => OmniscopeCatalogService.CreateMapShopRecord(shopId, mapName, mapFilePath);
    private static string GetMapScriptName(string? mapName) => OmniscopeCatalogService.GetMapScriptName(mapName);
    private static GameDataRecord CreateMapItemRecord(MapItem mapItem, string questName, string mapKey) => OmniscopeCatalogService.CreateMapItemRecord(mapItem, questName, mapKey);
    private static GameDataRecord CreateDropRecord(ItemBase item) => OmniscopeCatalogService.CreateDropRecord(item);
    private static GameDataRecord CreateObservedDropRecord(DropObservation observation) => OmniscopeCatalogService.CreateObservedDropRecord(observation);
    private static GameDataRecord CreateMonsterRecord(Monster monster) => OmniscopeCatalogService.CreateMonsterRecord(monster);
    private static GameDataRecord CreateStatusRecord(string name, string message, string category) => OmniscopeCatalogService.CreateStatusRecord(name, message, category);
    private static string FormatItems(IEnumerable<ItemBase>? items) => OmniscopeCatalogService.FormatItems(items);
    private static void AddItemReferences(Dictionary<int, List<string>> references, IEnumerable<ItemBase>? items, string source) => OmniscopeCatalogService.AddItemReferences(references, items, source);
    private static void AddItemReference(Dictionary<int, List<string>> references, ItemBase item, string source) => OmniscopeCatalogService.AddItemReference(references, item, source);
}
