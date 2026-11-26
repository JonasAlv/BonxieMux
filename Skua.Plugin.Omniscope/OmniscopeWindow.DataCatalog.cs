using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using Skua.Core.Models.Items;
using Skua.Core.Models.Quests;
using Skua.Core.Models.Shops;

namespace Skua.Plugin.Omniscope;

public partial class OmniscopeWindow
{
    private void RefreshDataCatalog_Click(object? sender, RoutedEventArgs e) => RefreshDataCatalog();

    private void DataSearchBox_TextChanged(object? sender, string text) => ApplyDataFilter();

    private void DataCategoryBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyDataFilter();

    private void DataSortBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyDataFilter();

    private void MapSearchBox_TextChanged(object? sender, string text) => ApplyMapFilter();

    private void MapCategoryBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyMapFilter();

    private void MapSortBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyMapFilter();

    private void MapRecordList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (MapRecordList.SelectedItem is not GameDataRecord record)
        {
            LoadMapShopButton.IsEnabled = false;
            GenerateMergeShopButton.IsEnabled = false;
            GenerateMapQuestsButton.IsEnabled = false;
            return;
        }

        MapDetailTitle.Text = record.Name;
        MapDetailMeta.Text = $"{record.DisplayCategory}  /  {record.Metadata}";
        MapDetailText.Text = record.Details;
        UpdateDeveloperHelperButtons();
    }

    /// <summary>
    /// Keeps map actions disabled while a shop load or developer generator is already active.
    /// </summary>
    private void UpdateDeveloperHelperButtons()
    {
        bool ready = !_developerHelperRunning;
        bool isMapShop = MapRecordList.SelectedItem is GameDataRecord
        {
            Category: "MAP SHOP",
            SourceShopId: int shopId
        } && shopId > 1;
        bool hasMap = MapRecordList.SelectedItem is GameDataRecord selectedRecord &&
            !string.IsNullOrWhiteSpace(selectedRecord.SourceMapName);

        LoadMapShopButton.IsEnabled = !_mapShopLoadRunning && isMapShop;
        GenerateMergeShopButton.IsEnabled = ready && isMapShop && hasMap;
        GenerateMapQuestsButton.IsEnabled = ready && hasMap;
        GenerateCurrentMapQuestsButton.IsEnabled = ready;
    }

    private void DataRecordList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataRecordList.SelectedItem is not GameDataRecord record)
            return;

        DataDetailTitle.Text = record.Name;
        DataDetailMeta.Text = $"{record.DisplayCategory}  /  {record.Metadata}";
        SetSyntaxHighlightedDetails(
            $"{record.Details}{Environment.NewLine}{Environment.NewLine}Source Inspection:{Environment.NewLine}This is decompiled AQW client code. It describes client-side implementation and does not establish server-side validation.");
    }

    /// <summary>
    /// Renders the selected ActionScript record with a compact syntax palette and line numbers.
    /// </summary>
    /// <param name="source">The source text and metadata to display.</param>
    private void SetSyntaxHighlightedDetails(string source)
    {
        DataDetailText.Inlines.Clear();
        if (string.IsNullOrEmpty(source))
            return;

        bool truncated = source.Length > MaximumVisibleCodeCharacters;
        string displaySource = truncated ? source[..MaximumVisibleCodeCharacters] : source;
        string[] lines = displaySource.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool insideBlockComment = false;

        for (int index = 0; index < lines.Length; index++)
        {
            DataDetailText.Inlines.Add(new Run($"{index + 1,4}  ") { Foreground = CodeLineNumberBrush });
            AppendHighlightedCodeLine(lines[index], ref insideBlockComment);
            if (index < lines.Length - 1)
                DataDetailText.Inlines.Add(new LineBreak());
        }

        if (truncated)
        {
            DataDetailText.Inlines.Add(new LineBreak());
            DataDetailText.Inlines.Add(new Run("[Display Truncated: Source Exceeds 200,000 Characters]") { Foreground = CodeCommentBrush });
        }
    }

    /// <summary>
    /// Tokenizes one source line while carrying block-comment state between lines.
    /// </summary>
    /// <param name="line">The source line to tokenize.</param>
    /// <param name="insideBlockComment">Whether a block comment started on an earlier line.</param>
    private void AppendHighlightedCodeLine(string line, ref bool insideBlockComment)
    {
        int position = 0;
        while (position < line.Length)
        {
            if (insideBlockComment)
            {
                int commentEnd = line.IndexOf("*/", position, StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    DataDetailText.Inlines.Add(new Run(line[position..]) { Foreground = CodeCommentBrush });
                    return;
                }

                int endPosition = commentEnd + 2;
                DataDetailText.Inlines.Add(new Run(line[position..endPosition]) { Foreground = CodeCommentBrush });
                position = endPosition;
                insideBlockComment = false;
                continue;
            }

            Match match = ActionScriptTokenRegex.Match(line, position);
            if (!match.Success)
            {
                DataDetailText.Inlines.Add(new Run(line[position..]) { Foreground = CodeDefaultBrush });
                return;
            }

            if (match.Index > position)
                DataDetailText.Inlines.Add(new Run(line[position..match.Index]) { Foreground = CodeDefaultBrush });

            IBrush brush = match.Groups["comment"].Success ? CodeCommentBrush
                : match.Groups["string"].Success ? CodeStringBrush
                : match.Groups["keyword"].Success ? CodeKeywordBrush
                : match.Groups["number"].Success ? CodeNumberBrush
                : match.Groups["function"].Success ? CodeFunctionBrush
                : match.Groups["type"].Success ? CodeTypeBrush
                : CodeDefaultBrush;
            DataDetailText.Inlines.Add(new Run(match.Value) { Foreground = brush });

            if (match.Groups["comment"].Success &&
                match.Value.StartsWith("/*", StringComparison.Ordinal) &&
                !match.Value.EndsWith("*/", StringComparison.Ordinal))
                insideBlockComment = true;

            position = match.Index + match.Length;
        }
    }

    private void RefreshDataCatalog()
    {
        _dataSnapshot = _swfSourceRecords
            .OrderBy(static record => record.Category)
            .ThenBy(static record => record.Name)
            .ToList();

        SourceFileCountText.Text = _swfSourceRecords.Count.ToString("N0");
        ClientSystemCountText.Text = _swfSourceRecords.Select(static record => record.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString("N0");
        ClientFunctionCountText.Text = _swfSourceRecords.Sum(static record => record.SourceFunctionCount).ToString("N0");

        SwfSourceStatusText.Text = _swfSourceRecords.Count > 0
            ? $"Client SWF Indexed  /  {_swfSourceRecords.Count:N0} Source Files"
            : "Client SWF Source Not Indexed";
        DataStatusText.Text = _swfSourceRecords.Count > 0
            ? $"Client Source Ready  /  {_swfSourceRecords.Count:N0} Files  /  {ClientFunctionCountText.Text} Functions"
            : "Client Source Not Indexed  /  Use Index Game SWF To Decompile The Current AQW Client";

        ApplyDataFilter();
    }

    private void ApplyDataFilter()
    {
        if (!_xamlInitialized || DataSearchBox is null || DataCategoryBox is null || DataSortBox is null ||
            DataRecordList is null || DataResultMetric is null || DataDetailTitle is null ||
            DataDetailMeta is null || DataDetailText is null)
            return;

        string search = DataSearchBox.SearchText?.Trim() ?? string.Empty;
        string category = (DataCategoryBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Client Code";
        string sort = (DataSortBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Name (A-Z)";
        IEnumerable<GameDataRecord> query = _dataSnapshot;

        query = category switch
        {
            "Client Core" => query.Where(static record => record.Category == "CLIENT CORE"),
            "Network / API" => query.Where(static record => record.Category == "NETWORK / API"),
            "Game UI" => query.Where(static record => record.Category == "GAME UI"),
            "World / Map" => query.Where(static record => record.Category == "WORLD / MAP"),
            "Player / Avatar" => query.Where(static record => record.Category == "PLAYER / AVATAR"),
            "Inventory / Items" => query.Where(static record => record.Category == "INVENTORY / ITEMS"),
            "Combat / Skills" => query.Where(static record => record.Category == "COMBAT / SKILLS"),
            "Game Systems" => query.Where(static record => record.Category == "GAME SYSTEMS"),
            "Other Client Code" => query.Where(static record => record.Category == "OTHER"),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(record => record.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase));

        query = sort switch
        {
            "Name (Z-A)" => query.OrderByDescending(static record => record.Name, StringComparer.OrdinalIgnoreCase),
            "Category" => query.OrderBy(static record => record.Category).ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase),
            "ID (low-high)" => query.OrderBy(GetRecordId).ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase),
            "ID (high-low)" => query.OrderByDescending(GetRecordId).ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase),
            _ => query.OrderBy(static record => record.Name, StringComparer.OrdinalIgnoreCase)
        };

        GameDataRecord[] matches = query.Take(5000).ToArray();
        _dataRecords.Clear();
        foreach (GameDataRecord record in matches)
            _dataRecords.Add(record);

        DataResultMetric.Text = $"{matches.Length:N0} Matches";
        if (matches.Length == 0)
        {
            DataDetailTitle.Text = _dataSnapshot.Count == 0 ? "Client source not indexed" : "No matching source files";
            DataDetailMeta.Text = _dataSnapshot.Count == 0
                ? "Use Index Game SWF to download and decompile the current AQW client."
                : "Try another client category or search term.";
            DataDetailText.Inlines.Clear();
        }
        else if (DataRecordList.SelectedItem is not GameDataRecord selected || !matches.Contains(selected))
        {
            DataRecordList.SelectedIndex = 0;
        }
    }

    private void ApplyMapFilter()
    {
        if (!_xamlInitialized || MapSearchBox is null || MapCategoryBox is null || MapSortBox is null ||
            MapRecordList is null || MapResultMetric is null || MapDetailTitle is null ||
            MapDetailMeta is null || MapDetailText is null)
            return;

        string search = MapSearchBox.SearchText?.Trim() ?? string.Empty;
        string category = (MapCategoryBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All maps";
        string sort = (MapSortBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Map (A-Z)";
        string currentMap = GetMapScriptName(_bot.Map.Name);
        IEnumerable<GameDataRecord> query = _mapSnapshot;

        query = category switch
        {
            "Current map" => query.Where(record => string.Equals(record.SourceMapName, currentMap, StringComparison.OrdinalIgnoreCase)),
            "Map shops" => query.Where(static record => record.Category == "MAP SHOP"),
            "Map items" => query.Where(static record => record.Category == "MAP ITEM"),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(record => record.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase));

        query = sort switch
        {
            "Quest ID (low-high)" => query.OrderBy(static record => record.SourceObject is MapItem mapItem ? mapItem.QuestID : int.MaxValue).ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase),
            "Map item ID (low-high)" => query.Where(static record => record.Category == "MAP ITEM").OrderBy(GetRecordId).ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase),
            "Map item ID (high-low)" => query.Where(static record => record.Category == "MAP ITEM").OrderByDescending(GetRecordId).ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase),
            "Shop ID (low-high)" => query.Where(static record => record.Category == "MAP SHOP").OrderBy(GetRecordId).ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase),
            "Shop ID (high-low)" => query.Where(static record => record.Category == "MAP SHOP").OrderByDescending(GetRecordId).ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase),
            _ => query.OrderBy(static record => record.Metadata, StringComparer.OrdinalIgnoreCase).ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase)
        };

        GameDataRecord[] matches = query.Take(5000).ToArray();
        _mapRecords.Clear();
        foreach (GameDataRecord record in matches)
            _mapRecords.Add(record);

        MapResultMetric.Text = $"{matches.Length:N0} Map Records";
        if (matches.Length == 0)
        {
            MapDetailTitle.Text = _mapSnapshot.Count == 0 ? "Map index not loaded" : "No matching map records";
            MapDetailMeta.Text = _mapSnapshot.Count == 0
                ? "Load the saved map index for map items, then join a map and use Scan Current Map Shops."
                : "Try another map category or search term. Shop scanning indexes the currently joined map.";
            MapDetailText.Text = string.Empty;
            LoadMapShopButton.IsEnabled = false;
            GenerateMergeShopButton.IsEnabled = false;
            GenerateMapQuestsButton.IsEnabled = false;
        }
        else if (MapRecordList.SelectedItem is not GameDataRecord selected || !matches.Contains(selected))
        {
            MapRecordList.SelectedIndex = 0;
        }
    }

    private static int GetRecordId(GameDataRecord record)
    {
        if (record.SourceShopId is int shopId)
            return shopId;

        if (record.SourceQuestId is int questId)
            return questId;

        if (record.SourceObject is ShopItem shopItem)
            return shopItem.ID;

        if (record.SourceObject is MapItem mapItem)
            return mapItem.ID;

        Match match = Regex.Match($"{record.Metadata} {record.Name}", @"(?:ID\s*|#)(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out int id) ? id : int.MaxValue;
    }

}
