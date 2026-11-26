using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.Plugin.DailyTracker.Services;

/// <summary>
/// UI-free backend service for DailyTracker: owns tracked quest definitions,
/// completion state queries via <see cref="IScriptInterface"/>, and script launching.
/// </summary>
public sealed class DailyTrackerService
{
    public sealed record TrackedQuestDefinition(int Id, string Name, string ScriptName, string Category);

    private readonly IScriptInterface _bot;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private static readonly int[] TrackedQuestIds =
    [
        2091, 2098, 802, 803, 3075, 3076, 1239, 10047,
        3759, 3827, 3965, 3596,
        7156, 7157, 7158, 7159, 7160, 7161, 7162, 7163, 7164, 7165,
        8152, 8153, 8154, 8245, 8300, 8397, 8547, 8692, 8746, 9173, 10301
    ];

    private static readonly string[] QuestNames =
    [
        "Mine Crafting", "Hard Core Metals", "Elders' Blood", "Sparrow's Blood",
        "Doom Member Free Spin", "Doom Free Weekly Spin", "Free Member Magic Keys", "A Grain of Dirt",
        "BeastMaster Challenge", "Shadow Shield (Daily)",
        "Glacera Ice Token (Cryomancer)", "Embrace Your Chaos",
        "Heart of Servitude", "Spirit of Justice", "Purification of Chaos", "Steadfast Will",
        "Strike of Order", "Harmony", "Ordinance", "Axiom", "Blessing of Order", "The Final Challenge",
        "Ultra Ezrajal", "Ultra Warden", "Ultra Engineer", "Ultra Tyndarius",
        "Champion Drakath", "Ultra Drago", "Ultra Dage", "Ultra Nulgath",
        "Ultra Darkon", "Ultra Speaker", "Ultra Gramiel"
    ];

    private static readonly Dictionary<int, string> QuestScriptNames = new()
    {
        { 2091, "Dailies\\MineCrafting.cs" },
        { 2098, "Dailies\\HardCoreMetals[Mem].cs" },
        { 802,  "Dailies\\EldersBlood.cs" },
        { 803,  "Dailies\\SparrowsBlood.cs" },
        { 3075, "Dailies\\WheelofDoom.cs" },
        { 3076, "Dailies\\WheelofDoom.cs" },
        { 1239, "Dailies\\FreeDailyBoost[Mem].cs" },
        { 10047, "Dailies\\PearlOfNulgath.cs" },
        { 3759, "Dailies\\BeastMasterChallenge[Mem].cs" },
        { 3827, "Dailies\\ShadowShroud.cs" },
        { 3965, "Dailies\\Cryomancer.cs" },
        { 7156, "Dailies\\LordOfOrder.cs" },
        { 7157, "Dailies\\LordOfOrder.cs" },
        { 7158, "Dailies\\LordOfOrder.cs" },
        { 7159, "Dailies\\LordOfOrder.cs" },
        { 7160, "Dailies\\LordOfOrder.cs" },
        { 7161, "Dailies\\LordOfOrder.cs" },
        { 7162, "Dailies\\LordOfOrder.cs" },
        { 7163, "Dailies\\LordOfOrder.cs" },
        { 7164, "Dailies\\LordOfOrder.cs" },
        { 7165, "Dailies\\LordOfOrder.cs" },
        { 3596, "Dailies\\DagesScrollFragment.cs" },
        { 8152, "Ultras\\UltraEzrajal.cs" },
        { 8153, "Ultras\\UltraWarden.cs" },
        { 8154, "Ultras\\UltraEngineer.cs" },
        { 8245, "Ultras\\UltraAvatarTyndarius.cs" },
        { 8300, "Ultras\\ChampionDrakath.cs" },
        { 8397, "Ultras\\UltraDrago.cs" },
        { 8547, "Ultras\\UltraDage.cs" },
        { 8692, "Ultras\\UltraNulgath.cs" },
        { 8746, "Ultras\\UltraDarkon.cs" },
        { 9173, "Ultras\\UltraSpeaker.cs" },
        { 10301, "Ultras\\UltraGramiel.cs" }
    };

    public static IReadOnlyList<TrackedQuestDefinition> Definitions { get; } = BuildDefinitions();

    public DailyTrackerService(IScriptInterface bot)
    {
        _bot = bot;
    }

    private static IReadOnlyList<TrackedQuestDefinition> BuildDefinitions()
    {
        List<TrackedQuestDefinition> list = new(TrackedQuestIds.Length);
        for (int i = 0; i < TrackedQuestIds.Length; i++)
        {
            int id = TrackedQuestIds[i];
            string category = i switch
            {
                < 8 => "Resources & Miscellaneous",
                < 12 => "Classes & Factions",
                < 22 => "Lord of Order",
                < 26 => "Ultra Bosses (Daily)",
                _ => "Ultra Bosses (Weekly)"
            };
            QuestScriptNames.TryGetValue(id, out string? script);
            list.Add(new TrackedQuestDefinition(id, QuestNames[i], script ?? string.Empty, category));
        }
        return list;
    }

    private void EnsureAllQuestsLoaded()
    {
        try
        {
            if (_bot == null || !_bot.Player.LoggedIn)
                return;

            if (TrackedQuestIds.All(id => _bot.Quests.Tree.Any(q => q.ID == id)))
                return;

            _bot.Quests.Load(TrackedQuestIds);
            _bot.Wait.ForTrue(() => TrackedQuestIds.All(id => _bot.Quests.Tree.Any(q => q.ID == id)), null, 15, 200);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Batch quest loading failed: {ex}");
        }
    }

    private bool CheckQuestDone(int questId)
    {
        bool done = _bot.Quests.IsDailyComplete(questId);
        if (!done && questId >= 7156 && questId <= 7165)
        {
            try
            {
                int targetVal = questId - 7156 + 1;
                if (_bot.Flash.CallGameFunction<int>("world.getQuestValue", 327) >= targetVal)
                    done = true;
            }
            catch { }
        }
        return done;
    }

    public async Task<Dictionary<int, bool>?> QueryCompletionStatesAsync(CancellationToken ct = default)
    {
        if (_bot == null || ct.IsCancellationRequested)
            return null;

        if (!await _refreshLock.WaitAsync(0, ct).ConfigureAwait(false))
            return null;

        try
        {
            return await Task.Run(() =>
            {
                EnsureAllQuestsLoaded();
                Dictionary<int, bool> results = new(TrackedQuestIds.Length);
                foreach (int questId in TrackedQuestIds)
                {
                    if (ct.IsCancellationRequested)
                        break;
                    try
                    {
                        results[questId] = CheckQuestDone(questId);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Quest refresh failed {questId}: {ex}");
                    }
                }
                return results;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public static string? ResolveCategoryScript(string? categoryName) =>
        categoryName == "Lord of Order" ? "Dailies\\LordOfOrder.cs" : null;

    public static string? ResolveScriptPath(string scriptName)
    {
        if (string.IsNullOrWhiteSpace(scriptName))
            return null;

        string normalized = scriptName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        string path = Path.Combine(ClientFileSources.SkuaScriptsDIR, normalized);
        if (File.Exists(path))
            return path;

        string fallback = Path.Combine(AppContext.BaseDirectory, "Scripts", normalized);
        return File.Exists(fallback) ? fallback : null;
    }

    public Task StartScriptAsync(string scriptName)
    {
        if (_bot == null)
            return Task.CompletedTask;

        string? path = ResolveScriptPath(scriptName);
        if (path is null)
        {
            Trace.WriteLine($"Script not found: {scriptName}");
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            try
            {
                _bot.Manager.SetLoadedScript(path);
                await _bot.Manager.StartScript().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to start script {scriptName}: {ex}");
            }
        });
    }
}
