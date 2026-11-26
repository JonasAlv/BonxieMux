using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Auras;
using Skua.Core.Models.Monsters;
using Skua.Core.Models.Skills;
using Skua.Core.Models.Items;
using Skua.Core.Models.Quests;
using Skua.Core.Models.Shops;
using Skua.Shared.Avalonia.Theming;

namespace Skua.Plugin.Omniscope;

/// <summary>
/// Displays live, read-only game telemetry using the public VibeSkua scripting interfaces.
/// </summary>
public partial class OmniscopeWindow : Window
{
    private readonly IScriptInterface _bot;
    private static readonly Regex ActionScriptTokenRegex = new(
        @"(?<comment>//.*|/\*.*?(?:\*/|$))|(?<string>""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*')|(?<keyword>\b(?:package|import|class|interface|extends|implements|public|private|protected|internal|static|final|override|function|var|const|return|new|this|super|if|else|for|while|do|switch|case|break|continue|try|catch|throw|with|as|is|in|instanceof|typeof|void|delete|true|false|null|undefined|namespace)\b)|(?<number>\b(?:0[xX][0-9A-Fa-f]+|\d+(?:\.\d+)?)\b)|(?<function>\b[A-Za-z_$][A-Za-z0-9_$]*(?=\s*\())|(?<type>\b[A-Z][A-Za-z0-9_]*\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static IBrush CodeDefaultBrush => UiTokens.GetBrush(UiTokens.SkuaTextBrush, Brushes.Gainsboro);
    private static IBrush CodeLineNumberBrush => UiTokens.GetBrush(UiTokens.SkuaMutedTextBrush, Brushes.Gray);
    private static IBrush CodeCommentBrush => UiTokens.GetBrush(UiTokens.SkuaSuccessBrush, Brushes.SeaGreen);
    private static IBrush CodeStringBrush => UiTokens.GetBrush(UiTokens.SkuaWarningBrush, Brushes.Goldenrod);
    private static IBrush CodeKeywordBrush => UiTokens.GetBrush(UiTokens.SkuaAccentBrush, Brushes.CornflowerBlue);
    private static IBrush CodeNumberBrush => UiTokens.GetBrush(UiTokens.SkuaDangerBrush, Brushes.IndianRed);
    private static IBrush CodeFunctionBrush => UiTokens.GetBrush(UiTokens.SkuaTagTextBrush, Brushes.DeepSkyBlue);
    private static IBrush CodeTypeBrush => UiTokens.GetBrush(UiTokens.SkuaTextBrush, Brushes.Gainsboro);
    private const int MaximumVisibleCodeCharacters = 200_000;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly ObservableCollection<EntityRow> _entities = [];
    private EntityRow[] _entitySnapshot = [];
    private readonly ObservableCollection<string> _players = [];
    private readonly ObservableCollection<AuraRow> _auras = [];
    private readonly ObservableCollection<SkillRow> _skills = [];
    private readonly ObservableCollection<string> _quests = [];
    private readonly ObservableCollection<InventoryRow> _inventory = [];
    private readonly ObservableCollection<EventRow> _events = [];
    private readonly List<EventRow> _eventHistory = [];
    private string[] _playerSnapshot = [];
    private bool _avatarLiveFollow = true;
    private bool _entityLiveFollow = true;
    private bool _playerLiveFollow = true;
    private bool _eventLiveFollow = true;
    private readonly ObservableCollection<GameDataRecord> _dataRecords = [];
    private readonly ObservableCollection<GameDataRecord> _mapRecords = [];
    private readonly List<GameDataRecord> _swfSourceRecords = [];
    private readonly List<GameDataRecord> _mapShopRecords = [];
    private readonly object _dropHistoryLock = new();
    private readonly Queue<DropObservation> _dropHistory = new();
    private readonly Queue<MonsterKillObservation> _killHistory = new();
    private int _lastKilledMapId;
    private DateTimeOffset _lastKilledAt = DateTimeOffset.MinValue;
    private List<GameDataRecord> _dataSnapshot = [];
    private List<GameDataRecord> _mapSnapshot = [];
    private MapItem[] _mapItemSnapshot = [];
    private bool _mapAnalysisRunning;
    private bool _mapShopIndexRunning;
    private bool _mapShopLoadRunning;
    private bool _developerHelperRunning;
    private bool _swfIndexRunning;
    private string _lastMap = string.Empty;
    private string _lastTarget = string.Empty;
    private string _lastPlayerState = string.Empty;
    private string _lastVitals = string.Empty;
    private string _lastClass = string.Empty;
    private string _lastAuraSignature = string.Empty;
    private string _lastQuestSignature = string.Empty;
    private int _lastMonsterCount = -1;
    private int _lastPlayerCount = -1;
    private int _lastKillCount = -1;
    private bool _refreshing;
    private bool _xamlInitialized;

    /// <summary>
    /// Provides the parameterless constructor required by Avalonia's runtime XAML loader.
    /// </summary>
    public OmniscopeWindow() : this(null!)
    {
    }

    /// <summary>
    /// Creates the live observatory for the active game client.
    /// </summary>
    /// <param name="bot">The scripting interface exposed by the current client.</param>
    public OmniscopeWindow(IScriptInterface bot)
    {
        InitializeComponent();
        Skua.Shared.Avalonia.Services.ShellWindowHelper.ConfigureFramelessWindow(this);
        _xamlInitialized = true;
        _bot = bot;
        if (_bot is null)
            return;

        EntityList.ItemsSource = _entities;
        PlayerList.ItemsSource = _players;
        AuraList.ItemsSource = _auras;
        SkillList.ItemsSource = _skills;
        QuestList.ItemsSource = _quests;
        InventoryList.ItemsSource = _inventory;
        EventList.ItemsSource = _events;
        DataRecordList.ItemsSource = _dataRecords;
        MapRecordList.ItemsSource = _mapRecords;

        EntityList.SelectionChanged += EntityList_SelectionChanged;
        EntityList.PointerWheelChanged += EntityList_PointerWheelChanged;
        EntityList.PointerPressed += EntityList_PointerPressed;
        PlayerList.PointerWheelChanged += PlayerList_PointerWheelChanged;
        PlayerList.PointerPressed += PlayerList_PointerPressed;
        EventList.PointerWheelChanged += EventList_PointerWheelChanged;
        EventList.PointerPressed += EventList_PointerPressed;
        _refreshTimer.Tick += RefreshTimer_Tick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_bot is null)
            return;

        try
        {
            _bot.Events.MonsterKilled += OnMonsterKilled;
            _bot.Events.ItemDropped += OnItemDropped;
            _bot.Events.QuestAccepted += OnQuestAccepted;
            _bot.Events.QuestTurnedIn += OnQuestTurnedIn;
            _bot.Events.TryBuyItem += OnTryBuyItem;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE event subscription failed: {ex}");
        }

        RefreshTelemetry();
        _refreshTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        try
        {
            _bot.Events.MonsterKilled -= OnMonsterKilled;
            _bot.Events.ItemDropped -= OnItemDropped;
            _bot.Events.QuestAccepted -= OnQuestAccepted;
            _bot.Events.QuestTurnedIn -= OnQuestTurnedIn;
            _bot.Events.TryBuyItem -= OnTryBuyItem;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE event unsubscription failed: {ex}");
        }
    }

    private void TelemetryRateBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_xamlInitialized || TelemetryRateBox?.SelectedItem is not ComboBoxItem selected ||
            !int.TryParse(selected.Tag?.ToString(), out int intervalMilliseconds))
            return;

        _refreshTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(intervalMilliseconds, 100, 10000));
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e) => RefreshTelemetry();

    private void RefreshTelemetry()
    {
        if (_refreshing)
            return;

        _refreshing = true;
        try
        {
            DateTime now = DateTime.Now;
            ClockText.Text = now.ToString("HH:mm:ss");

            bool loggedIn = _bot.Player.LoggedIn;
            bool playing = _bot.Player.Playing;
            ConnectionText.Text = loggedIn ? playing ? "Live / Avatar Active" : "Connected / Loading Avatar" : "Waiting For Game Session";
            ConnectionIndicator.Fill = playing
                ? UiTokens.GetBrush(UiTokens.SkuaSuccessBrush, Brushes.MediumSpringGreen)
                : loggedIn
                    ? UiTokens.GetBrush(UiTokens.SkuaWarningBrush, Brushes.Goldenrod)
                    : UiTokens.GetBrush(UiTokens.SkuaDangerBrush, Brushes.IndianRed);

            int health = loggedIn ? _bot.Player.Health : 0;
            int maxHealth = loggedIn ? _bot.Player.MaxHealth : 0;
            int mana = loggedIn ? _bot.Player.Mana : 0;
            int maxMana = loggedIn ? _bot.Player.MaxMana : 0;
                VitalsMetric.Text = loggedIn ? $"{health:N0} / {maxHealth:N0} HP" : "-- / -- HP";
            SetProgress(HealthProgressBar, health, maxHealth);
            PositionMetric.Text = loggedIn ? $"X {_bot.Player.X:N0}  /  Y {_bot.Player.Y:N0}" : "X --  /  Y --";
            CellMetric.Text = loggedIn ? $"Cell {NullLabel(_bot.Player.Cell)}  /  Pad {NullLabel(_bot.Player.Pad)}" : "Cell --  /  Pad --";

            if (_avatarLiveFollow)
            {
                    PlayerNameText.Text = loggedIn ? _bot.Player.Username : "No active player session";
                PlayerClassText.Text = $"Class: {_bot.Player.CurrentClass?.Name ?? "Unavailable"}  /  Rank {_bot.Player.CurrentClassRank}";
                LevelText.Text = loggedIn ? _bot.Player.Level.ToString("N0") : "--";
                XpText.Text = loggedIn ? _bot.Stats.ExperienceProgressText : "XP unavailable";
                SetProgress(XpProgressBar, _bot.Stats.ExperienceProgress, 100);
                StateText.Text = loggedIn ? GetPlayerState(_bot.Player.State) : "Offline";
                GoldText.Text = loggedIn ? _bot.Player.Gold.ToString("N0") : "--";
                CoinsText.Text = loggedIn ? _bot.Player.Coins.ToString("N0") : "--";
                GuildText.Text = loggedIn ? NullLabel(_bot.Player.Guild) : "--";
                ServerText.Text = loggedIn ? NullLabel(_bot.Player.ServerIP) : "--";

                HealthText.Text = loggedIn ? $"{health:N0} / {maxHealth:N0}" : "-- / --";
                ManaText.Text = loggedIn ? $"{mana:N0} / {maxMana:N0}" : "-- / --";
                SetProgress(HealthDetailBar, health, maxHealth);
                SetProgress(ManaProgressBar, mana, maxMana);

                SessionText.Text = loggedIn
                ? $"Avatar loaded: {_bot.Player.Loaded}\nTarget: {(_bot.Player.Target?.Name ?? "None")}\nCombat: {(_bot.Player.InCombat ? "Engaged" : "Idle")}  /  AFK: {(_bot.Player.AFK ? "Yes" : "No")}"
                : "Waiting for the game client to expose a logged-in avatar.";

            }

            bool mapLoaded = _bot.Map.Loaded;
            string mapName = mapLoaded ? _bot.Map.FullName : "Map unavailable";
            MapMetric.Text = mapName;
            RoomMetric.Text = mapLoaded ? $"Room {_bot.Map.RoomNumber}  /  ID {_bot.Map.RoomID}  /  {_bot.Map.PlayerCount} players" : "Waiting for map data";
            TrackChange(ref _lastMap, mapName, "World Transition");
            string playerState = loggedIn ? GetPlayerState(_bot.Player.State) : "Offline";
            TrackChange(ref _lastPlayerState, playerState, "Player State");

            string className = loggedIn ? _bot.Player.CurrentClass?.Name ?? "Unknown class" : "Offline";
            TrackChange(ref _lastClass, className, "Class Change");

            string vitals = loggedIn ? $"{health}/{maxHealth} HP · {mana}/{maxMana} MP" : "Offline";
            if (_lastVitals.Length > 0 && _lastVitals != vitals)
                AddEvent("Vitals Update", vitals);
            _lastVitals = vitals;

            string target = _bot.Player.Target is Monster currentTarget ? $"{currentTarget.Name} [{currentTarget.ID}]" : "No target";
            TrackChange(ref _lastTarget, target, "Target Change");

            Monster[] monsters = mapLoaded ? _bot.Monsters.MapMonsters.ToArray() : [];
            int aliveCount = monsters.Count(static monster => monster.Alive);
            EntityMetric.Text = monsters.Length.ToString("N0");
            EntitySubMetric.Text = $"{aliveCount} alive  /  {monsters.Length - aliveCount} inactive";
            if (_lastMonsterCount != monsters.Length)
            {
                if (_lastMonsterCount >= 0)
                    AddEvent("Entity Field", $"{monsters.Length} monsters observed in the current map");
                _lastMonsterCount = monsters.Length;
            }

            UpdateEntities(monsters);
            UpdatePlayers(mapLoaded);
            UpdateAuras(loggedIn);
            UpdateSkills(loggedIn);
            UpdateSessionAnalytics(loggedIn);

            Skua.Core.Models.Quests.Quest[] activeQuests = loggedIn ? _bot.Quests.Active.ToArray() : [];
            Skua.Core.Models.Quests.Quest[] readyQuests = loggedIn ? _bot.Quests.Completed.ToArray() : [];
            string questSignature = $"{activeQuests.Length}:{readyQuests.Length}";
            QuestMetric.Text = $"Quests: {activeQuests.Length:N0} active  /  {readyQuests.Length:N0} ready";
            if (_lastQuestSignature.Length > 0 && _lastQuestSignature != questSignature)
                AddEvent("Quest Tracker", QuestMetric.Text);
            _lastQuestSignature = questSignature;

            string[] questRows = readyQuests.Take(4).Select(static quest => $"Ready  /  {quest.Name} [{quest.ID}]")
                .Concat(activeQuests.Take(4).Select(static quest => $"Active  /  {quest.Name} [{quest.ID}]"))
                .ToArray();
            if (!_quests.SequenceEqual(questRows))
            {
                _quests.Clear();
                foreach (string questRow in questRows)
                    _quests.Add(questRow);
            }
        }
        catch (Exception ex)
        {
            ConnectionText.Text = "Telemetry Degraded";
            ConnectionIndicator.Fill = UiTokens.GetBrush(UiTokens.SkuaDangerBrush, Brushes.OrangeRed);
            System.Diagnostics.Trace.WriteLine($"OMNISCOPE telemetry refresh failed: {ex}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void OnMonsterKilled(int mapId)
    {
        DateTimeOffset timestamp = DateTimeOffset.Now;
        lock (_dropHistoryLock)
        {
            _lastKilledMapId = mapId;
            _lastKilledAt = timestamp;
            _killHistory.Enqueue(new MonsterKillObservation(timestamp, mapId));
            while (_killHistory.Count > 500)
                _killHistory.Dequeue();
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
                AddEvent("Monster Killed", $"Monster MapID {mapId}");
        });
    }

    private void OnItemDropped(ItemBase item, bool addedToInventory, int quantityNow)
    {
        DateTimeOffset timestamp = DateTimeOffset.Now;
        int itemId = item.ID;
        string itemName = NullLabel(item.Name);
        int itemQuantity = item.Quantity;
        int? precedingKillMapId = null;
        double? secondsAfterKill = null;

        lock (_dropHistoryLock)
        {
            if (_lastKilledAt != DateTimeOffset.MinValue)
            {
                TimeSpan elapsed = timestamp - _lastKilledAt;
                if (elapsed >= TimeSpan.Zero && elapsed <= TimeSpan.FromSeconds(12))
                {
                    precedingKillMapId = _lastKilledMapId;
                    secondsAfterKill = elapsed.TotalSeconds;
                }
            }

            _dropHistory.Enqueue(new DropObservation(
                timestamp,
                itemId,
                itemName,
                itemQuantity,
                addedToInventory,
                quantityNow,
                precedingKillMapId,
                secondsAfterKill));

            while (_dropHistory.Count > 500)
                _dropHistory.Dequeue();
        }

        string context = precedingKillMapId.HasValue
            ? $"  /  preceding kill MapID {precedingKillMapId.Value} ({secondsAfterKill:0.0}s earlier, correlation only)"
            : string.Empty;
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
                AddEvent("Item Drop", $"{itemName} [{itemId}] ×{itemQuantity}  /  Added to inventory: {addedToInventory}  /  quantity now: {quantityNow}{context}");
        });
    }

    private void OnQuestAccepted(int questId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
                AddEvent("Quest Accepted", $"Quest ID {questId}");
        });
    }

    private void OnQuestTurnedIn(int questId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
                AddEvent("Quest Turned In", $"Quest ID {questId}");
        });
    }

    private void OnTryBuyItem(int shopId, int itemId, int shopItemId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
                AddEvent("Shop Purchase Attempt", $"Shop {shopId}  /  Item {itemId}  /  ShopItemID {shopItemId}");
        });
    }

}
