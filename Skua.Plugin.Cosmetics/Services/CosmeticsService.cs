using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Items;
using Skua.Plugin.Cosmetics.Models;

namespace Skua.Plugin.Cosmetics.Services;

public class CosmeticsService : IDisposable
{
    public const string OpenSheetApiUrl = "https://opensheet.elk.sh/14H2J2nd8Pp-6Mof6rDIPjCPsE-iHEzkO_CC9OA0O0Us/Data";

    private readonly IScriptInterface _bot;
    private static readonly HttpClient _httpClient = CreateHttpClient();

    private readonly string _storageDir;
    private readonly string _catalogFile;
    private readonly string _favoritesFile;
    private readonly string _setsFile;

    private readonly HashSet<string> _favoriteKeys = new(StringComparer.OrdinalIgnoreCase);

    public List<CosmeticItem> AllItems { get; private set; } = new();
    public List<CosmeticItem> FavoriteItems { get; private set; } = new();
    public ObservableCollection<CosmeticSet> SavedSets { get; private set; } = new();

    // Pre-indexed category caches for 0ms tab switching
    public List<CosmeticItem> ArmorItems { get; private set; } = new();
    public List<CosmeticItem> WeaponItems { get; private set; } = new();
    public List<CosmeticItem> HelmItems { get; private set; } = new();
    public List<CosmeticItem> CapeItems { get; private set; } = new();
    public List<CosmeticItem> PetItems { get; private set; } = new();
    public List<CosmeticItem> RuneItems { get; private set; } = new();

    public bool IsCatalogLoaded { get; private set; }
    private readonly SemaphoreSlim _catalogLoadLock = new(1, 1);

    /// <summary>
    /// Remembers the player's true character colors before any cosmetic/target overrides were applied.
    /// </summary>
    public (int baseColor, int trimColor, int accColor, int eyeColor, int skinColor, int hairColor)? OriginalColors { get; private set; }

    /// <summary>
    /// Remembers the player's true character gender before any cosmetic/morph overrides were applied.
    /// </summary>
    public char? OriginalGender { get; private set; }

    /// <summary>
    /// Tracks active cosmetic overrides currently displayed on the player's avatar.
    /// </summary>
    public CosmeticSet CurrentVisualSet { get; private set; } = new() { Name = "Active Look" };

    public event Action? CatalogUpdated;
    public event Action? FavoritesUpdated;
    public event Action? SetsUpdated;
    public event Action? VisualsUpdated;

    public CosmeticsService(IScriptInterface bot)
    {
        _bot = bot;
        _storageDir = Path.Combine(ClientFileSources.SkuaOptionsDIR, "Cosmetics");
        Directory.CreateDirectory(_storageDir);

        _catalogFile = Path.Combine(_storageDir, "CosmeticsCatalog.json");
        _favoritesFile = Path.Combine(_storageDir, "FavoritedItems.json");
        _setsFile = Path.Combine(_storageDir, "SavedSets.json");

        LoadLightweightState();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        return client;
    }

    private void LoadLightweightState()
    {
        try
        {
            if (File.Exists(_favoritesFile))
            {
                var favs = JsonConvert.DeserializeObject<List<CosmeticItem>>(File.ReadAllText(_favoritesFile));
                if (favs != null)
                {
                    FavoriteItems = favs;
                    foreach (var f in favs)
                    {
                        f.IsFavorite = true;
                        _favoriteKeys.Add(GetItemKey(f.ID, f.Path));
                    }
                }
            }

            if (File.Exists(_setsFile))
            {
                var sets = JsonConvert.DeserializeObject<List<CosmeticSet>>(File.ReadAllText(_setsFile));
                if (sets != null)
                {
                    SavedSets.Clear();
                    foreach (var s in sets)
                        SavedSets.Add(s);
                }
            }
        }
        catch (Exception ex)
        {
            _bot.Log($"[CosmeticsService] Error loading favorites/sets: {ex.Message}");
        }
    }

    public void CaptureOriginalStateIfEmpty()
    {
        if (!_bot.Player.LoggedIn) return;

        if (!OriginalColors.HasValue)
        {
            var colors = GetRealServerOrAvatarColors();
            if (colors.baseColor != 0 || colors.trimColor != 0 || colors.accColor != 0 || colors.eyeColor != 0 || colors.skinColor != 0 || colors.hairColor != 0)
            {
                OriginalColors = colors;
            }
        }

        if (!OriginalGender.HasValue)
        {
            char g = GetRealServerGender();
            if (g == 'M' || g == 'F')
            {
                OriginalGender = g;
            }
        }
    }

    public void CaptureOriginalColorsIfEmpty() => CaptureOriginalStateIfEmpty();

    public char GetRealServerGender()
    {
        if (!_bot.Player.LoggedIn) return 'M';

        try
        {
            string g = _bot.Flash.GetGameObject<string>("world.myAvatar.dataLeaf.strGender") ?? "";
            if (!string.IsNullOrWhiteSpace(g) && (g[0] == 'M' || g[0] == 'F' || g[0] == 'm' || g[0] == 'f'))
                return char.ToUpperInvariant(g[0]);
        }
        catch { }

        return GetCurrentGender();
    }

    public char GetCurrentGender()
    {
        if (!_bot.Player.LoggedIn) return 'M';

        try
        {
            string cur = _bot.Flash.Call("gender")?.Trim('"', ' ', '\'') ?? "";
            if (cur.Length > 0 && (cur[0] == 'M' || cur[0] == 'F' || cur[0] == 'm' || cur[0] == 'f'))
                return char.ToUpperInvariant(cur[0]);

            string objG = _bot.Flash.GetGameObject<string>("world.myAvatar.objData.strGender") ?? "";
            if (!string.IsNullOrWhiteSpace(objG) && (objG[0] == 'M' || objG[0] == 'F' || objG[0] == 'm' || objG[0] == 'f'))
                return char.ToUpperInvariant(objG[0]);

            string dataG = _bot.Flash.GetGameObject<string>("world.myAvatar.dataLeaf.strGender") ?? "";
            if (!string.IsNullOrWhiteSpace(dataG) && (dataG[0] == 'M' || dataG[0] == 'F' || dataG[0] == 'm' || dataG[0] == 'f'))
                return char.ToUpperInvariant(dataG[0]);
        }
        catch { }

        return 'M';
    }

    public (int baseColor, int trimColor, int accColor, int eyeColor, int skinColor, int hairColor) GetRealServerOrAvatarColors()
    {
        if (!_bot.Player.LoggedIn) return (0, 0, 0, 0, 0, 0);

        try
        {
            // In AQW, dataLeaf retains the server-sent user variables (immutable from client loadMovieAtES)
            int b = _bot.Flash.GetGameObject<int>("world.myAvatar.dataLeaf.intColorBase");
            int t = _bot.Flash.GetGameObject<int>("world.myAvatar.dataLeaf.intColorTrim");
            int a = _bot.Flash.GetGameObject<int>("world.myAvatar.dataLeaf.intColorAccessory");
            int e = _bot.Flash.GetGameObject<int>("world.myAvatar.dataLeaf.intColorEye");
            int s = _bot.Flash.GetGameObject<int>("world.myAvatar.dataLeaf.intColorSkin");
            int h = _bot.Flash.GetGameObject<int>("world.myAvatar.dataLeaf.intColorHair");

            if (b != 0 || t != 0 || a != 0 || e != 0 || s != 0 || h != 0)
                return (b, t, a, e, s, h);
        }
        catch { }

        return GetCurrentColors();
    }

    public async Task EnsureCatalogLoadedAsync(IProgress<string>? progress = null)
    {
        if (IsCatalogLoaded) return;

        await _catalogLoadLock.WaitAsync();
        try
        {
            if (IsCatalogLoaded) return;

            if (File.Exists(_catalogFile))
            {
                progress?.Report("Streaming local catalog...");

                await Task.Run(() =>
                {
                    using var stream = File.OpenRead(_catalogFile);
                    using var reader = new StreamReader(stream);
                    using var jsonReader = new JsonTextReader(reader);
                    var serializer = JsonSerializer.CreateDefault();
                    var items = serializer.Deserialize<List<CosmeticItem>>(jsonReader);

                    if (items != null && items.Count > 0)
                    {
                        ApplyFavorites(items);
                        AllItems = items.OrderByDescending(x => x.ID).ToList();
                        RebuildCategoryIndices();
                        IsCatalogLoaded = true;
                    }
                });

                CatalogUpdated?.Invoke();
            }
            else
            {
                await CheckAndUpdateCatalogAsync(progress);
            }
        }
        catch (Exception ex)
        {
            _bot.Log($"[CosmeticsService] Error loading catalog: {ex.Message}");
        }
        finally
        {
            _catalogLoadLock.Release();
        }
    }

    private void RebuildCategoryIndices()
    {
        ArmorItems = AllItems.Where(x => x.Category == ItemCategory.Armor || x.Category == ItemCategory.Class || x.ItemGroup == "co" || x.ItemGroup == "ar").ToList();
        WeaponItems = AllItems.Where(x => x.ItemGroup == "Weapon").ToList();
        HelmItems = AllItems.Where(x => x.Category == ItemCategory.Helm || x.ItemGroup == "he").ToList();
        CapeItems = AllItems.Where(x => x.Category == ItemCategory.Cape || x.ItemGroup == "ba").ToList();
        PetItems = AllItems.Where(x => x.Category == ItemCategory.Pet || x.ItemGroup == "pe").ToList();
        RuneItems = AllItems.Where(x => x.Category == ItemCategory.Misc || x.ItemGroup == "mi").ToList();
    }

    public static string GetItemKey(int id, string? path) => $"{id}_{path?.Trim().ToLowerInvariant()}";

    private void ApplyFavorites(List<CosmeticItem> items)
    {
        foreach (var item in items)
        {
            item.IsFavorite = _favoriteKeys.Contains(GetItemKey(item.ID, item.Path));
        }
    }

    public async Task<(bool success, string message, int newItemsCount)> CheckAndUpdateCatalogAsync(IProgress<string>? progress = null)
    {
        progress?.Report("Checking for updates...");

        try
        {
            using var response = await _httpClient.GetAsync(OpenSheetApiUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            using var jsonReader = new JsonTextReader(reader);
            var serializer = JsonSerializer.CreateDefault();
            var remoteList = serializer.Deserialize<List<CosmeticItem>>(jsonReader);

            if (remoteList == null || remoteList.Count == 0)
            {
                progress?.Report("Update check completed: No items returned.");
                return (false, "Invalid catalog format.", 0);
            }

            int currentCount = AllItems.Count;
            int newCount = remoteList.Count;

            // Only update when remote actually has new items or local database is empty
            if (newCount > currentCount || currentCount == 0)
            {
                int diff = Math.Max(0, newCount - currentCount);
                ApplyFavorites(remoteList);
                AllItems = remoteList.OrderByDescending(x => x.ID).ToList();
                RebuildCategoryIndices();
                IsCatalogLoaded = true;

                await Task.Run(() =>
                {
                    using var fileStream = File.Create(_catalogFile);
                    using var writer = new StreamWriter(fileStream);
                    using var jsonWriter = new JsonTextWriter(writer);
                    serializer.Serialize(jsonWriter, AllItems);
                });

                string msg = currentCount == 0
                    ? $"Loaded {AllItems.Count:N0} cosmetics to local catalog."
                    : $"Catalog updated! (+{diff:N0} new items, Total: {AllItems.Count:N0})";

                progress?.Report(msg);
                CatalogUpdated?.Invoke();
                return (true, msg, diff);
            }
            else
            {
                string msg = $"Catalog is up to date ({AllItems.Count:N0} items).";
                progress?.Report(msg);
                return (true, msg, 0);
            }
        }
        catch (Exception ex)
        {
            string msg = $"Update check failed: {ex.Message}";
            progress?.Report(msg);
            return (false, msg, 0);
        }
    }

    public void ToggleFavorite(CosmeticItem item)
    {
        string key = GetItemKey(item.ID, item.Path);
        if (_favoriteKeys.Contains(key))
        {
            _favoriteKeys.Remove(key);
            item.IsFavorite = false;
            FavoriteItems.RemoveAll(x => GetItemKey(x.ID, x.Path) == key);
        }
        else
        {
            _favoriteKeys.Add(key);
            item.IsFavorite = true;
            FavoriteItems.Add(item);
        }

        SaveFavorites();
        FavoritesUpdated?.Invoke();
    }

    public void SaveFavorites()
    {
        try
        {
            File.WriteAllText(_favoritesFile, JsonConvert.SerializeObject(FavoriteItems, Formatting.Indented));
        }
        catch (Exception ex)
        {
            _bot.Log($"[CosmeticsService] Error saving favorites: {ex.Message}");
        }
    }

    public void AddSavedSet(CosmeticSet set)
    {
        SavedSets.Add(set);
        SaveSets();
        SetsUpdated?.Invoke();
    }

    public void RemoveSavedSet(CosmeticSet set)
    {
        if (SavedSets.Contains(set))
        {
            SavedSets.Remove(set);
            SaveSets();
            SetsUpdated?.Invoke();
        }
    }

    public void SaveSets()
    {
        try
        {
            File.WriteAllText(_setsFile, JsonConvert.SerializeObject(SavedSets, Formatting.Indented));
        }
        catch (Exception ex)
        {
            _bot.Log($"[CosmeticsService] Error saving sets: {ex.Message}");
        }
    }

    public void Equip(CosmeticItem item)
    {
        if (!_bot.Player.LoggedIn)
        {
            _bot.Log("[Cosmetics] Cannot equip: You are not logged in.");
            return;
        }

        CaptureOriginalColorsIfEmpty();

        try
        {
            dynamic val = new ExpandoObject();
            val.sFile = item.Path;
            val.sLink = item.Link;
            val.sType = (item.Category == ItemCategory.Armor || item.Category == ItemCategory.Class) ? "Armor" : item.CategoryString;
            val.sMeta = item.Meta ?? string.Empty;
            string itemGroup = (item.ItemGroup == "ar") ? "co" : item.ItemGroup;

            _bot.Flash.SetGameObject($"world.myAvatar.objData.eqp[{itemGroup}]", val);
            _bot.Flash.CallGameFunction("world.myAvatar.loadMovieAtES", itemGroup, val.sFile, val.sLink);

            switch (itemGroup.ToLowerInvariant())
            {
                case "co":
                case "ar":
                    CurrentVisualSet.Armor = item;
                    break;
                case "he":
                    CurrentVisualSet.Helm = item;
                    break;
                case "ba":
                    CurrentVisualSet.Cape = item;
                    break;
                case "weapon":
                    CurrentVisualSet.Weapon = item;
                    break;
                case "pe":
                    CurrentVisualSet.Pet = item;
                    break;
                case "mi":
                    CurrentVisualSet.Misc = item;
                    break;
            }

            VisualsUpdated?.Invoke();
            _bot.Log($"[Cosmetics] Equipping {item.DisplayCategory}: {item.Name}");
        }
        catch (Exception ex)
        {
            _bot.Log($"[Cosmetics] Equip error: {ex.Message}");
        }
    }

    public async Task EquipSetAsync(CosmeticSet set, CancellationToken cancellationToken = default)
    {
        if (!_bot.Player.LoggedIn) return;

        CaptureOriginalColorsIfEmpty();

        if (set.Gender.HasValue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SwapGender(set.Gender.Value);
            await Task.Delay(100, cancellationToken);
        }

        if (set.Armor != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Equip(set.Armor);
            await Task.Delay(140, cancellationToken);
        }
        if (set.Helm != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Equip(set.Helm);
            await Task.Delay(140, cancellationToken);
        }
        if (set.Cape != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Equip(set.Cape);
            await Task.Delay(140, cancellationToken);
        }
        if (set.Weapon != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Equip(set.Weapon);
            await Task.Delay(140, cancellationToken);
        }
        if (set.Pet != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Equip(set.Pet);
            await Task.Delay(140, cancellationToken);
        }
        if (set.Misc != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Equip(set.Misc);
            await Task.Delay(140, cancellationToken);
        }

        VisualsUpdated?.Invoke();
    }

    public async Task ReapplyCurrentVisualSetAsync(CancellationToken cancellationToken = default)
    {
        if (!_bot.Player.LoggedIn || !CurrentVisualSet.HasAnyItem) return;
        _bot.Log("[Cosmetics] Auto-persisting cosmetic look across map/respawn...");
        await EquipSetAsync(CurrentVisualSet, cancellationToken);
    }

    /// <summary>
    /// Unequips a specific slot. For pets, thoroughly removes and destroys the pet actor from the map.
    /// </summary>
    public void UnequipSlot(string slot)
    {
        if (!_bot.Player.LoggedIn) return;

        try
        {
            string itemGroup = slot == "ar" ? "co" : slot;

            if (itemGroup.Equals("pe", StringComparison.OrdinalIgnoreCase))
            {
                // Comprehensive Pet removal from world
                try
                {
                    _bot.Flash.SetGameObject("world.myAvatar.petMC.visible", false);
                    _bot.Flash.SetGameObject("world.myAvatar.petMC.x", -10000);
                    _bot.Flash.SetGameObject("world.myAvatar.petMC.y", -10000);
                }
                catch { }

                try { _bot.Flash.CallGameFunction("world.myAvatar.unloadMovieAtES", "pe"); } catch { }
                try { _bot.Flash.CallGameFunction("world.destroyPet", "world.myAvatar"); } catch { }
                try { _bot.Flash.CallGameFunction("world.destroyPetMC", "world.myAvatar"); } catch { }
                try { _bot.Flash.CallGameFunction("world.myAvatar.unloadPet"); } catch { }

                try
                {
                    _bot.Flash.SetGameObject("world.myAvatar.objData.eqp.pe", null);
                    _bot.Flash.SetGameObject("world.myAvatar.petMC", null);
                }
                catch { }

                CurrentVisualSet.Pet = null;
            }
            else
            {
                _bot.Flash.CallGameFunction("world.myAvatar.unloadMovieAtES", itemGroup);
                try
                {
                    _bot.Flash.SetGameObject($"world.myAvatar.objData.eqp[{itemGroup}]", null);
                }
                catch { }

                switch (itemGroup.ToLowerInvariant())
                {
                    case "co": CurrentVisualSet.Armor = null; break;
                    case "he": CurrentVisualSet.Helm = null; break;
                    case "ba": CurrentVisualSet.Cape = null; break;
                    case "weapon": CurrentVisualSet.Weapon = null; break;
                    case "mi": CurrentVisualSet.Misc = null; break;
                }
            }

            VisualsUpdated?.Invoke();
            _bot.Log($"[Cosmetics] Unequipped slot: {itemGroup}");
        }
        catch (Exception ex)
        {
            _bot.Log($"[Cosmetics] Unequip slot error: {ex.Message}");
        }
    }

    /// <summary>
    /// Restores the player's true equipped inventory gear, gender, and character colors.
    /// </summary>
    public async Task RevertToRealGearAsync()
    {
        if (!_bot.Player.LoggedIn) return;

        try
        {
            var equippedItems = _bot.Inventory.Items?.Where(x => x.Equipped).ToList() ?? new List<InventoryItem>();
            var activeSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "co", "he", "ba", "Weapon", "pe", "mi" };

            CurrentVisualSet.Clear();

            // 1. Revert gender back to original player gender
            char realGender = OriginalGender ?? GetRealServerGender();
            char curGender = GetCurrentGender();
            if (curGender != realGender)
            {
                SwapGender(realGender, force: true);
                await Task.Delay(120);
            }

            // 2. Revert colors back to original player colors (Base, Trim, Acc, Eye, Skin, Hair)
            if (OriginalColors.HasValue)
            {
                var orig = OriginalColors.Value;
                SetColors(orig.baseColor, orig.trimColor, orig.accColor, orig.eyeColor, orig.skinColor, orig.hairColor, reloadGear: false);
            }
            else
            {
                var serverColors = GetRealServerOrAvatarColors();
                if (serverColors.baseColor != 0)
                {
                    SetColors(serverColors.baseColor, serverColors.trimColor, serverColors.accColor, serverColors.eyeColor, serverColors.skinColor, serverColors.hairColor, reloadGear: false);
                }
            }

            // 3. Equip real inventory items
            foreach (var item in equippedItems)
            {
                string group = item.ItemGroup == "ar" ? "co" : item.ItemGroup;
                activeSlots.Remove(group);

                string file = item.FileLink;
                string link = item.FileName;

                // Ensure armor SWF path matches the player's real gender
                if (group == "co" && !string.IsNullOrWhiteSpace(file))
                {
                    if (realGender == 'M')
                    {
                        file = file.Replace("/F/", "/M/").Replace("/f/", "/m/");
                        if (link.StartsWith("f", StringComparison.OrdinalIgnoreCase))
                            link = "m" + link.Substring(1);
                    }
                    else
                    {
                        file = file.Replace("/M/", "/F/").Replace("/m/", "/f/");
                        if (link.StartsWith("m", StringComparison.OrdinalIgnoreCase) && !link.StartsWith("mc", StringComparison.OrdinalIgnoreCase))
                            link = "f" + link.Substring(1);
                    }
                }

                dynamic val = new ExpandoObject();
                val.sFile = file;
                val.sLink = link;
                val.sType = item.CategoryString;
                val.sMeta = item.Meta ?? string.Empty;

                _bot.Flash.SetGameObject($"world.myAvatar.objData.eqp[{group}]", val);
                _bot.Flash.CallGameFunction("world.myAvatar.loadMovieAtES", group, val.sFile, val.sLink);
                await Task.Delay(100);
            }

            // 4. For any slots where no real item is equipped (including pet!), unequip & destroy
            foreach (var emptySlot in activeSlots)
            {
                UnequipSlot(emptySlot);
            }

            VisualsUpdated?.Invoke();
            _bot.Log("[Cosmetics] Reset avatar to actual equipped gear, restored original colors and gender.");
        }
        catch (Exception ex)
        {
            _bot.Log($"[Cosmetics] Reset to real gear error: {ex.Message}");
        }
    }

    public async Task<(bool success, string message)> CopyTargetLookAsync()
    {
        if (!_bot.Player.LoggedIn)
            return (false, "You are not logged in.");

        int entId = _bot.Flash.GetGameObject<int>("world.myAvatar.target.dataLeaf.entID");
        string targetName = _bot.Flash.GetGameObject<string>("world.myAvatar.target.dataLeaf.uoName") ?? "";

        if (entId <= 0)
        {
            return (false, "No player targeted. Click a player in-game first.");
        }

        return await CopyPlayerLookByEntIdAsync(entId, targetName);
    }

    public async Task<(bool success, string message)> CopyPlayerLookByNameAsync(string username)
    {
        if (!_bot.Player.LoggedIn)
            return (false, "You are not logged in.");

        if (string.IsNullOrWhiteSpace(username))
            return (false, "Please select or enter a player name.");

        if (_bot.Map.TryGetPlayer(username, out var player) && player != null)
        {
            return await CopyPlayerLookByEntIdAsync(player.EntID, player.Name);
        }

        return (false, $"Player '{username}' was not found in the current map.");
    }

    private async Task<(bool success, string message)> CopyPlayerLookByEntIdAsync(int entId, string playerName)
    {
        try
        {
            CaptureOriginalColorsIfEmpty();

            string rawAvatar = _bot.Flash.Call<string>("getAvatar", entId);
            if (string.IsNullOrWhiteSpace(rawAvatar))
                return (false, $"Could not retrieve avatar data for {playerName}.");

            var avatarObj = JObject.Parse(rawAvatar);
            var eqp = avatarObj["eqp"] as JObject;
            if (eqp == null)
                return (false, $"{playerName} has no equipment data.");

            var newSet = new CosmeticSet
            {
                Name = $"{playerName}'s Look",
                CreatedAt = DateTime.Now
            };

            // 1. ARMOR / CLASS: Prioritize "co" (Costume/Armor). If "co" does not exist or has empty file, fallback to "ar" (Class).
            // This prevents the underlying base class from overwriting the visual armor!
            JObject? armorObj = (eqp["co"] as JObject);
            if (armorObj != null && !HasValidArt(armorObj))
                armorObj = null;

            if (armorObj == null && eqp["ar"] is JObject arObj && HasValidArt(arObj))
                armorObj = arObj;

            if (armorObj != null)
                newSet.Armor = ParseCosmeticItemFromJObject("co", armorObj);

            // 2. HELM: "he"
            if (eqp["he"] is JObject heObj && HasValidArt(heObj))
                newSet.Helm = ParseCosmeticItemFromJObject("he", heObj);

            // 3. CAPE: "ba"
            if (eqp["ba"] is JObject baObj && HasValidArt(baObj))
                newSet.Cape = ParseCosmeticItemFromJObject("ba", baObj);

            // 4. WEAPON: "Weapon" or "weapon"
            var wepObj = (eqp["Weapon"] ?? eqp["weapon"]) as JObject;
            if (wepObj != null && HasValidArt(wepObj))
                newSet.Weapon = ParseCosmeticItemFromJObject("Weapon", wepObj);

            // 5. PET: "pe"
            if (eqp["pe"] is JObject peObj && HasValidArt(peObj))
                newSet.Pet = ParseCosmeticItemFromJObject("pe", peObj);

            // 6. RUNE: "mi"
            if (eqp["mi"] is JObject miObj && HasValidArt(miObj))
                newSet.Misc = ParseCosmeticItemFromJObject("mi", miObj);

            // 7. GENDER: Extract target's gender from getAvatar JSON, Flash world.CHARS, target dataLeaf, or armor SWF
            char? targetGender = null;
            string gStr = avatarObj["strGender"]?.ToString()
                          ?? avatarObj["dataLeaf"]?["strGender"]?.ToString()
                          ?? avatarObj["objData"]?["strGender"]?.ToString();

            if (!string.IsNullOrWhiteSpace(gStr) && (gStr[0] == 'M' || gStr[0] == 'm' || gStr[0] == 'F' || gStr[0] == 'f'))
            {
                targetGender = char.ToUpperInvariant(gStr[0]);
            }

            if (!targetGender.HasValue && entId > 0)
            {
                try
                {
                    string flashG = _bot.Flash.GetGameObject<string>($"world.CHARS[{entId}].dataLeaf.strGender")
                                    ?? _bot.Flash.GetGameObject<string>($"world.CHARS[{entId}].objData.strGender");
                    if (!string.IsNullOrWhiteSpace(flashG) && (flashG[0] == 'M' || flashG[0] == 'm' || flashG[0] == 'F' || flashG[0] == 'f'))
                    {
                        targetGender = char.ToUpperInvariant(flashG[0]);
                    }
                }
                catch { }
            }

            if (!targetGender.HasValue)
            {
                try
                {
                    int curTargetEntId = _bot.Flash.GetGameObject<int>("world.myAvatar.target.dataLeaf.entID");
                    if (curTargetEntId == entId)
                    {
                        string tG = _bot.Flash.GetGameObject<string>("world.myAvatar.target.dataLeaf.strGender")
                                    ?? _bot.Flash.GetGameObject<string>("world.myAvatar.target.objData.strGender");
                        if (!string.IsNullOrWhiteSpace(tG) && (tG[0] == 'M' || tG[0] == 'm' || tG[0] == 'F' || tG[0] == 'f'))
                        {
                            targetGender = char.ToUpperInvariant(tG[0]);
                        }
                    }
                }
                catch { }
            }

            if (!targetGender.HasValue && newSet.Armor != null)
            {
                string path = newSet.Armor.Path ?? "";
                string link = newSet.Armor.Link ?? "";
                if (path.Contains("/F/", StringComparison.OrdinalIgnoreCase) || path.Contains("/f/", StringComparison.Ordinal) || (link.StartsWith("f", StringComparison.OrdinalIgnoreCase) && !link.StartsWith("fl", StringComparison.OrdinalIgnoreCase)))
                {
                    targetGender = 'F';
                }
                else if (path.Contains("/M/", StringComparison.OrdinalIgnoreCase) || path.Contains("/m/", StringComparison.Ordinal) || link.StartsWith("m", StringComparison.OrdinalIgnoreCase))
                {
                    targetGender = 'M';
                }
            }

            if (targetGender.HasValue)
            {
                newSet.Gender = targetGender.Value;
                SwapGender(targetGender.Value);
                await Task.Delay(100);
            }

            // Copy CC colors, skin, and hair if present on target
            int? cBase = avatarObj["intColorBase"] != null && int.TryParse(avatarObj["intColorBase"]!.ToString(), out int b) ? b : null;
            int? cTrim = avatarObj["intColorTrim"] != null && int.TryParse(avatarObj["intColorTrim"]!.ToString(), out int t) ? t : null;
            int? cAcc = avatarObj["intColorAccessory"] != null && int.TryParse(avatarObj["intColorAccessory"]!.ToString(), out int a) ? a : null;
            int? cEye = avatarObj["intColorEye"] != null && int.TryParse(avatarObj["intColorEye"]!.ToString(), out int e) ? e : null;
            int? cSkin = avatarObj["intColorSkin"] != null && int.TryParse(avatarObj["intColorSkin"]!.ToString(), out int s) ? s : null;
            int? cHair = avatarObj["intColorHair"] != null && int.TryParse(avatarObj["intColorHair"]!.ToString(), out int h) ? h : null;

            // Fallback: check Flash world.CHARS or target dataLeaf if getAvatar missed skin/hair
            if (!cSkin.HasValue && entId > 0)
            {
                try
                {
                    int fSkin = _bot.Flash.GetGameObject<int>($"world.CHARS[{entId}].dataLeaf.intColorSkin");
                    if (fSkin == 0) fSkin = _bot.Flash.GetGameObject<int>($"world.CHARS[{entId}].objData.intColorSkin");
                    if (fSkin != 0) cSkin = fSkin;
                }
                catch { }
            }
            if (!cHair.HasValue && entId > 0)
            {
                try
                {
                    int fHair = _bot.Flash.GetGameObject<int>($"world.CHARS[{entId}].dataLeaf.intColorHair");
                    if (fHair == 0) fHair = _bot.Flash.GetGameObject<int>($"world.CHARS[{entId}].objData.intColorHair");
                    if (fHair != 0) cHair = fHair;
                }
                catch { }
            }

            if (cBase.HasValue || cTrim.HasValue || cAcc.HasValue || cEye.HasValue || cSkin.HasValue || cHair.HasValue)
            {
                SetColors(cBase, cTrim, cAcc, cEye, cSkin, cHair, reloadGear: false);
            }

            // Equip items target has; explicitly UNEQUIP any slot the target does NOT have (e.g. no pet, no cape)
            if (newSet.Armor != null) { Equip(newSet.Armor); await Task.Delay(120); }
            else { UnequipSlot("co"); }

            if (newSet.Helm != null) { Equip(newSet.Helm); await Task.Delay(120); }
            else { UnequipSlot("he"); }

            if (newSet.Cape != null) { Equip(newSet.Cape); await Task.Delay(120); }
            else { UnequipSlot("ba"); }

            if (newSet.Weapon != null) { Equip(newSet.Weapon); await Task.Delay(120); }
            else { UnequipSlot("Weapon"); }

            if (newSet.Pet != null) { Equip(newSet.Pet); await Task.Delay(120); }
            else { UnequipSlot("pe"); }

            if (newSet.Misc != null) { Equip(newSet.Misc); await Task.Delay(120); }
            else { UnequipSlot("mi"); }

            return (true, $"Copied look from {playerName}!");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to copy look: {ex.Message}");
        }
    }

    private static bool HasValidArt(JObject obj)
    {
        string sFile = obj["sFile"]?.ToString() ?? "";
        string sLink = obj["sLink"]?.ToString() ?? "";
        return !string.IsNullOrWhiteSpace(sFile) || !string.IsNullOrWhiteSpace(sLink);
    }

    private CosmeticItem ParseCosmeticItemFromJObject(string slotKey, JObject obj)
    {
        string sFile = obj["sFile"]?.ToString() ?? "";
        string sLink = obj["sLink"]?.ToString() ?? "";
        string sType = obj["sType"]?.ToString() ?? "";
        string sMeta = obj["sMeta"]?.ToString() ?? "";
        int itemID = obj["ItemID"]?.Value<int>() ?? 0;

        string name = AllItems.FirstOrDefault(x => x.Path.Equals(sFile, StringComparison.OrdinalIgnoreCase))?.Name
                      ?? Path.GetFileNameWithoutExtension(sFile);

        if (string.IsNullOrWhiteSpace(name))
            name = sLink;

        return new CosmeticItem(name, itemID, sFile, sType, slotKey, sLink, sMeta);
    }

    public List<string> GetRoomPlayerNames()
    {
        if (!_bot.Player.LoggedIn || _bot.Map.PlayerNames == null) return new();
        string myName = _bot.Player.Username ?? "";
        return _bot.Map.PlayerNames
            .Where(name => !name.Equals(myName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public void SetColors(int? baseColor, int? trimColor, int? accColor, int? eyeColor, int? skinColor = null, int? hairColor = null, bool reloadGear = true)
    {
        if (!_bot.Player.LoggedIn) return;

        CaptureOriginalColorsIfEmpty();

        try
        {
            if (baseColor.HasValue)
                _bot.Flash.SetGameObject("world.myAvatar.objData.intColorBase", baseColor.Value);
            if (trimColor.HasValue)
                _bot.Flash.SetGameObject("world.myAvatar.objData.intColorTrim", trimColor.Value);
            if (accColor.HasValue)
                _bot.Flash.SetGameObject("world.myAvatar.objData.intColorAccessory", accColor.Value);
            if (eyeColor.HasValue)
                _bot.Flash.SetGameObject("world.myAvatar.objData.intColorEye", eyeColor.Value);
            if (skinColor.HasValue)
                _bot.Flash.SetGameObject("world.myAvatar.objData.intColorSkin", skinColor.Value);
            if (hairColor.HasValue)
                _bot.Flash.SetGameObject("world.myAvatar.objData.intColorHair", hairColor.Value);

            try
            {
                _bot.Flash.CallGameFunction("world.myAvatar.pMC.updateColor");
            }
            catch { }

            if (reloadGear)
            {
                if (CurrentVisualSet.Armor != null) Equip(CurrentVisualSet.Armor);
                if (CurrentVisualSet.Helm != null) Equip(CurrentVisualSet.Helm);
                if (CurrentVisualSet.Cape != null) Equip(CurrentVisualSet.Cape);
            }

            _bot.Log("[Cosmetics] Palette colors updated.");
        }
        catch (Exception ex)
        {
            _bot.Log($"[Cosmetics] SetColors error: {ex.Message}");
        }
    }

    public (int baseColor, int trimColor, int accColor, int eyeColor, int skinColor, int hairColor) GetCurrentColors()
    {
        if (!_bot.Player.LoggedIn) return (0, 0, 0, 0, 0, 0);

        try
        {
            int b = _bot.Flash.GetGameObject<int>("world.myAvatar.objData.intColorBase");
            int t = _bot.Flash.GetGameObject<int>("world.myAvatar.objData.intColorTrim");
            int a = _bot.Flash.GetGameObject<int>("world.myAvatar.objData.intColorAccessory");
            int e = _bot.Flash.GetGameObject<int>("world.myAvatar.objData.intColorEye");
            int s = _bot.Flash.GetGameObject<int>("world.myAvatar.objData.intColorSkin");
            int h = _bot.Flash.GetGameObject<int>("world.myAvatar.objData.intColorHair");
            return (b, t, a, e, s, h);
        }
        catch
        {
            return (0, 0, 0, 0, 0, 0);
        }
    }

    public void SwapGender(char? targetGender = null, bool force = false)
    {
        if (!_bot.Player.LoggedIn) return;

        CaptureOriginalStateIfEmpty();

        try
        {
            int uid = _bot.Player.ID;
            if (uid <= 0)
                uid = _bot.Flash.GetGameObject<int>("world.myAvatar.dataLeaf.entID");

            char currentGender = GetCurrentGender();

            char nextGender = targetGender.HasValue
                ? targetGender.Value
                : (currentGender == 'M' ? 'F' : 'M');

            if (!force && targetGender.HasValue && currentGender == nextGender)
                return;

            // 1. Update Flash state (leave dataLeaf untouched as server ground truth)
            _bot.Flash.SetGameObject("world.myAvatar.objData.strGender", nextGender.ToString());

            // 2. Send AQW genderSwap packet to client
            string packet = $"{{\"t\":\"xt\",\"b\":{{\"r\":-1,\"o\":{{\"cmd\":\"genderSwap\",\"bitSuccess\":1,\"gender\":\"{nextGender}\",\"intCoins\":0,\"uid\":{uid},\"strHairFileName\":\"\",\"HairID\":\"\",\"strHairName\":\"\"}}}}}}";
            _bot.Send.ClientPacket(packet, "json");

            // 3. Swap current armor M/ <-> F/ file and redraw avatar
            try
            {
                string coFile = _bot.Flash.GetGameObject<string>("world.myAvatar.objData.eqp.co.sFile") ?? "";
                string coLink = _bot.Flash.GetGameObject<string>("world.myAvatar.objData.eqp.co.sLink") ?? "";

                if (string.IsNullOrWhiteSpace(coFile))
                {
                    coFile = _bot.Flash.GetGameObject<string>("world.myAvatar.objData.eqp.ar.sFile") ?? "";
                    coLink = _bot.Flash.GetGameObject<string>("world.myAvatar.objData.eqp.ar.sLink") ?? "";
                }

                if (!string.IsNullOrWhiteSpace(coFile) && !string.IsNullOrWhiteSpace(coLink))
                {
                    string swappedFile = coFile;
                    string swappedLink = coLink;

                    if (nextGender == 'F')
                    {
                        swappedFile = swappedFile.Replace("/M/", "/F/").Replace("/m/", "/f/");
                        if (swappedLink.StartsWith("m", StringComparison.OrdinalIgnoreCase) && !swappedLink.StartsWith("mc", StringComparison.OrdinalIgnoreCase))
                            swappedLink = "f" + swappedLink.Substring(1);
                    }
                    else
                    {
                        swappedFile = swappedFile.Replace("/F/", "/M/").Replace("/f/", "/m/");
                        if (swappedLink.StartsWith("f", StringComparison.OrdinalIgnoreCase))
                            swappedLink = "m" + swappedLink.Substring(1);
                    }

                    _bot.Flash.SetGameObject("world.myAvatar.objData.eqp.co.sFile", swappedFile);
                    _bot.Flash.SetGameObject("world.myAvatar.objData.eqp.co.sLink", swappedLink);
                    _bot.Flash.CallGameFunction("world.myAvatar.loadMovieAtES", "co", swappedFile, swappedLink);
                }
                else
                {
                    _bot.Flash.CallGameFunction("world.myAvatar.pMC.draw");
                }
            }
            catch { }

            _bot.Log($"[Cosmetics] Gender set to {(nextGender == 'M' ? "Male" : "Female")}");
        }
        catch (Exception ex)
        {
            _bot.Log($"[Cosmetics] Gender swap error: {ex.Message}");
        }
    }

    /// <summary>
    /// Captures the current look using cosmetic overrides first, falling back to Flash and real equipped inventory gear.
    /// </summary>
    public CosmeticSet CaptureCurrentLook(string setName = "Captured Outfit")
    {
        var set = new CosmeticSet { Name = setName, CreatedAt = DateTime.Now, Gender = GetCurrentGender() };

        try
        {
            // 1. Populate from active cosmetic overrides if present
            if (CurrentVisualSet.Armor != null) set.Armor = CurrentVisualSet.Armor;
            if (CurrentVisualSet.Helm != null) set.Helm = CurrentVisualSet.Helm;
            if (CurrentVisualSet.Cape != null) set.Cape = CurrentVisualSet.Cape;
            if (CurrentVisualSet.Weapon != null) set.Weapon = CurrentVisualSet.Weapon;
            if (CurrentVisualSet.Pet != null) set.Pet = CurrentVisualSet.Pet;
            if (CurrentVisualSet.Misc != null) set.Misc = CurrentVisualSet.Misc;

            // 2. Parse Flash objData.eqp
            string rawEqp = _bot.Flash.GetGameObject("world.myAvatar.objData.eqp") ?? "";
            if (!string.IsNullOrWhiteSpace(rawEqp))
            {
                var eqpDict = JsonConvert.DeserializeObject<Dictionary<string, JObject>>(rawEqp);
                if (eqpDict != null)
                {
                    // Armor: prioritize "co" then "ar"
                    if (set.Armor == null)
                    {
                        if (eqpDict.TryGetValue("co", out var coObj) && HasValidArt(coObj))
                            set.Armor = ParseCosmeticItemFromJObject("co", coObj);
                        else if (eqpDict.TryGetValue("ar", out var arObj) && HasValidArt(arObj))
                            set.Armor = ParseCosmeticItemFromJObject("ar", arObj);
                    }

                    // Helm
                    if (set.Helm == null && eqpDict.TryGetValue("he", out var heObj) && HasValidArt(heObj))
                        set.Helm = ParseCosmeticItemFromJObject("he", heObj);

                    // Cape
                    if (set.Cape == null && eqpDict.TryGetValue("ba", out var baObj) && HasValidArt(baObj))
                        set.Cape = ParseCosmeticItemFromJObject("ba", baObj);

                    // Weapon
                    if (set.Weapon == null)
                    {
                        if (eqpDict.TryGetValue("Weapon", out var wepObj) && HasValidArt(wepObj))
                            set.Weapon = ParseCosmeticItemFromJObject("Weapon", wepObj);
                        else if (eqpDict.TryGetValue("weapon", out var wepObj2) && HasValidArt(wepObj2))
                            set.Weapon = ParseCosmeticItemFromJObject("Weapon", wepObj2);
                    }

                    // Pet
                    if (set.Pet == null && eqpDict.TryGetValue("pe", out var peObj) && HasValidArt(peObj))
                        set.Pet = ParseCosmeticItemFromJObject("pe", peObj);

                    // Rune
                    if (set.Misc == null && eqpDict.TryGetValue("mi", out var miObj) && HasValidArt(miObj))
                        set.Misc = ParseCosmeticItemFromJObject("mi", miObj);
                }
            }

            // 3. Fallback to real inventory items equipped for any remaining empty slots
            var realEquipped = _bot.Inventory.Items?.Where(x => x.Equipped).ToList();
            if (realEquipped != null && realEquipped.Count > 0)
            {
                if (set.Armor == null)
                {
                    var item = realEquipped.FirstOrDefault(x => x.ItemGroup.Equals("co", StringComparison.OrdinalIgnoreCase) || x.ItemGroup.Equals("ar", StringComparison.OrdinalIgnoreCase));
                    if (item != null) set.Armor = new CosmeticItem(item);
                }
                if (set.Helm == null)
                {
                    var item = realEquipped.FirstOrDefault(x => x.ItemGroup.Equals("he", StringComparison.OrdinalIgnoreCase));
                    if (item != null) set.Helm = new CosmeticItem(item);
                }
                if (set.Cape == null)
                {
                    var item = realEquipped.FirstOrDefault(x => x.ItemGroup.Equals("ba", StringComparison.OrdinalIgnoreCase));
                    if (item != null) set.Cape = new CosmeticItem(item);
                }
                if (set.Weapon == null)
                {
                    var item = realEquipped.FirstOrDefault(x => x.ItemGroup.Equals("Weapon", StringComparison.OrdinalIgnoreCase));
                    if (item != null) set.Weapon = new CosmeticItem(item);
                }
                if (set.Pet == null)
                {
                    var item = realEquipped.FirstOrDefault(x => x.ItemGroup.Equals("pe", StringComparison.OrdinalIgnoreCase));
                    if (item != null) set.Pet = new CosmeticItem(item);
                }
                if (set.Misc == null)
                {
                    var item = realEquipped.FirstOrDefault(x => x.ItemGroup.Equals("mi", StringComparison.OrdinalIgnoreCase));
                    if (item != null) set.Misc = new CosmeticItem(item);
                }
            }
        }
        catch (Exception ex)
        {
            _bot.Log($"[Cosmetics] Error capturing look: {ex.Message}");
        }

        return set;
    }

    public void Dispose()
    {
        _catalogLoadLock.Dispose();
    }
}
