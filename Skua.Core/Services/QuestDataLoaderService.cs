using Newtonsoft.Json;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Quests;
using System.Dynamic;

namespace Skua.Core.Services;

public class QuestDataLoaderService : IQuestDataLoaderService
{
    public QuestDataLoaderService(IScriptQuest quests, IScriptPlayer player, IFlashUtil flash, IScriptWait wait)
    {
        _quests = quests;
        _flash = flash;
        _player = player;
        _wait = wait;
    }

    private readonly IScriptQuest _quests;
    private readonly IFlashUtil _flash;
    private readonly IScriptPlayer _player;
    private readonly IScriptWait _wait;
    private readonly Dictionary<string, List<QuestData>?> _cachedQuests = new();

    public void ClearCache()
    {
        _cachedQuests.Clear();
    }

    public async Task<List<QuestData>> GetFromFileAsync(string fileName)
    {
        fileName = Path.Combine(ClientFileSources.SkuaDIR, fileName);
        if (!File.Exists(fileName))
            return new();

        if (_cachedQuests.TryGetValue($"CachedQuests_{fileName}", out List<QuestData>? quests))
            return quests ?? new();

        string text = await File.ReadAllTextAsync(fileName);
        quests = JsonConvert.DeserializeObject<List<QuestData>>(text);
        _cachedQuests.Add($"CachedQuests_{fileName}", quests);
        return quests ?? new();
    }

    public async Task<List<QuestData>> UpdateAsync(string fileName, bool all, IProgress<string>? progress, CancellationToken token)
    {
        return await Task.Run(async () =>
        {
            if (!_player.LoggedIn)
                return _quests.Cached = await GetFromFileAsync(fileName);

            // Clear cache to ensure we get fresh data during updates
            string cacheKey = $"CachedQuests_{Path.Combine(ClientFileSources.SkuaDIR, fileName)}";
            _cachedQuests.Remove(cacheKey);

            // Load existing data first - we'll use this for incremental updates or if cancellation happens
            List<QuestData> existingQuestData = await GetFromFileAsync(fileName);
            _quests.Cached = all ? new List<QuestData>() : existingQuestData;

            int start = 1;
            if (!all && (_quests.Cached.Count > 0))
                start = _quests.Cached.Last().ID + 1;

            List<QuestData> quests = new();
            for (int i = start; i < 13000; i += 29)
            {
                if (token.IsCancellationRequested)
                    break;

                _flash.SetGameObject("world.questTree", new ExpandoObject());
                progress?.Report($"Loading Quests {i}-{i + 29}...");

                _quests.Load(Enumerable.Range(i, 29).ToArray());

                if (!_wait.ForQuestLoad(i, i + 28, 100))
                {
                    progress?.Report("No more quests found.");
                    break;
                }

                List<Quest> loadedQuests = _quests.Tree.Where(q => q.ID >= i && q.ID <= i + 28).ToList();
                if (loadedQuests.Count == 0)
                {
                    progress?.Report("No more quests found.");
                    break;
                }

                quests.AddRange(loadedQuests.Select(q => ConvertToQuestData(q)));
                if (!token.IsCancellationRequested)
                    await Task.Delay(1500);
            }

            // Handle cancellation gracefully and merge data appropriately
            if (!all)
            {
                // For incremental updates, merge with existing cached data
                quests.AddRange(_quests.Cached);
            }
            else if (token.IsCancellationRequested)
            {
                if (quests.Count == 0)
                {
                    // If cancelled early in full update with no new data, keep existing data
                    if (existingQuestData.Count > 0)
                    {
                        progress?.Report("Update cancelled - keeping existing quest data");
                        return _quests.Cached = existingQuestData;
                    }
                }
                else
                {
                    // If we got some new data before cancellation, merge it with existing data
                    // Keep newer data (higher IDs) from new fetch, older data from existing
                    if (quests.Any())
                    {
                        int maxNewId = quests.Max(q => q.ID);
                        IEnumerable<QuestData> olderExistingData = existingQuestData.Where(q => q.ID > maxNewId);
                        quests.AddRange(olderExistingData);
                        progress?.Report($"Update cancelled - saved {quests.Count} quests (partial data + existing)");
                    }
                    else
                    {
                        // No new data was fetched, just keep existing data
                        progress?.Report("Update cancelled - keeping existing quest data");
                        return _quests.Cached = existingQuestData;
                    }
                }
            }

            // Don't pass cancelled token to file write operation
            CancellationToken writeToken = token.IsCancellationRequested ? CancellationToken.None : token;
            await File.WriteAllTextAsync(Path.Combine(ClientFileSources.SkuaDIR, fileName), JsonConvert.SerializeObject(quests.Distinct().OrderBy(q => q.ID), Formatting.Indented), writeToken);
            progress?.Report($"Getting quests from file {fileName}");

            // Clear cache again to force reading the newly written file
            _cachedQuests.Remove(cacheKey);

            HashSet<int> existingQuestIds = quests.Select(q => q.ID).ToHashSet();
            quests.AddRange(_quests.Cached.Where(q => !existingQuestIds.Contains(q.ID)));
            await File.WriteAllTextAsync(Path.Combine(ClientFileSources.SkuaDIR, fileName), JsonConvert.SerializeObject(quests.OrderBy(q => q.ID), Formatting.Indented), token);
            progress?.Report($"Getting quests from file {fileName}");

            _cachedQuests.Remove($"CachedQuests_{Path.Combine(ClientFileSources.SkuaDIR, fileName)}");
            return _quests.Cached = await GetFromFileAsync(fileName);
        });
    }

    public async Task<List<QuestData>> UpdateRangeAsync(string fileName, int startId, int endId, IProgress<string>? progress, CancellationToken token)
    {
        return await Task.Run(async () =>
        {
            if (!_player.LoggedIn)
                return _quests.Cached = await GetFromFileAsync(fileName);

            _quests.Cached = await GetFromFileAsync(fileName);

            List<QuestData> quests = new();
            for (int i = startId; i <= endId; i += 29)
            {
                if (token.IsCancellationRequested)
                    break;

                _flash.SetGameObject("world.questTree", new ExpandoObject());
                int questCount = Math.Min(29, endId - i + 1);
                int rangeEnd = i + questCount - 1;
                progress?.Report($"Loading Quests {i}-{rangeEnd}...");

                _quests.Load(Enumerable.Range(i, questCount).ToArray());

                if (!_wait.ForQuestLoad(i, rangeEnd, 100))
                {
                    progress?.Report("No more quests found.");
                    break;
                }

                List<Quest> loadedQuests = _quests.Tree.Where(q => q.ID >= i && q.ID <= rangeEnd).ToList();
                if (loadedQuests.Count == 0)
                {
                    progress?.Report("No more quests found.");
                    break;
                }

                quests.AddRange(loadedQuests.Select(q => ConvertToQuestData(q)));
                if (!token.IsCancellationRequested)
                    await Task.Delay(1500);
            }

            HashSet<int> existingQuestIds = quests.Select(q => q.ID).ToHashSet();
            quests.AddRange(_quests.Cached.Where(q => !existingQuestIds.Contains(q.ID)));
            await File.WriteAllTextAsync(Path.Combine(ClientFileSources.SkuaDIR, fileName), JsonConvert.SerializeObject(quests.OrderBy(q => q.ID), Formatting.Indented), token);
            progress?.Report($"Getting quests from file {fileName}");

            _cachedQuests.Remove($"CachedQuests_{Path.Combine(ClientFileSources.SkuaDIR, fileName)}");

            return _quests.Cached = await GetFromFileAsync(fileName);
        });
    }

    private QuestData ConvertToQuestData(Quest q)
    {
        return new()
        {
            ID = q.ID,
            Name = q.Name,
            AcceptRequirements = q.AcceptRequirements,
            Field = q.Field,
            Gold = q.Gold,
            Index = q.Index,
            Level = q.Level,
            Once = q.Once,
            RequiredClassID = q.RequiredClassID,
            RequiredClassPoints = q.RequiredClassPoints,
            RequiredFactionId = q.RequiredFactionId,
            RequiredFactionRep = q.RequiredFactionRep,
            Requirements = q.Requirements,
            Rewards = q.Rewards,
            SimpleRewards = q.SimpleRewards,
            Slot = q.Slot,
            Upgrade = q.Upgrade,
            Value = q.Value,
            XP = q.XP
        };
    }
}
