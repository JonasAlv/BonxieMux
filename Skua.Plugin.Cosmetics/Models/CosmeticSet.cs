using System;
using Newtonsoft.Json;

namespace Skua.Plugin.Cosmetics.Models;

public class CosmeticSet
{
    [JsonProperty("name")]
    public string Name { get; set; } = "Default Outfit";

    [JsonProperty("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonProperty("gender")]
    public char? Gender { get; set; }

    [JsonProperty("helm")]
    public CosmeticItem? Helm { get; set; }

    [JsonProperty("armor")]
    public CosmeticItem? Armor { get; set; }

    [JsonProperty("cape")]
    public CosmeticItem? Cape { get; set; }

    [JsonProperty("weapon")]
    public CosmeticItem? Weapon { get; set; }

    [JsonProperty("pet")]
    public CosmeticItem? Pet { get; set; }

    [JsonProperty("misc")]
    public CosmeticItem? Misc { get; set; }

    [JsonIgnore]
    public bool HasAnyItem => Armor != null || Helm != null || Cape != null || Weapon != null || Pet != null || Misc != null;

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();
            if (Armor != null) parts.Add($"Armor: {Armor.Name}");
            if (Helm != null) parts.Add($"Helm: {Helm.Name}");
            if (Cape != null) parts.Add($"Cape: {Cape.Name}");
            if (Weapon != null) parts.Add($"Weapon: {Weapon.Name}");
            if (Pet != null) parts.Add($"Pet: {Pet.Name}");
            if (Misc != null) parts.Add($"Rune: {Misc.Name}");
            return parts.Count > 0 ? string.Join(" • ", parts) : "Empty Loadout";
        }
    }

    public void Clear()
    {
        Armor = null;
        Helm = null;
        Cape = null;
        Weapon = null;
        Pet = null;
        Misc = null;
    }
}
