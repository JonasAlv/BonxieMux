using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Interactivity;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Items;
using Skua.Core.Models.Quests;
using Skua.Core.Models.Shops;

namespace Skua.Plugin.Omniscope;

public partial class OmniscopeWindow
{
    private async void AnalyzeMap_Click(object? sender, RoutedEventArgs e)
    {
        if (_mapAnalysisRunning)
            return;

        string cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VibeSkua",
            "cache",
            "0SavedMaps.json");

        _mapAnalysisRunning = true;
        AnalyzeMapButtonState(true);
        DataStatusText.Text = "Loading Saved Map SWF References";
        try
        {
            await LoadMapShopIndexCacheAsync();
            MapShopRecordCountText.Text = _mapShopRecords.Count.ToString("N0");
            if (!File.Exists(cachePath))
            {
                _mapSnapshot = _mapShopRecords.ToList();
                _mapRecords.Clear();
                MapSwfMapCountText.Text = "0";
                MapItemRecordCountText.Text = "0";
                ApplyMapFilter();
                DataStatusText.Text = _mapShopRecords.Count > 0
                    ? $"No Saved Map Item Index  /  {_mapShopRecords.Count:N0} Shop IDs Restored From Previous Map Scans"
                    : "No Saved Map Index Found  /  Load Maps In-Game First";
                return;
            }

            string json = await File.ReadAllTextAsync(cachePath);
            Dictionary<string, List<MapItem>>? savedMaps = JsonSerializer.Deserialize<Dictionary<string, List<MapItem>>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            KeyValuePair<string, List<MapItem>>[] mapEntries = savedMaps?
                .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];

            List<GameDataRecord> mapRecords = new();
            foreach (KeyValuePair<string, List<MapItem>> mapEntry in mapEntries)
            {
                foreach (MapItem mapItem in mapEntry.Value)
                {
                    string questName = _bot.Quests.CachedDictionary.TryGetValue(mapItem.QuestID, out QuestData? questData) && questData is not null
                        ? questData.Name
                        : $"Quest ID {mapItem.QuestID}";
                    mapRecords.Add(CreateMapItemRecord(mapItem, questName, mapEntry.Key));
                }
            }

            _mapItemSnapshot = mapEntries.SelectMany(static pair => pair.Value).ToArray();
            _mapSnapshot = mapRecords
                .GroupBy(static record => $"{record.Metadata}|{GetRecordId(record)}")
                .Select(static group => group.First())
                .Concat(_mapShopRecords)
                .ToList();

            MapSwfMapCountText.Text = mapEntries.Length.ToString("N0");
            MapItemRecordCountText.Text = _mapSnapshot.Count(static record => record.Category == "MAP ITEM").ToString("N0");
            MapShopRecordCountText.Text = _mapShopRecords.Count.ToString("N0");
            ApplyMapFilter();
            DataStatusText.Text = $"Loaded {_mapSnapshot.Count:N0} Map References From {mapEntries.Length:N0} Saved Maps";
        }
        catch (Exception ex)
        {
            DataStatusText.Text = $"Saved Map Index Read Failed  /  {ex.Message}";
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE saved map index read failed: {ex}");
        }
        finally
        {
            _mapAnalysisRunning = false;
            AnalyzeMapButtonState(false);
        }
    }

    private static string MapShopIndexCachePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VibeSkua",
            "cache",
            "0SavedMapShops.json");

    private async Task LoadMapShopIndexCacheAsync()
    {
        _mapShopRecords.Clear();
        string cachePath = MapShopIndexCachePath;
        if (!File.Exists(cachePath))
            return;

        try
        {
            string json = await File.ReadAllTextAsync(cachePath);
            List<SavedMapShopIndexEntry>? entries = JsonSerializer.Deserialize<List<SavedMapShopIndexEntry>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            foreach (SavedMapShopIndexEntry entry in entries ?? [])
            {
                string mapName = GetMapScriptName(entry.MapName);
                if (string.IsNullOrWhiteSpace(mapName) || entry.ShopIds is null)
                    continue;

                foreach (int shopId in entry.ShopIds.Where(static id => id > 1).Distinct())
                    _mapShopRecords.Add(CreateMapShopRecord(shopId, mapName, entry.MapFilePath));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE saved map shop index could not be read: {ex.Message}");
        }
    }

    private async Task SaveMapShopIndexCacheAsync()
    {
        string cachePath = MapShopIndexCachePath;
        try
        {
            List<SavedMapShopIndexEntry> entries = _mapShopRecords
                .Where(static record => !string.IsNullOrWhiteSpace(record.SourceMapName) && record.SourceShopId is > 1)
                .GroupBy(static record => record.SourceMapName!, StringComparer.OrdinalIgnoreCase)
                .Select(static group => new SavedMapShopIndexEntry
                {
                    MapName = group.Key,
                    MapFilePath = group.Select(static record => record.Metadata).FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path)) ?? string.Empty,
                    ShopIds = group.Select(static record => record.SourceShopId!.Value).Distinct().Order().ToArray()
                })
                .OrderBy(static entry => entry.MapName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            string json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(cachePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE saved map shop index could not be written: {ex.Message}");
            DataStatusText.Text = $"Map Shops Scanned, But The Local Index Could Not Be Saved  /  {ex.Message}";
        }
    }

    private async void ScanCurrentMapShops_Click(object? sender, RoutedEventArgs e)
    {
        if (_mapShopIndexRunning)
            return;

        if (!_bot.Map.Loaded)
        {
            DataStatusText.Text = "Map Shop Scan Unavailable  /  Join The Map You Want To Index First";
            return;
        }

        string mapFilePath = _bot.Map.FilePath?.Split('?')[0] ?? string.Empty;
        string mapName = GetMapScriptName(_bot.Map.Name);
        if (string.IsNullOrWhiteSpace(mapFilePath) || string.IsNullOrWhiteSpace(mapName))
        {
            DataStatusText.Text = "Map Shop Scan Failed  /  The Current Map Did Not Expose A Valid SWF Path";
            return;
        }

        _mapShopIndexRunning = true;
        ScanMapShopsButton.IsEnabled = false;
        ScanMapShopsButton.Content = "Fetching + Scanning...";
        DataStatusText.Text = $"Fetching The Map SWF For /{mapName}";
        try
        {
            string localMapSwfPath = await AqwMapSwfDownloader.DownloadAsync(mapFilePath);
            DataStatusText.Text = $"Map SWF Ready  /  Decompiling {Path.GetFileName(localMapSwfPath)}";
            string ffdecDirectory = SwfSourceIndexer.ResolveFfdecDirectory();
            SwfSourceIndexResult result = await SwfSourceIndexer.DecompileAsync(localMapSwfPath, ffdecDirectory);
            int[] shopIds = LocationSwfShopIndexer.ReadShopIds(result.ExportDirectory);
            List<GameDataRecord> mapShopRecords = shopIds
                .Select(shopId => CreateMapShopRecord(shopId, mapName, mapFilePath))
                .ToList();

            _mapShopRecords.RemoveAll(record => string.Equals(record.SourceMapName, mapName, StringComparison.OrdinalIgnoreCase));
            _mapShopRecords.AddRange(mapShopRecords);
            _mapSnapshot = _mapSnapshot
                .Where(record => record.Category != "MAP SHOP" ||
                    !string.Equals(record.SourceMapName, mapName, StringComparison.OrdinalIgnoreCase))
                .Concat(mapShopRecords)
                .ToList();

            MapShopRecordCountText.Text = _mapShopRecords.Count.ToString("N0");
            await SaveMapShopIndexCacheAsync();
            MapCategoryBox.SelectedIndex = 2;
            ApplyMapFilter();
            DataStatusText.Text = shopIds.Length > 0
                ? $"Found {shopIds.Length:N0} Shop ID(S) In /{mapName}  /  Select A Shop To Load Or Generate Its Merge Script"
                : $"No Shop IDs Found In /{mapName}  /  Map SWF Scan Completed";
        }
        catch (Exception ex)
        {
            DataStatusText.Text = $"Map Shop Scan Failed  /  {ex.Message}";
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE map shop scan failed: {ex}");
        }
        finally
        {
            _mapShopIndexRunning = false;
            ScanMapShopsButton.IsEnabled = true;
            ScanMapShopsButton.Content = "Scan Current Map Shops";
        }
    }

    private async void LoadMapShop_Click(object? sender, RoutedEventArgs e)
    {
        if (_mapShopLoadRunning ||
            MapRecordList.SelectedItem is not GameDataRecord { Category: "MAP SHOP", SourceShopId: int shopId } record ||
            shopId <= 1)
            return;

        _mapShopLoadRunning = true;
        LoadMapShopButton.IsEnabled = false;
        LoadMapShopButton.Content = "Waiting For AQW...";
        DataStatusText.Text = $"Requesting Shop {shopId}  /  Waiting For The Game Client";
        try
        {
            // ScriptShop.Load waits synchronously for the server response (up to 20 seconds).
            // Run it off the Avalonia UI thread, then copy the results into plain strings before returning.
            ShopLoadResult result = await Task.Run(() =>
            {
                _bot.Shops.Load(shopId);
                if (!_bot.Shops.IsLoaded || _bot.Shops.ID != shopId)
                    return new ShopLoadResult(false, shopId, string.Empty, []);

                string shopName = _bot.Shops.Name;
                string[] itemLines = _bot.Shops.Items
                    .Select(static item => $"{item.Name} [{item.ID}]  /  ShopItemID {item.ShopItemID}")
                    .ToArray();

                return new ShopLoadResult(true, shopId, shopName, itemLines);
            });

            if (!result.Loaded)
            {
                DataStatusText.Text = $"Shop {shopId} Did Not Load  /  AQW Did Not Confirm The Request";
                return;
            }

            string items = result.ItemLines.Length == 0
                ? "The shop loaded, but no items were exposed by the client."
                : string.Join(Environment.NewLine, result.ItemLines);
            MapDetailText.Text = $"{record.Details}{Environment.NewLine}{Environment.NewLine}" +
                $"Loaded Shop: {result.ShopName} [{result.ShopId}]{Environment.NewLine}" +
                $"Items: {result.ItemLines.Length:N0}{Environment.NewLine}{items}{Environment.NewLine}{Environment.NewLine}" +
                "No purchase request was sent.";
            DataStatusText.Text = $"Shop Loaded  /  {result.ShopName} [{result.ShopId}]  /  No Purchase Made";
        }
        catch (Exception ex)
        {
            DataStatusText.Text = $"Shop Load Failed  /  {ex.Message}";
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE map shop load failed: {ex}");
        }
        finally
        {
            _mapShopLoadRunning = false;
            LoadMapShopButton.Content = "Load Shop";
            UpdateDeveloperHelperButtons();
        }
    }

    private async void GenerateMergeShop_Click(object? sender, RoutedEventArgs e)
    {
        if (MapRecordList.SelectedItem is not GameDataRecord { Category: "MAP SHOP", SourceShopId: int shopId, SourceMapName: string mapName } ||
            shopId <= 1 || string.IsNullOrWhiteSpace(mapName))
            return;

        await RunDeveloperHelperAsync(
            "MergeTemplateHelperV3.cs",
            ("regenerateExisting", false),
            ("location", mapName),
            ("shopID", shopId),
            ("ignoreCaseStorage", false));
    }

    private async void GenerateCurrentMapQuests_Click(object? sender, RoutedEventArgs e)
    {
        string mapName = GetMapScriptName(_bot.Map.Name);
        if (!_bot.Map.Loaded || string.IsNullOrWhiteSpace(mapName))
        {
            DataStatusText.Text = "Join The Map You Want To Generate Before Starting The Weekly Release Generator";
            return;
        }

        await RunDeveloperHelperAsync(
            "WeeklyReleaseGeneratorV3.cs",
            ("UseCurrentMap", true),
            ("MapName", mapName));
    }

    private async void GenerateMapQuests_Click(object? sender, RoutedEventArgs e)
    {
        if (MapRecordList.SelectedItem is not GameDataRecord record || string.IsNullOrWhiteSpace(record.SourceMapName))
            return;

        await RunDeveloperHelperAsync(
            "WeeklyReleaseGeneratorV3.cs",
            ("UseCurrentMap", false),
            ("MapName", record.SourceMapName));
    }

    private async Task RunDeveloperHelperAsync(string fileName, params (string Name, object Value)[] optionValues)
    {
        if (_developerHelperRunning)
            return;

        if (_bot.Manager is not IScriptManager manager)
        {
            DataStatusText.Text = "Script Generation Unavailable  /  Script Manager Interface Not Found";
            return;
        }

        if (manager.ScriptRunning)
        {
            DataStatusText.Text = "Stop The Current Script Before Starting A Developer Generator";
            return;
        }

        string scriptPath = Path.Combine(ClientFileSources.SkuaScriptsDIR, "Tools", "ForDevelopers", fileName);
        if (!File.Exists(scriptPath))
        {
            DataStatusText.Text = $"Generator Script Not Found  /  {scriptPath}  /  Update Your Scripts Repository";
            return;
        }

        _developerHelperRunning = true;
        UpdateDeveloperHelperButtons();
        DataStatusText.Text = $"Preparing {Path.GetFileNameWithoutExtension(fileName)}  /  Compiling + Applying Map Options";
        try
        {
            // Compilation, reflected option discovery, config reads, and config writes can all touch disk.
            // Keep the complete preparation phase away from the UI thread.
            manager.SetLoadedScript(scriptPath);
            await Task.Run(() =>
            {
                string source = File.ReadAllText(scriptPath);
                object? scriptInstance = manager.Compile(source);
                if (scriptInstance is null)
                    throw new InvalidOperationException($"Skua could not compile {fileName}. Check the script compiler output.");

                manager.LoadScriptConfig(scriptInstance);
                IScriptOptionContainer? config = manager.Config;
                if (config is null)
                    throw new InvalidOperationException($"{fileName} did not expose a script option container.");

                foreach ((string name, object value) in optionValues)
                    config.Set(name, value);
                config.Save();
            });

            DataStatusText.Text = $"Starting {Path.GetFileNameWithoutExtension(fileName)}  /  Handing Off To Script Manager";
            // StartScript performs synchronous setup after its awaits. Run the whole start flow off-thread too;
            // the actual script still executes on Skua's dedicated Script Thread.
            Exception? startFailure = await Task.Run(async () =>
                await manager.StartScript().ConfigureAwait(false));
            if (startFailure is not null)
                throw startFailure;

            DataStatusText.Text = $"Started {Path.GetFileNameWithoutExtension(fileName)}  /  Map Options Prefilled";
        }
        catch (Exception ex)
        {
            DataStatusText.Text = $"Script Generation Failed  /  {ex.Message}";
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE developer script launch failed: {ex}");
        }
        finally
        {
            _developerHelperRunning = false;
            UpdateDeveloperHelperButtons();
        }
    }

    private void AnalyzeMapButtonState(bool running)
    {
        if (AnalyzeMapButton is not null)
        {
            AnalyzeMapButton.IsEnabled = !running;
            AnalyzeMapButton.Content = running ? "Reading Map Index..." : "Load Saved Map Index";
        }
    }

    private async void DecompileSwf_Click(object? sender, RoutedEventArgs e)
    {
        if (_swfIndexRunning)
            return;

        _swfIndexRunning = true;
        DecompileSwfButtonState(true);
        DataStatusText.Text = "Resolving Latest AQW Game Client  /  Manifest + CDN Fallback";
        try
        {
            string ffdecDirectory = SwfSourceIndexer.ResolveFfdecDirectory();
            AqwGameClientFile clientFile = await AqwGameClientResolver.DownloadLatestAsync();
            DataStatusText.Text = $"AQW Client {clientFile.FileName} Downloaded  /  Starting FFDec";
            SwfSourceIndexResult result = await SwfSourceIndexer.DecompileAsync(clientFile.LocalPath, ffdecDirectory);
            _swfSourceRecords.Clear();
            _swfSourceRecords.Add(new GameDataRecord
            {
                Category = "CLIENT CORE",
                Name = $"AQW game client / {clientFile.FileName}",
                Summary = $"{clientFile.ResolutionMethod}  /  {result.Records.Count:N0} source files indexed",
                Metadata = clientFile.SourceUri.ToString(),
                Details = $"Resolution: {clientFile.ResolutionMethod}{Environment.NewLine}" +
                    $"Source File: {clientFile.FileName}{Environment.NewLine}" +
                    $"Local Copy: {clientFile.LocalPath}{Environment.NewLine}" +
                    $"Download Source: {clientFile.SourceUri}{Environment.NewLine}" +
                    $"Exported Source: {result.ExportDirectory}{Environment.NewLine}" +
                    $"Indexed ActionScript Files: {result.Records.Count:N0}{Environment.NewLine}{Environment.NewLine}" +
                    "The SWF is the latest AQW game client resolved from the game-version manifest or CDN version-probing fallback. Client-side logic is not proof of server-side rules.",
                SearchText = $"AQW game client {clientFile.FileName} {clientFile.SourceUri} {clientFile.LocalPath}"
            });
            _swfSourceRecords.AddRange(result.Records);
            RefreshDataCatalog();
            SwfSourceStatusText.Text = $"AQW Client Indexed  /  {clientFile.FileName}  /  {_swfSourceRecords.Count:N0} Records";
            DataStatusText.Text = $"AQW Game Client Decompiled  /  {result.Records.Count:N0} ActionScript Files Indexed";
        }
        catch (Exception ex)
        {
            DataStatusText.Text = $"AQW Client Decompilation Failed  /  {ex.Message}";
            SwfSourceStatusText.Text = "AQW Client Source Index Failed";
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE AQW client SWF indexing failed: {ex}");
        }
        finally
        {
            _swfIndexRunning = false;
            DecompileSwfButtonState(false);
        }
    }

    private void DecompileSwfButtonState(bool running)
    {
        if (DecompileSwfButton is not null)
        {
            DecompileSwfButton.IsEnabled = !running;
            DecompileSwfButton.Content = running ? "Downloading / Indexing..." : "Index Game SWF";
        }
    }

}
