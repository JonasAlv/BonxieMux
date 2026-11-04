using System.Text.Json;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Items;

namespace Skua.Core.Services;

public sealed class JunkService : IJunkService
{
    private readonly IScriptInventory _inventory;
    private readonly IScriptShop _shop;
    private readonly object _lock = new();
    private List<JunkItemConfig> _junkItems = new();
    private bool _loaded;

    public JunkService(IScriptInventory inventory, IScriptShop shop)
    {
        _inventory = inventory;
        _shop = shop;
    }

    public IReadOnlyList<JunkItemConfig> JunkItems
    {
        get
        {
            EnsureLoaded();
            lock (_lock)
            {
                return _junkItems.ToList();
            }
        }
    }

    public void Load()
    {
        EnsureLoaded(force: true);
    }

    public void Save()
    {
        EnsureLoaded();
        lock (_lock)
        {
            try
            {
                string json = JsonSerializer.Serialize(_junkItems, GetJsonOptions());
                File.WriteAllText(ClientFileSources.SkuaJunkItemsFile, json);
            }
            catch
            {
                // ignored
            }
        }
    }

    public bool IsJunk(int id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _junkItems.Any(x => x.ID == id);
        }
    }

    public void SetJunk(IEnumerable<JunkItemConfig> items)
    {
        EnsureLoaded();
        lock (_lock)
        {
            _junkItems = items
                .GroupBy(x => x.ID)
                .Select(g => g.First())
                .ToList();
        }
        Save();
    }

    public void SellAllJunk()
    {
        EnsureLoaded();

        List<JunkItemConfig> snapshot;
        lock (_lock)
        {
            snapshot = _junkItems.ToList();
        }

        if (snapshot.Count == 0)
            return;

        var junkIds = new HashSet<int>(snapshot.Select(j => j.ID));

        // Only inventory items can be sold
        var itemsToSell = _inventory.Items
            .Where(i => junkIds.Contains(i.ID) && !i.Equipped)
            .ToList();

        foreach (var item in itemsToSell)
        {
            _shop.SellItem(item.ID);
        }
    }

    private void EnsureLoaded(bool force = false)
    {
        if (_loaded && !force)
            return;

        lock (_lock)
        {
            if (_loaded && !force)
                return;

            try
            {
                if (!File.Exists(ClientFileSources.SkuaJunkItemsFile))
                {
                    _junkItems = new List<JunkItemConfig>();
                }
                else
                {
                    string json = File.ReadAllText(ClientFileSources.SkuaJunkItemsFile);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        _junkItems = new List<JunkItemConfig>();
                    }
                    else
                    {
                        _junkItems = JsonSerializer.Deserialize<List<JunkItemConfig>>(json, GetJsonOptions())
                                     ?? new List<JunkItemConfig>();
                    }
                }
            }
            catch
            {
                _junkItems = new List<JunkItemConfig>();
            }

            _loaded = true;
        }
    }

    private static JsonSerializerOptions GetJsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }
}
