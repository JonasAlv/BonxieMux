using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.Services;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Core.Models.Items;
using Skua.Core.Services;
using Skua.Core.Threading;
using Skua.Core.Utils;
using System.Diagnostics;

namespace Skua.Core.Scripts;

public partial class ScriptInterface : IScriptInterface, IScriptInterfaceManager, IDisposable
{
    private CancellationTokenSource? ScriptInterfaceCTS;
    private readonly Thread ScriptInterfaceThread;
    private GameStateChannel? _stateChannel;
    private GameApiThreadSafeWrapper? _apiWrapper;
    private const int _timerDelay = 20;
    private readonly TimeLimiter _limit = new();
    private readonly ILogService _logger;
    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;
    private bool _playerDead;

    public bool ShouldExit => Manager.ShouldExit;
    public Version Version { get; }

    public IScriptStatus Manager { get; }
    public IFlashUtil Flash { get; }
    public IScriptAuto Auto { get; }
    public IMessenger Messenger { get; }
    public IScriptBoost Boosts { get; }
    public IScriptBotStats Stats { get; }
    public IScriptSelfAuras Self { get; }
    public IScriptTargetAuras Target { get; }
    public IAuraMonitorService AuraMonitor { get; }
    public IUltraBossHelper UltraBossHelper { get; }
    public IScriptCombat Combat { get; }
    public IScriptKill Kill { get; }
    public IScriptHunt Hunt { get; }
    public IScriptDrop Drops { get; }
    public IScriptEvent Events { get; }
    public IScriptFaction Reputation { get; }
    public IScriptHouseInv House { get; }
    public IScriptInventory Inventory { get; }
    public IScriptTempInv TempInv { get; }
    public IScriptBank Bank { get; }
    public IScriptInventoryHelper InvHelper { get; }
    public IScriptLite Lite { get; }
    public IScriptOption Options { get; }
    public IScriptMap Map { get; }
    public IScriptMonster Monsters { get; }
    public IScriptPlayer Player { get; }
    public IScriptQuest Quests { get; }
    public IScriptSend Send { get; }
    public IScriptShop Shops { get; }
    public IScriptSkill Skills { get; }
    public IScriptWait Wait { get; }
    public IScriptServers Servers { get; }
    public IScriptHandlers Handlers { get; }
    public ICaptureProxy GameProxy { get; }
    public IScriptAccounts Accounts { get; }
    public IScriptOptionContainer? Config => Manager.Config;
    public Random Random { get; set; } = new Random();

    public ScriptInterface(
        ILogService logger,
        IScriptManager manager,
        IFlashUtil flash,
        IScriptHandlers handlers,
        IScriptServers server,
        IScriptBoost boosts,
        IScriptBotStats stats,
        IScriptSelfAuras scriptSelfAuras,
        IScriptTargetAuras scriptTargetAuras,
        IScriptCombat combat,
        IScriptDrop drops,
        IScriptEvent events,
        IScriptFaction rep,
        IScriptHouseInv house,
        IScriptInventory inventory,
        IScriptTempInv tempInv,
        IScriptBank bank,
        IScriptInventoryHelper invManager,
        IScriptLite lite,
        IScriptOption options,
        IScriptMap map,
        IScriptMonster monsters,
        IScriptPlayer player,
        IScriptQuest quests,
        IScriptSend send,
        IScriptShop shops,
        IScriptSkill skills,
        IScriptWait wait,
        IScriptKill kill,
        IScriptHunt hunt,
        ICaptureProxy gameProxy,
        IScriptAuto auto,
        IDialogService dialogService,
        ISettingsService settingsService,
        IAuraMonitorService auraMonitorService,
        IUltraBossHelper ultraBossHelper,
        IScriptAccounts accounts)
    {
        _logger = logger;
        Manager = manager;
        Boosts = boosts;
        Stats = stats;
        Self = scriptSelfAuras;
        Target = scriptTargetAuras;
        Combat = combat;
        Kill = kill;
        Hunt = hunt;
        GameProxy = gameProxy;
        Auto = auto;
        Messenger = StrongReferenceMessenger.Default;
        _dialogService = dialogService;
        Drops = drops;
        Events = events;
        Reputation = rep;
        House = house;
        Inventory = inventory;
        TempInv = tempInv;
        Bank = bank;
        InvHelper = invManager;
        Lite = lite;
        Options = options;
        Map = map;
        Monsters = monsters;
        Player = player;
        Quests = quests;
        Send = send;
        Shops = shops;
        Skills = skills;
        Wait = wait;
        Servers = server;
        Handlers = handlers;
        Flash = flash;
        AuraMonitor = auraMonitorService;
        UltraBossHelper = ultraBossHelper;
        Accounts = accounts;
        _settingsService = settingsService;

        string applicationVersion = settingsService.Get("ApplicationVersion", ClientFileSources.AssemblyVersion);
        int versionSuffix = applicationVersion.IndexOfAny(['-', '+']);
        if (versionSuffix >= 0)
            applicationVersion = applicationVersion[..versionSuffix];

        // Note (Linux / Community Scripts): Upstream community scripts (CoreBots.cs) include a Windows-only
        // version check (SkuaVersionCheckerAsync) that checks for Windows 10 and downloads .msi installers.
        // It hardcodes an exemption for 1.8.5.0 and 1.4.4.4. We report 1.8.5.0 to scripts so they immediately
        // bypass that Windows check on Linux, while the application UI displays the true ClientVersion.
        Version = new Version(1, 8, 5, 0);

        _stateChannel = new GameStateChannel(_ => { }, 100);
        _stateChannel.Start();
        if (events is ScriptEvent scriptEvent)
            scriptEvent.SetStateChannel(_stateChannel);

        _apiWrapper = new GameApiThreadSafeWrapper(this, 1);
        _apiWrapper.Start();
        Flash.FlashCall += HandleFlashCall;
        Messenger.Register<ScriptInterface, LogoutRequestedMessage, int>(this, (int)MessageChannels.GameEvents, (r, m) => r.CancelRelogin());

        ScriptInterfaceThread = new(() =>
        {
            ScriptInterfaceCTS = new();
            ScriptTimer(ScriptInterfaceCTS.Token);
            ScriptInterfaceCTS?.Dispose();
            ScriptInterfaceCTS = null;
        })
        {
            Name = "ScriptInterface",
            IsBackground = true
        };

        IScriptInterface.Instance = this;
    }

    public Task Schedule(int delay, Func<IScriptInterface, Task> function)
    {
        return Task.Run(async () => { await Task.Delay(delay); await function(this); });
    }

    public Task Schedule(int delay, Action<IScriptInterface> action)
    {
        return Task.Run(async () => { await Task.Delay(delay); action(this); });
    }

    public void SellJunk()
    {
        IJunkService? junkService = Ioc.Default.GetService<IJunkService>();
        junkService?.SellAllJunk();
    }

    public void Log(string message)
    {
        CheckScriptTermination();
        _logger.ScriptLog(message);
    }

    public void Sleep(int ms)
    {
        CheckScriptTermination();

        // For longer sleeps, break them up to check for cancellation more frequently
        if (ms > 1000)
        {
            int remaining = ms;
            while (remaining > 0)
            {
                CheckScriptTermination();
                int chunk = Math.Min(500, remaining);
                Thread.Sleep(chunk);
                remaining -= chunk;
            }
        }
        else
        {
            Thread.Sleep(ms);
        }

        CheckScriptTermination();
    }

    public async Task SleepAsync(int ms)
    {
        CheckScriptTermination();
        await Task.Delay(ms);
        CheckScriptTermination();
    }

    private void CheckScriptTermination()
    {
        if (Thread.CurrentThread.Name == "Script Thread")
        {
            if (Manager is IScriptManager scriptManager)
            {
                try
                {
                    scriptManager.ScriptPauseEvent.Wait(scriptManager.ScriptCts?.Token ?? CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (Manager.ShouldExit)
                throw new OperationCanceledException();
        }
    }

    public bool? ShowMessageBox(string message, string caption, bool yesAndNo = false)
    {
        return _dialogService.ShowMessageBox(message, caption, yesAndNo);
    }

    public DialogResult ShowMessageBox(string message, string caption, params string[] buttons)
    {
        return _dialogService.ShowMessageBox(message, caption, buttons);
    }

    public void Initialize()
    {
        if (!ScriptInterfaceThread.IsAlive)
            ScriptInterfaceThread.Start();
    }

    public async Task StopTimerAsync()
    {
        ScriptInterfaceCTS?.Cancel();
        await Wait.ForTrueAsync(() => ScriptInterfaceCTS == null, 20);
    }

    public void Stop(bool runScriptStoppingEvent = true)
    {
        _ = Manager.StopScript(runScriptStoppingEvent);
    }

    public async Task StopAsync(bool runScriptStoppingEvent = true)
    {
        await Manager.StopScript(runScriptStoppingEvent);
    }

    private void ScriptTimer(CancellationToken token)
    {
        bool catching = false;
        int lastConnChange = 0;
        string lastConnDetail = "";
        bool wasPlaying = false;
        int disconnectedSince = 0;

        Stopwatch sw = new();

        while (!token.IsCancellationRequested)
        {
            try
            {
                sw.Restart();

                bool currentInGame = Flash.IsWorldLoaded && Player.LoggedIn;
                bool currentPlaying = currentInGame && Player.Alive;

                // Track connected gameplay regardless of whether a script is running.
                _limit.LimitedRun("gameplayStats", 1000, () =>
                {
                    if (!currentInGame)
                    {
                        Stats.UpdateGameplaySnapshot(0, 0, 0, 0, 0, 0, 0, 0, false);
                        return;
                    }

                    InventoryItem? equippedClass = Inventory.Items?.Find(item => item is { Equipped: true, Category: ItemCategory.Class });
                    // iCurCP is progress within the current class rank; iCPToRank is that rank's point requirement.
                    int currentClassPoints = Flash.GetGameObject<int>("world.myAvatar.objData.iCurCP", 0);
                    int classPointsToRank = Flash.GetGameObject<int>("world.myAvatar.objData.iCPToRank", 0);
                    Stats.UpdateGameplaySnapshot(Player.XP, Player.RequiredXP, Player.Level, Player.Gold,
                        equippedClass?.ID ?? 0, currentClassPoints, classPointsToRank, Player.CurrentClassRank, true);
                });

                if (currentInGame)
                {
                    if (!wasPlaying)
                    {
                        try { Options.Reset(); } catch { }
                    }
                    wasPlaying = true;
                    disconnectedSince = 0;
                    Servers.LastIP = Player.ServerIP ?? Servers.LastIP;
                    if (Servers.IsIntentionalLogout)
                        Servers.IsIntentionalLogout = false;

                    if (!catching)
                    {
                        Flash.Call("catchPackets");
                        catching = true;
                    }

                    if (currentPlaying)
                    {
                        if (Options.RestPackets && !Player.InCombat)
                            _limit.LimitedRun("rest", 1200, () => Send.Packet("%xt%zm%restRequest%1%%"));

                        _limit.LimitedRun("opts", 250, CheckOptions);
                    }
                }
                else
                {
                    catching = false;
                    if ((wasPlaying || Manager.ScriptRunning) && !_waitForLogin && !Servers.IsIntentionalLogout && Options.AutoRelogin && WatchdogEnabled)
                    {
                        // A busy Flash makes reads fail and return false; only act if the disconnect persists.
                        if (disconnectedSince == 0)
                            disconnectedSince = Environment.TickCount;
                        else if (Environment.TickCount - disconnectedSince >= 10000)
                        {
                            wasPlaying = false;
                            disconnectedSince = 0;
                            Log("Unexpected disconnection detected by watchdog. Triggering auto re-login.");
                            BeginLogout();
                        }
                    }
                    else
                    {
                        wasPlaying = false;
                        disconnectedSince = 0;
                    }
                }

                _limit.LimitedRun("kicked", 1000, () =>
                {
                    if (Player.Kicked && !_waitForLogin && !Servers.IsIntentionalLogout && Options.AutoRelogin)
                    {
                        Log("Player kick detected. Triggering auto re-login.");
                        BeginLogout();
                    }
                });

                _limit.LimitedRun("connDetail", 100, () => (lastConnChange, lastConnDetail) = CheckStuckonLoading(lastConnChange, lastConnDetail));
                _limit.LimitedRun("loginWatchdog", 500, LoginWatchdogTick);
                UpdateCounterAttack();
                if (Manager.ScriptRunning)
                    RunScriptHandlers();

                sw.Stop();
                Thread.Sleep(Math.Max(10, _timerDelay - (int)sw.Elapsed.TotalMilliseconds));
            }
            catch (Exception e)
            {
                Trace.WriteLine($"Error in timer thread: {e.Message}");
            }
        }
    }

    // The relogin watchdog (Application Options); read only when a disconnect would trigger it. Not the
    // default-value overload: it turns a saved false into the default. Unsaved, the model default is true.
    private bool WatchdogEnabled => _settingsService.Get<bool>("ReloginWatchdog");

    private void CheckOptions()
    {
        if (Options.LagKiller)
            Flash.Call("killLag", true);

        if (!Player.Playing)
            return;

        if (Options.Magnetise)
            Flash.Call("magnetize");
        if (Options.InfiniteRange)
            Flash.Call("infiniteRange");
        if (Options.AggroMonsters)
            Flash.CallGameFunction("world.aggroAllMon");
        if (Options.AggroAllMonsters)
            Send.Packet($"%xt%zm%aggroMon%{Map.RoomID}%{string.Join("%", Monsters.MapMonsters.Select(m => m.MapID))}%");
        if (Options.SkipCutscenes)
        {
            Flash.Call("skipCutscenes");
            // A cutscene hides the world while it plays, and removing it early leaves the world hidden, which looks just like
            // Lag Killer being on. Only Lag Killer may hide it.
            try
            {
                if (!Options.LagKiller && !Flash.GetGameObject<bool>("world.visible", true))
                    Flash.SetGameObject("world.visible", true);
            }
            catch { }
        }
        if (Options.WalkSpeed != 8)
            Player.WalkSpeed = Options.WalkSpeed;
        if (!Lite.UntargetSelf)
            Lite.UntargetSelf = true;
        if (!Lite.UntargetDead)
            Lite.UntargetDead = true;
    }

    /// <summary>
    /// Checks if the player is stuck in the loading screen.
    /// </summary>
    /// <param name="lastConnChange">Last time the loading message changed.</param>
    /// <param name="lastConnDetail">Last loading message.</param>
    /// <returns>The last loading message and its time</returns>
    private int _lastConnLostLog = Environment.TickCount - 60000;

    private (int newTime, string newText) CheckStuckonLoading(int lastConnChange, string lastConnDetail)
    {
        string connDetail = Flash.IsNull("mcConnDetail.stage") ? "null" : Flash.GetGameObject("mcConnDetail.txtDetail.text", "null")!;
        // After a relogin the overlay can stay on stage with its last text while the player is fully in game;
        // acting on it would log a healthy account out (without re-login) once LoadTimeout passes.
        if (connDetail != "null" && Servers.IsConnected && Flash.IsWorldLoaded && !Map.Loading && Player.Loaded)
            connDetail = "null";
        if (connDetail == "null")
            return (Environment.TickCount, connDetail);
        if (connDetail.Contains("has been lost") && !_waitForLogin && !Servers.IsConnected)
        {
            // Checked 10 times a second: with auto relogin off this used to log (and try) every time.
            if (!Servers.IsIntentionalLogout && Environment.TickCount - _lastConnLostLog > 15000)
            {
                _lastConnLostLog = Environment.TickCount;
                Log("Connection lost detected. Triggering auto re-login.");
                BeginLogout();
            }
        }
        else if (Environment.TickCount - lastConnChange >= Options.LoadTimeout && connDetail == lastConnDetail && !_waitForLogin)
        {
            if (connDetail.Contains("loading map"))
            {
                Map.Join("battleon");
                Map.Reload();
                Handlers.RegisterOnce(500, b =>
                {
                    if (Flash.GetGameObject("mcConnDetail.txtDetail.text") == "loading map")
                    {
                        if (Options.AutoRelogin)
                            BeginLogout();
                        else
                            Servers.DisconnectForRelogin();
                        return;
                    }
                    Map.Join(Map.LastMap);
                });
            }
            else
            {
                if (Options.AutoRelogin)
                    BeginLogout();
                else
                    Servers.DisconnectForRelogin();
            }
        }
        if (connDetail == lastConnDetail)
            return (lastConnChange, connDetail);
        return (Environment.TickCount, connDetail);
    }

    /// <summary>
    /// Run all registered handlers, if the handler returns <see langword="false"/> it is removed from the list.
    /// </summary>
    private void RunScriptHandlers()
    {
        if (Handlers.CurrentHandlers.ToList().Count == 0)
            return;
        List<IHandler> rem = [];
        foreach (IHandler handler in Handlers.CurrentHandlers.ToList())
        {
            _limit.LimitedRun("handler_" + handler.Name, handler.Ticks * _timerDelay, () =>
            {
                if (!handler.Function(this))
                    rem.Add(handler);
            });
        }
        Handlers.Remove(rem);
    }

    private void HandleFlashCall(string name, object[]? args)
    {
        switch (name)
        {
            case "pre-load":
                Schedule(1000, _ =>
                {
                    try
                    {
                        string sBG = _settingsService.Get("sBG", "Generic2.swf");
                        string? customBackgroundPath = _settingsService.Get<string?>("CustomBackgroundPath", null);
                        Flash.Call("setBackgroundValues", sBG, customBackgroundPath ?? "");
                    }
                    catch (Exception ex)
                    {
                        Log($"Could not set background values: {ex.Message}");
                    }
                });
                break;

            case "loaded":
                try
                {
                    if (Options.IsHeadlessNow())
                    {
                        Flash.SetGameObject("stage.frameRate", 2);
                        Flash.SetGameObject("stage.quality", "LOW");
                        Flash.SetGameObject("world.rootClass.mixer.soundTransform.volume", 0);
                    }
                    else
                    {
                        if (Options.SetFPS > 2)
                            Flash.SetGameObject("stage.frameRate", Options.SetFPS);

                        Options.ApplyStageQuality();
                    }
                }
                catch { }
                OnGameLoaded();
                Initialize();
                try { Options.Reset(); } catch { }
                try
                {
                    if (Options.IsHeadlessNow())
                    {
                        Flash.SetGameObject("stage.frameRate", 2);
                        Flash.SetGameObject("stage.quality", "LOW");
                        Flash.SetGameObject("world.rootClass.mixer.soundTransform.volume", 0);
                    }
                    else
                    {
                        Options.ApplyStageQuality();
                    }
                }
                catch { }
                break;

            case "connectionLost":
                Log("Flash socket connection lost event received.");
                if (!_waitForLogin && !Servers.IsIntentionalLogout && Options.AutoRelogin && WatchdogEnabled)
                {
                    BeginLogout();
                }
                break;

            case "userIntentionalAction":
                if (args is { Length: > 0 } && args[0] is string action)
                {
                    switch (action)
                    {
                        case "logout":
                            Log("Intentional logout button clicked by user in game. Auto re-login disarmed.");
                            Servers.IsIntentionalLogout = true;
                            Servers.UserLoggedOut = true;
                            CancelRelogin();
                            if (Manager.ScriptRunning)
                                _ = Manager.StopScript();
                            break;

                        case "changeServer":
                            Log("Intentional 'Change Server' button clicked by user in game. Auto re-login disarmed.");
                            Servers.IsIntentionalLogout = true;
                            Servers.UserLoggedOut = true;
                            CancelRelogin();
                            if (Manager.ScriptRunning)
                                _ = Manager.StopScript();
                            break;

                        case "selectServer":
                            Log("Server chosen by user on server list. Watchdog armed.");
                            Servers.IsIntentionalLogout = false;
                            break;
                    }
                }
                break;

            case "openWebsite":
                if (args is { Length: > 0 } && args[0] is string url)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = url,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to open website: {url}. Error: {ex.Message}");
                    }
                }
                break;

            case "debug":
                Trace.WriteLine(args[0]);
                break;

            case "pext":
                if (((string)args[0]).Contains("Missing Quest Progress", StringComparison.OrdinalIgnoreCase))
                    OnMissingQuestProgress();
                if (((string)args[0]).Contains("One Time Quest Only", StringComparison.OrdinalIgnoreCase))
                    OnOneTimeQuestOnly();
                if (((string)args[0]).Contains("Already have a full stack", StringComparison.OrdinalIgnoreCase))
                    Drops.RejectFullStackDrops();
                dynamic packet = JsonConvert.DeserializeObject<dynamic>((string)args[0])!;
                string? pextCmd = null;
                try
                {
                    string type = packet["params"].type;
                    dynamic data = packet["params"].dataObj;
                    if (type is not null and "json")
                    {
                        string cmd = data.cmd;
                        pextCmd = cmd;
                        switch (cmd)
                        {
                            case "event":
                                string zone = data.args?["zoneSet"]!;
                                if (zone is not null)
                                    Messenger.Send<RunToAreaMessage, int>(new(zone), (int)MessageChannels.GameEvents);
                                break;

                            case "moveToArea":
                                Stats.GetSpace();
                                Messenger.Send<MapChangedMessage, int>(new(Convert.ToString(data.strMapName)), (int)MessageChannels.GameEvents);
                                Map.FilePath = Convert.ToString(data.strMapFileName);
                                Map.LastMap = Convert.ToString(data.strMapName);
                                break;

                            case "ct":
                                dynamic p = data.p?[Player.Username.ToLower()]!;
                                if (p is not null && p.intHP == 0)
                                {
                                    // Combat-state packets can repeat while the player remains dead.
                                    if (!_playerDead)
                                    {
                                        Stats.Deaths++;
                                        Messenger.Send<PlayerDeathMessage, int>((int)MessageChannels.GameEvents);
                                    }

                                    _playerDead = true;
                                    break;
                                }

                                if (p is not null)
                                    _playerDead = false;

                                if (data is JObject ctData)
                                    HandleCounterAttack(ctData);
                                break;

                        case "sellItem":
                            Stats.GetSpace();
                            Messenger.Send<ItemSoldMessage, int>(new(
                                Convert.ToInt32(data.CharItemID),
                                Convert.ToInt32(data.iQty),
                                Convert.ToInt32(data.iQtyNow),
                                Convert.ToInt32(data.intAmount),
                                Convert.ToInt32(data.bCoins) == 1),
                                (int)MessageChannels.GameEvents);
                            break;

                        case "buyItem":
                            if (data.bitSuccess == 1)
                            {
                                Messenger.Send<ItemBoughtMessage, int>(new(Convert.ToInt32(data.CharItemID)), (int)MessageChannels.GameEvents);
                                Stats.GetSpace();
                            }
                            break;

                        case "dropItem":
                            string items = Convert.ToString(data["items"]);
                            InventoryItem drop = JsonConvert.DeserializeObject<Dictionary<string, InventoryItem>>(items)!.First().Value;
                            Messenger.Send<ItemDroppedMessage, int>(new(drop), (int)MessageChannels.GameEvents);
                            break;

                        case "addItems":
                            string addItems = Convert.ToString(data["items"]);
                            Dictionary<int, dynamic> addedItem = JsonConvert.DeserializeObject<Dictionary<int, dynamic>>(addItems)!;
                            int itemID = addedItem.Keys.First()!;
                            ItemBase? invItem = Inventory.GetItem(itemID) ?? TempInv.GetItem(itemID);
                            Stats.GetSpace();
                            if (invItem is null)
                            {
                                invItem = Bank.GetItem(itemID);
                                if (invItem is not null)
                                    Messenger.Send<ItemAddedToBankMessage, int>(new(invItem, invItem.Quantity), (int)MessageChannels.GameEvents);
                                break;
                            }
                            if (!invItem.Temp)
                                Stats.Drops++;
                            Messenger.Send<ItemDroppedMessage, int>(new(invItem, true, Convert.ToInt32(addedItem.Values.First().iQtyNow)), (int)MessageChannels.GameEvents);
                            break;

                        case "getDrop":
                            bool toBank = Convert.ToBoolean(data.bBank);
                            Messenger.Send<DropPickupResultMessage, int>(new(Convert.ToInt32(data.ItemID), data.bSuccess == 1), (int)MessageChannels.GameEvents);
                            if (data.bSuccess == 1)
                            {
                                Stats.Drops += (int)data.iQty;
                                Stats.GetSpace();
                            }
                            if (toBank)
                            {
                                ItemBase bankItem = Bank.GetItem(Convert.ToInt32(data.ItemID))!;
                                Messenger.Send<ItemAddedToBankMessage, int>(new(bankItem, Convert.ToInt32(data.iQtyNow)), (int)MessageChannels.GameEvents);
                            }
                            break;

                        case "addGoldExp":
                            if (data.typ == "m")
                            {
                                Stats.Kills++;
                                Messenger.Send<MonsterKilledMessage, int>(new(Convert.ToInt32(data.id)), (int)MessageChannels.GameEvents);
                            }
                            break;

                        case "ccqr":
                            if (data.bSuccess == 1)
                            {
                                Stats.QuestsCompleted++;
                                Messenger.Send<QuestTurninMessage, int>(new(Convert.ToInt32(data.QuestID)), (int)MessageChannels.GameEvents);
                            }
                            else
                            {
                                try { OnQuestTurnInFailed(Convert.ToInt32(data.QuestID)); } catch { }
                            }
                            break;

                        case "loadBank":
                            Stats.GetSpace();
                            Messenger.Send<BankLoadedMessage, int>((int)MessageChannels.GameEvents);
                            break;

                        case "loadShop":
                            Stats.GetSpace();
                            Messenger.Send<ShopLoadedMessage, int>(new(new(Shops.ID, Shops.Name, Shops.Items)), (int)MessageChannels.GameEvents);
                            break;
                    }
                }
                else if (type is not null and "str")
                {
                    string cmd = data[0];
                    switch (cmd)
                    {
                        case "popup":
                            string b = Convert.ToString(packet);
                            Debug.WriteLine(b);
                            break;

                        case "uotls":
                            if (Player.Username == (string)data[2] && data[3] == "afk:true")
                                Messenger.Send<PlayerAFKMessage, int>((int)MessageChannels.GameEvents);
                            break;

                        case "loginResponse":
                            _playerDead = false;
                            if (Flash.IsWorldLoaded)
                                Stats.GetSpace();
                            Options.CustomName = string.Empty;
                            Options.CustomGuild = string.Empty;
                            Servers.IsIntentionalLogout = false;
                            _waitForLogin = false;
                            Messenger.Send<LoginMessage, int>(new(Convert.ToString(data[4])), (int)MessageChannels.GameEvents);
                            break;
                    }
                }
                }
                catch (Exception ex)
                {
                    _logger.DebugLog($"Error handling extension packet '{pextCmd}': {ex.Message}\n{ex.StackTrace}");
                }
                try
                {
                    Messenger.Send<ExtensionPacketMessage, int>(new(packet), (int)MessageChannels.GameEvents);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ExtensionPacket] Script handler exception: {ex.Message}");
                }
                break;

            case "packetFromServer":
                if (args?.Length > 0 && args[0] is not null && ((string)args[0]).Contains("Already have a full stack", StringComparison.OrdinalIgnoreCase))
                    Drops.RejectFullStackDrops();
                break;

            case "packet":
                if (((string)args[0]).Contains("Already have a full stack", StringComparison.OrdinalIgnoreCase))
                    Drops.RejectFullStackDrops();
                try
                {
                    string[] parts = ((string)args[0]).Split('%', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3)
                        break;
                    switch (parts[2])
                    {
                        case "eLO":
                        case "eLO2":
                            Log("In-game exit logout packet (eLO) detected. Disarming auto re-login.");
                            Servers.IsIntentionalLogout = true;
                            Servers.UserLoggedOut = true;
                            _wdRestartScript = false;
                            CancelRelogin();
                            if (Manager.ScriptRunning)
                                _ = Manager.StopScript();
                            break;

                        case "eSL":
                        case "eSL2":
                            Log("In-game exit to server list packet (eSL) detected. Disarming auto re-login.");
                            Servers.IsIntentionalLogout = true;
                            Servers.UserLoggedOut = true;
                            _wdRestartScript = false;
                            CancelRelogin();
                            if (Manager.ScriptRunning)
                                _ = Manager.StopScript();
                            break;

                        case "moveToCell":
                            if (parts.Length >= 6)
                                Messenger.Send<CellChangedMessage, int>(new(Map.Name, parts[4], parts[5]), (int)MessageChannels.GameEvents);
                            break;

                        case "buyItem":
                            if (parts.Length >= 7 && int.TryParse(parts[5], out int sId) && int.TryParse(parts[4], out int iId) && int.TryParse(parts[6], out int sItemId))
                            {
                                Stats.GetSpace();
                                Messenger.Send<TryBuyItemMessage, int>(new(sId, iId, sItemId), (int)MessageChannels.GameEvents);
                            }
                            break;

                        case "acceptQuest":
                            if (parts.Length >= 5 && int.TryParse(parts[4], out int qId))
                            {
                                Stats.QuestsAccepted++;
                                Messenger.Send<QuestAcceptedMessage, int>(new(qId), (int)MessageChannels.GameEvents);
                            }
                            break;

                        case "cmd":
                            if (parts.Length >= 5 && parts[4] == "logout")
                            {
                                if (_waitForLogin)
                                {
                                    break;
                                }

                                Messenger.Send<LogoutMessage, int>((int)MessageChannels.GameEvents);
                                if (Servers.IsIntentionalLogout)
                                {
                                    Log("Intentional logout confirmed. Auto re-login skipped.");
                                    CancelRelogin();
                                }
                                else
                                {
                                    Log($"Server logout command detected (Kicked: {Player.Kicked}). Triggering auto re-login.");
                                    BeginLogout(logoutNotified: true);
                                }
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.DebugLog($"Error handling packet: {ex.Message}");
                }
                Messenger.Send<PacketMessage, int>(new((string)args[0]), (int)MessageChannels.GameEvents);
                break;
        }
    }

    private const string CounterAttackWarning = "prepares a counter attack";
    private const string CounterAttackAnimation = "Reflect";
    private const string CounterAttackAuraName = "Counter Attack";

    private const int CounterAttackWarningSeconds = 10;
    private const int CounterAttackMarginSeconds = 1;

    // A counter in progress, per monster ("m:<MapID>"): its counter aura (null until the aura arrives) and when the
    // counter is assumed over if no removal packet comes.
    private sealed record CounterAttackState(string? Aura, DateTime Expires);

    private readonly Dictionary<string, CounterAttackState> _counterAttacks = new();
    private readonly object _counterAttackLock = new();
    // Whether the last CounterAttackMessage said a counter is on. Only the script timer thread touches it.
    private bool _counterAttackSent;

    /// <summary>
    /// Tracks monsters' counter attacks from a <c>ct</c> packet. A warning message starts one. Its aura is the aura the
    /// monster puts on itself in the same packet as its <c>Reflect</c> animation (or one named "Counter Attack"; the
    /// shortest if there are several), so the name doesn't have to be known. A counter ends when that aura is removed
    /// from that monster, when the monster dies, or when it expires. Messages are sent by
    /// <see cref="UpdateCounterAttack"/>.
    /// </summary>
    private void HandleCounterAttack(JObject data)
    {
        lock (_counterAttackLock)
        {
            DateTime now = DateTime.Now;
            HashSet<string> reflecting = new();
            if (data["anims"] is JArray anims)
            {
                foreach (JToken anim in anims)
                {
                    string? caster = (string?)anim["cInf"];
                    if (caster is null || !caster.StartsWith("m:"))
                        continue;

                    string? animStr = (string?)anim["animStr"];
                    if (animStr is not null && animStr.Split(',').Contains(CounterAttackAnimation))
                        reflecting.Add(caster);

                    string? msg = (string?)anim["msg"];
                    if (msg is null || !msg.Contains(CounterAttackWarning, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // A new warning sets a fallback expiry until the aura arrives.
                    DateTime expires = now.AddSeconds(CounterAttackWarningSeconds);
                    if (!_counterAttacks.TryGetValue(caster, out CounterAttackState? prior) || prior.Expires < expires)
                        _counterAttacks[caster] = new(prior?.Aura, expires);
                }
            }

            // A dead monster can't counter.
            if (data["m"] is JObject monsters)
            {
                foreach (JProperty monster in monsters.Properties())
                {
                    if ((int?)monster.Value["intState"] == 0)
                        _counterAttacks.Remove($"m:{monster.Name}");
                }
            }

            if (data["a"] is JArray actions)
            {
                Dictionary<string, (string Name, float Duration)> counterAuras = new();
                foreach (JToken action in actions)
                {
                    string? cmd = (string?)action["cmd"];
                    string? target = (string?)action["tInf"];
                    if (cmd is null || target is null || !target.StartsWith("m:"))
                        continue;

                    switch (cmd)
                    {
                        case "aura+":
                        case "aura++":
                            if ((string?)action["cInf"] != target || action["auras"] is not JArray auras)
                                break;
                            foreach (JToken aura in auras)
                            {
                                string? name = (string?)aura["nam"];
                                if (name is null || (!reflecting.Contains(target) && name != CounterAttackAuraName))
                                    continue;

                                // Counter auras are short (3-6 s); a longer one in the same packet (e.g. "Skill
                                // Locked", 45 s) is something else the monster cast.
                                float duration = (float?)aura["dur"] ?? 0;
                                if (!counterAuras.TryGetValue(target, out (string Name, float Duration) best)
                                    || (duration > 0 && (best.Duration <= 0 || duration < best.Duration)))
                                    counterAuras[target] = (name, duration);
                            }
                            break;

                        case "aura-":
                        case "aura--":
                            if (!_counterAttacks.TryGetValue(target, out CounterAttackState? counter) || counter.Aura is null)
                                break;
                            IEnumerable<JToken> removed = action["auras"] is JArray list ? list : new[] { action["aura"] }.OfType<JToken>();
                            if (removed.Any(a => (string?)a["nam"] == counter.Aura))
                                _counterAttacks.Remove(target);
                            break;
                    }
                }

                foreach ((string monster, (string name, float duration)) in counterAuras)
                {
                    double seconds = (duration > 0 ? duration : CounterAttackWarningSeconds) + CounterAttackMarginSeconds;
                    _counterAttacks[monster] = new(name, now.AddSeconds(seconds));
                }
            }
        }
    }

    /// <summary>
    /// Sends <see cref="CounterAttackMessage"/> when the first counter starts and when the last one ends, including
    /// counters that expire because their removal packet never came (lost, the monster left, or a warning with no
    /// counter after it). Runs on the script timer thread only, so start and end messages can't arrive out of order.
    /// </summary>
    private void UpdateCounterAttack()
    {
        bool active;
        lock (_counterAttackLock)
        {
            DateTime now = DateTime.Now;
            foreach (string monster in _counterAttacks.Where(c => c.Value.Expires <= now).Select(c => c.Key).ToList())
                _counterAttacks.Remove(monster);
            active = _counterAttacks.Count > 0;
        }

        if (active == _counterAttackSent)
            return;
        _counterAttackSent = active;
        Messenger.Send<CounterAttackMessage, int>(new(!active), (int)MessageChannels.GameEvents);
    }

    private Task? _reloginTask;
    private volatile bool _waitForLogin;
    private CancellationTokenSource? _reloginCTS;

    public void CancelRelogin()
    {
        if (_reloginCTS is not null)
        {
            try
            {
                _reloginCTS.Cancel();
            }
            catch { }
            _reloginCTS = null;
        }
        _reloginTask = null;
        _waitForLogin = false;
    }

    private async Task OnLogout(bool logoutNotified)
    {
        if (!Options.AutoRelogin || _waitForLogin || Servers.IsIntentionalLogout)
            return;

        if (_reloginTask is { IsCompleted: false })
        {
            Log("Re-login task already running.");
            return;
        }

        Log("Auto re-login triggered.");
        bool wasRunning = Manager.ScriptRunning;
        WdLog($" auto re-login started; script running now: {wasRunning}, running recently: {ScriptRanRecently}.");
        CancellationTokenSource reloginCts = new();
        _reloginCTS = reloginCts;
        if (wasRunning || _wdScriptWhenInGame || ScriptRanRecently)
        {
            _wdRestartScript = true;
            _wdReloginMarker = Environment.TickCount | 1;
            _wdFallbackSince = 0;
        }
        // Claim the re-login before awaiting the script stop, so the crash recovery doesn't start a
        // second login meanwhile.
        _waitForLogin = true;
        try
        {
            if (!logoutNotified)
                Messenger.Send<LogoutMessage, int>((int)MessageChannels.GameEvents);
            if (wasRunning)
                await Manager.StopScript();
            reloginCts.Token.ThrowIfCancellationRequested();
            bool kicked = Player.Kicked;
            Messenger.Send<ReloginTriggeredMessage, int>(new(kicked), (int)MessageChannels.GameEvents);

            int delay = Options.ReloginTryDelay;
            if (Options.SafeRelogin)
            {
                delay = kicked ? 20000 : Math.Max(10000, Options.ReloginTryDelay);
            }
            else
            {
                delay = Math.Max(800, Options.ReloginTryDelay);
            }
            Relogin(delay, false, reloginCts);
        }
        catch
        {
            if (ReferenceEquals(_reloginCTS, reloginCts))
            {
                _reloginCTS = null;
                _waitForLogin = false;
            }
            reloginCts.Dispose();
            throw;
        }
    }

    private async void BeginLogout(bool logoutNotified = false)
    {
        try
        {
            await OnLogout(logoutNotified);
        }
        catch (OperationCanceledException)
        {
            Log("Auto re-login cancelled.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Auto re-login failed: {ex}");
            Log($"Auto re-login failed: {ex.Message}");
        }
    }

    private void Relogin(int delay, bool startScript, CancellationTokenSource reloginCts)
    {
        reloginCts.Token.ThrowIfCancellationRequested();
        _reloginStartsScript = startScript;
        Servers.DisconnectForRelogin();
        Log($"Waiting {delay}ms for re-login.");
        DogNotice.Working(Player.Kicked ? "Kicked from the game: logging back in" : "Logged out: logging back in", (int)Math.Ceiling(delay / 1000.0));
        _reloginTask = Schedule(delay, async _ =>
        {
            try
            {
                reloginCts.Token.ThrowIfCancellationRequested();
                Stats.Relogins++;
                bool relogged = await Servers.EnsureRelogin(reloginCts.Token);
                reloginCts.Token.ThrowIfCancellationRequested();
                if (startScript && relogged)
                    await Ioc.Default.GetService<IScriptManager>()!.StartScript();
                else if (startScript && !relogged)
                    Log("Skipping script restart because re-login did not succeed.");
                Log($"Re-login was {(relogged ? "successful" : "cancelled or unsuccessful")}.");
                if (relogged)
                    DogNotice.Done("Back in game");
            }
            catch (OperationCanceledException) when (reloginCts.IsCancellationRequested)
            {
                Log("Auto re-login cancelled.");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Auto re-login task failed: {ex}");
                Log($"Auto re-login failed: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_reloginCTS, reloginCts))
                {
                    _reloginCTS = null;
                    _reloginTask = null;
                    _waitForLogin = false;
                }

                reloginCts.Dispose();
            }
        });
    }

    private bool _disposed = false;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // Unsubscribe from Flash events
                Flash.FlashCall -= HandleFlashCall;
                Messenger.Unregister<LogoutRequestedMessage, int>(this, (int)MessageChannels.GameEvents);

                // Cancel and clean up the script interface thread
                ScriptInterfaceCTS?.Cancel();
                if (ScriptInterfaceThread is { IsAlive: true })
                {
                    // Give the thread time to finish gracefully
                    if (!ScriptInterfaceThread.Join(1000))
                    {
                        // Force abort if it doesn't finish in time
                        try { ScriptInterfaceThread.Interrupt(); } catch {/* ignored */ }
                    }
                }
                ScriptInterfaceCTS?.Dispose();
                ScriptInterfaceCTS = null;
                _apiWrapper?.StopAsync().GetAwaiter().GetResult();
                _apiWrapper = null;

                if (_stateChannel != null)
                {
                    _stateChannel.StopAsync().GetAwaiter().GetResult();
                    _stateChannel.Dispose();
                    _stateChannel = null;
                }

                _reloginCTS?.Cancel();
                _reloginCTS?.Dispose();
                _reloginCTS = null;
                _reloginTask = null;

                // Clear the static instance reference
                if (IScriptInterface.Instance == this)
                {
                    IScriptInterface.Instance = null;
                }
            }

            _disposed = true;
        }
    }

    ~ScriptInterface()
    {
        Dispose(false);
    }
}
