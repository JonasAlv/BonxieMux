using System;
using Newtonsoft.Json;
using Skua.Core.Models.Items;

namespace Skua.Plugin.Cosmetics.Models;

public class CosmeticItem : IEquatable<CosmeticItem>
{
    private ItemCategory? _category;
    private string? _displayCategory;

    [JsonProperty("id")]
    public int ID { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("path")]
    public string Path { get; set; } = string.Empty;

    [JsonProperty("link")]
    public string Link { get; set; } = string.Empty;

    [JsonProperty("itemGroup")]
    public string ItemGroup { get; set; } = string.Empty;

    [JsonProperty("category")]
    public string CategoryString { get; set; } = string.Empty;

    [JsonProperty("meta")]
    public string Meta { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsFavorite { get; set; }

    [JsonIgnore]
    public ItemCategory Category
    {
        get
        {
            if (_category.HasValue)
                return _category.Value;

            if (Enum.TryParse<ItemCategory>(CategoryString, true, out var cat))
                _category = cat;
            else if (ItemGroup == "Weapon")
                _category = ItemCategory.Sword;
            else
                _category = ItemCategory.Unknown;

            return _category.Value;
        }
    }

    [JsonIgnore]
    public string DisplayCategory
    {
        get
        {
            if (_displayCategory != null)
                return _displayCategory;

            if (ItemGroup != "Weapon")
            {
                _displayCategory = Category == ItemCategory.Misc ? "Ground Rune" : (string.IsNullOrWhiteSpace(CategoryString) ? ItemGroup : CategoryString);
            }
            else
            {
                _displayCategory = Category == ItemCategory.HandGun ? "Weapon (Hand Gun)" : $"Weapon ({CategoryString})";
            }

            return _displayCategory;
        }
    }

    public CosmeticItem()
    {
    }

    public CosmeticItem(string name, int id, string path, string category, string itemGroup, string link, string meta)
    {
        Name = name ?? string.Empty;
        ID = id;
        Path = path ?? string.Empty;
        CategoryString = category ?? string.Empty;
        ItemGroup = itemGroup ?? string.Empty;
        Link = link ?? string.Empty;
        Meta = meta ?? string.Empty;
    }

    public CosmeticItem(ItemBase item)
    {
        Name = item.Name ?? string.Empty;
        ID = item.ID;
        Path = item.FileLink ?? string.Empty;
        CategoryString = item.CategoryString ?? string.Empty;
        ItemGroup = item.ItemGroup ?? string.Empty;
        Link = item.FileName ?? string.Empty;
        Meta = item.Meta ?? string.Empty;
    }

    public bool Equals(CosmeticItem? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return ID == other.ID && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj) => Equals(obj as CosmeticItem);

    public override int GetHashCode() => (ID * 397) ^ (Path != null ? StringComparer.OrdinalIgnoreCase.GetHashCode(Path) : 0);

    public override string ToString() => $"{Name} [{ID}] ({DisplayCategory})";
}
