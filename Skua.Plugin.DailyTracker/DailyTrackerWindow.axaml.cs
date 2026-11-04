using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;

namespace Skua.Plugin.DailyTracker;

public partial class DailyTrackerWindow : Window
{
    private readonly IScriptInterface _bot;
    private CancellationTokenSource? _cts;
    private Timer? _refreshTimer;
    private static readonly TimeSpan _refreshInterval = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private List<QuestCategory> _categories = [];
    private bool _isRefreshing;

    private static readonly int[] _trackedQuests =
    [
        2091, 2098, 802, 803, 3075, 3076, 1239, 10047,
        3759, 3827, 3965, 3596,
        7156, 7157, 7158, 7159, 7160, 7161, 7162, 7163, 7164, 7165,
        8152, 8153, 8154, 8245, 8300, 8397, 8547, 8692, 8746, 9173, 10301
    ];

    private static readonly string[] _questNames =
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

    private static readonly Dictionary<int, string> _questScriptNames = new()
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

    public DailyTrackerWindow() : this(null!)
    {
    }

    public DailyTrackerWindow(IScriptInterface bot)
    {
        InitializeComponent();
        _bot = bot;
        Skua.Shared.Avalonia.Services.WindowPlacementHelper.CenterOnActiveScreen(this);
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e) => await InitializeAsync();

    private async Task InitializeAsync()
    {
        if (_bot == null)
            return;

        _cts = new CancellationTokenSource();
        try
        {
            await LoadQuestsAsync();
            RegisterEventHandlers();
            StartLiveUpdates();
        }
        catch (TaskCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"DailyTracker InitializeAsync error: {ex}");
        }
    }

    private void EnsureAllQuestsLoaded()
    {
        try
        {
            if (_bot == null || !_bot.Player.LoggedIn)
                return;

            if (_trackedQuests.All(id => _bot.Quests.Tree.Any(q => q.ID == id)))
                return;

            _bot.Quests.Load(_trackedQuests);
            _bot.Wait.ForTrue(() => _trackedQuests.All(id => _bot.Quests.Tree.Any(q => q.ID == id)), null, 15, 200);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Batch quest loading failed: {ex}");
        }
    }

    private async Task LoadQuestsAsync()
    {
        if (_bot == null)
            return;

        bool[] doneStates = await Task.Run(() =>
        {
            EnsureAllQuestsLoaded();
            bool[] results = new bool[_trackedQuests.Length];
            for (int i = 0; i < _trackedQuests.Length; i++)
            {
                try
                {
                    results[i] = _bot.Quests.IsDailyComplete(_trackedQuests[i]);
                    if (!results[i] && _trackedQuests[i] >= 7156 && _trackedQuests[i] <= 7165)
                    {
                        int targetVal = _trackedQuests[i] - 7156 + 1;
                        if (_bot.Flash.CallGameFunction<int>("world.getQuestValue", 327) >= targetVal)
                            results[i] = true;
                    }
                }
                catch
                {
                    results[i] = false;
                }
            }
            return results;
        });

        List<QuestItem> questItems = [];
        for (int i = 0; i < _trackedQuests.Length; i++)
        {
            QuestItem item = new(_trackedQuests[i], _questNames[i], doneStates[i]);
            if (_questScriptNames.TryGetValue(_trackedQuests[i], out string? script))
                item.ScriptName = script;
            questItems.Add(item);
        }

        _categories =
        [
            new() { Name = "Resources & Miscellaneous", Quests = [.. questItems.Take(8)] },
            new() { Name = "Classes & Factions", Quests = [.. questItems.Skip(8).Take(4)] },
            new() { Name = "Lord of Order", Quests = [.. questItems.Skip(12).Take(10)] },
            new() { Name = "Ultra Bosses (Daily)", Quests = [.. questItems.Skip(22).Take(4)] },
            new() { Name = "Ultra Bosses (Weekly)", Quests = [.. questItems.Skip(26)] }
        ];

        Dispatcher.UIThread.Post(() => CategoriesControl.ItemsSource = _categories);
    }

    private void RegisterEventHandlers()
    {
        StrongReferenceMessenger.Default.Register<DailyTrackerWindow, QuestTurninMessage, int>(
            this, (int)MessageChannels.GameEvents, (r, m) => r.OnQuestTurnIn(m));

        StrongReferenceMessenger.Default.Register<DailyTrackerWindow, ScriptStoppedMessage, int>(
            this, (int)MessageChannels.ScriptStatus, (r, m) => r.OnScriptStopped(m));
    }

    private void OnQuestTurnIn(QuestTurninMessage message)
    {
        int questId = message.QuestID;
        foreach (QuestCategory cat in _categories)
        {
            QuestItem? item = cat.Quests.Find(q => q.ID == questId);
            if (item != null)
            {
                Dispatcher.UIThread.Post(() => item.IsDone = true);
                break;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(800);
                if (_cts?.IsCancellationRequested != true)
                    await RefreshQuestStatesAsync();
            }
            catch { }
        });
    }

    private void OnScriptStopped(ScriptStoppedMessage message)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500);
                if (_cts?.IsCancellationRequested != true)
                    await RefreshQuestStatesAsync();
            }
            catch { }
        });
    }

    private async Task RefreshQuestStatesAsync()
    {
        if (_bot == null)
            return;

        if (!await _refreshLock.WaitAsync(0))
            return;

        try
        {
            await Task.Run(() =>
            {
                EnsureAllQuestsLoaded();
                foreach (QuestCategory category in _categories)
                {
                    foreach (QuestItem quest in category.Quests)
                    {
                        bool newState;
                        try
                        {
                            newState = _bot.Quests.IsDailyComplete(quest.ID);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Trace.WriteLine($"Quest refresh failed {quest.ID}: {ex}");
                            continue;
                        }

                        if (!newState && quest.ID >= 7156 && quest.ID <= 7165)
                        {
                            try
                            {
                                int targetVal = quest.ID - 7156 + 1;
                                if (_bot.Flash.CallGameFunction<int>("world.getQuestValue", 327) >= targetVal)
                                    newState = true;
                            }
                            catch { }
                        }

                        if (quest.IsDone != newState)
                            Dispatcher.UIThread.Post(() => quest.IsDone = newState);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"RefreshQuestStatesAsync error: {ex}");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void StartLiveUpdates()
    {
        _refreshTimer = new Timer(async _ =>
        {
            try
            {
                if (_cts?.IsCancellationRequested != true)
                    await RefreshQuestStatesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"DailyTracker live update failed: {ex}");
            }
        }, null, _refreshInterval, _refreshInterval);
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (_isRefreshing)
            return;

        _isRefreshing = true;
        RefreshBtn.IsEnabled = false;
        if (RefreshText != null)
            RefreshText.Text = "Checking...";

        try
        {
            await RefreshQuestStatesAsync();
        }
        finally
        {
            if (RefreshText != null)
                RefreshText.Text = "Refresh";
            RefreshBtn.IsEnabled = true;
            _isRefreshing = false;
        }
    }

    private void StartScript_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not string scriptName || string.IsNullOrWhiteSpace(scriptName)) return;
        StartScript(scriptName);
    }

    private void StartCategory_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string? categoryName = btn.Tag as string;
        if (categoryName == "Lord of Order")
            StartScript("Dailies\\LordOfOrder.cs");
    }

    private void StartScript(string scriptName)
    {
        string normalized = scriptName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        string path = Path.Combine(ClientFileSources.SkuaScriptsDIR, normalized);
        if (!File.Exists(path))
        {
            string fallback = Path.Combine(AppContext.BaseDirectory, "Scripts", normalized);
            if (File.Exists(fallback))
                path = fallback;
        }

        if (!File.Exists(path))
        {
            System.Diagnostics.Trace.WriteLine($"Script not found: {path}");
            return;
        }
        Task.Run(async () =>
        {
            try
            {
                _bot.Manager.SetLoadedScript(path);
                await _bot.Manager.StartScript();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Failed to start script {scriptName}: {ex}");
            }
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            StrongReferenceMessenger.Default.UnregisterAll(this);
        }
        catch { }
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _refreshTimer?.Dispose();
        _refreshTimer = null;
    }
}

public class QuestCategory : INotifyPropertyChanged
{
    private string _name = string.Empty;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasCategoryScript)); }
    }

    public List<QuestItem> Quests { get; set; } = [];
    public bool HasCategoryScript => Name == "Lord of Order";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class QuestItem : INotifyPropertyChanged
{
    private static readonly IBrush _completedBrush = new SolidColorBrush(Color.Parse("#2E7D32"));
    private static readonly IBrush _incompleteBrush = new SolidColorBrush(Color.Parse("#C62828"));

    public int ID { get; set; }
    public string Name { get; set; } = string.Empty;

    private bool _isDone;

    public bool IsDone
    {
        get => _isDone;
        set
        {
            _isDone = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowScript));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private string _scriptName = string.Empty;

    public string ScriptName
    {
        get => _scriptName;
        set
        {
            _scriptName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowScript));
        }
    }

    public bool HasScript => !string.IsNullOrEmpty(ScriptName);
    public bool ShowScript => HasScript && !IsDone && (ID < 7156 || ID > 7165);
    public bool IsLordOfOrder => ID >= 7156 && ID <= 7165;
    public IBrush StatusBrush => IsDone ? _completedBrush : _incompleteBrush;
    public string StatusText => IsDone ? "Completed" : "Incomplete";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public QuestItem(int id, string name, bool isDone)
    {
        ID = id;
        Name = name;
        IsDone = isDone;
    }
}
