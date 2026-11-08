using Skua.Core.Interfaces;
using Skua.Core.Services;

namespace Skua.Core.Scripts;

/// <summary>
/// Login watchdog: notices when the game sits on one connection message ("Authenticating Account Info...",
/// "Connecting to game server...", "Loading Character Data...") for too long and gets it moving again.
/// </summary>
/// <remarks>
/// Each stall in a row escalates: 1) reset the login screen and log in again, 2+) restart the whole game
/// player and log in again (Ruffle only; with Flash it resets the login screen again).
/// The server that got stuck is avoided for a few minutes. After six stalls in a row it waits a minute
/// between tries, after twelve ten minutes, so it doesn't hammer AQW while AQW is down, but it never gives up.
/// It also logs back in when an account has been out of the game for 3 minutes without anyone asking for
/// that (auto relogin gave up, a failed start, a kick with auto relogin off). Getting into the game resets
/// the count.
/// </remarks>
public partial class ScriptInterface
{
    private const int WatchdogStallMs = 20000;
    private const int WatchdogBackoffAfter = 6;
    private const int WatchdogBackoffMs = 60000;
    private const int WatchdogSlowAfter = 12;
    private const int WatchdogSlowMs = 600000;
    private const int WatchdogOutOfGameMs = 180000;
    private int _wdOutSince;
    private int _wdWaitSince;          // when a re-login started waiting (blank screen, nothing moving)
    private const int WatchdogLoginTimeoutMs = 300000;
    private volatile bool _wdWasInGame;
    private volatile bool _wdReturnToRoom;
    private string? _wdLastRoom, _wdLastCell, _wdLastPad;
    private int _wdServerCheck;

    private string _wdText = string.Empty;
    private int _wdSince = Environment.TickCount;
    private int _wdStalls;
    private int _wdLastAction;
    private volatile bool _wdBusy;
    private volatile bool _wdGameLoaded;
    private bool _reloginStartsScript;

    // Crash recovery: when the game player restarts by itself (a crash) while the account was in game,
    // log back in. _wdOwnRestart marks restarts the watchdog made itself (it logs in on its own).
    private int _wdLastInGame;
    private bool _wdScriptWhenInGame;
    private bool _wdCredsKept;
    private volatile bool _wdOwnRestart;

    // Set when the account got knocked out of the game while a script was running (crash, stuck login,
    // disconnect). Whichever way it gets back in, the script is started again once it is in game.
    private volatile bool _wdRestartScript;

    // Safety net for VibeSkua's own auto re-login: set when an auto re-login begins while a script was (or had just
    // been) running. If the re-login works but nothing started the script again, the watchdog starts it once the
    // account has been steady in game for a while (see LoginWatchdogTick).
    private int _wdReloginMarker;
    private int _wdFallbackSince;
    private const int WatchdogFallbackSteadyMs = 30000;
    private const int WatchdogFallbackWindowMs = 1200000;

    /// <inheritdoc cref="IScriptInterface.RecoveryPending"/>
    public bool RecoveryPending => _waitForLogin || _wdBusy || _wdRestartScript || _wdRestarting != 0 || RescueActive;

    // When a script was last seen running. A script often stops a moment before the game is seen as gone
    // (the player was killed, calls start failing), so "was a script running when it left the game" looks
    // back a little instead of only at that instant.
    private int _wdScriptSeenAt;
    private bool ScriptRanRecently => Manager.ScriptRunning || (_wdScriptSeenAt != 0 && Environment.TickCount - _wdScriptSeenAt < 45000);
    private int _wdRestarting;
    private int _wdStoppedInGameAt;

    /// <summary>
    /// Keeps <see cref="_wdScriptSeenAt"/> up to date. A script that stopped while the game carried on normally
    /// for a few seconds afterwards was stopped on purpose or finished: it isn't started again later.
    /// </summary>
    private void TrackScriptStops()
    {
        int now = Environment.TickCount;
        if (Manager.ScriptRunning)
        {
            _wdScriptSeenAt = now | 1;
            _wdStoppedInGameAt = 0;
            return;
        }
        if (_wdScriptSeenAt == 0)
            return;
        bool inGame = !_waitForLogin && !_wdBusy && Player.Playing && Flash.IsWorldLoaded;
        if (!inGame)
        {
            _wdStoppedInGameAt = 0;
            return;
        }
        if (_wdStoppedInGameAt == 0)
            _wdStoppedInGameAt = now | 1;
        else if (now - _wdStoppedInGameAt > 5000)
        {
            _wdScriptSeenAt = 0;
            _wdStoppedInGameAt = 0;
        }
    }

    // "Communication with server has been lost" left on screen (auto relogin off, or it gave up).
    private int _wdLostSince;
    private const int WatchdogLostMs = 60000;

    /// <summary>Called from the script timer about twice a second.</summary>
    private void LoginWatchdogTick()
    {
        TrackScriptStops();
        if (_wdBusy)
            return;

        if (Player.Playing && Flash.IsWorldLoaded)
        {
            // Whatever the notice said, the character is in game now.
            if (DogNotice.IsWorking)
                DogNotice.Done("Back in game");
            if (_wdStalls > 0)
                WdLog($" in game again after {_wdStalls} recover{(_wdStalls == 1 ? "y" : "ies")}.");
            if (_wdStalls >= WatchdogBackoffAfter)
                Alert("back in game", $"Back in game after {_wdStalls} recoveries in a row.", VibeAlerts.Level.Info);
            _wdStalls = 0;
            Servers.CurrentLoginServer = null;
            _wdLastInGame = Environment.TickCount | 1;
            _wdScriptWhenInGame = ScriptRanRecently;
            _wdWasInGame = true;
            _wdLostSince = 0;
            // Timers for "out of game" start fresh every time it gets back in (a leftover start time made the
            // 3-minute check fire the moment the next restart began).
            _wdOutSince = 0;
            _wdWaitSince = 0;
            CheckStuckScript();
            RescueTick();
            RestartAfterSilentRelogin();
            // Back from a recovery with no script to restart: go back to the room it was in (a re-login
            // lands in a random Battleon room, and ButlerV5 followers can't follow into a full one).
            if (_wdReturnToRoom && !_waitForLogin)
            {
                _wdReturnToRoom = false;
                if (!_wdRestartScript && !Manager.ScriptRunning && _wdLastRoom is { } room)
                    _ = Task.Run(() => ReturnToRoom(room, _wdLastCell, _wdLastPad));
            }
            // Remember the server and room, so automatic re-logins go back to them.
            if (Environment.TickCount - _wdServerCheck > 10000 && !_wdReturnToRoom)
            {
                _wdServerCheck = Environment.TickCount;
                try
                {
                    string room = Map.FullName;
                    if (!string.IsNullOrWhiteSpace(room) && room.Contains('-'))
                    {
                        _wdLastRoom = room;
                        _wdLastCell = Player.Cell;
                        _wdLastPad = Player.Pad;
                    }
                }
                catch { }
                try
                {
                    string? name = Flash.GetGameObject("objServerInfo.sName")?.Trim().Trim('"').Trim();
                    if (!string.IsNullOrEmpty(name) && name != "null")
                        Servers.PreferredReloginServer = name;
                }
                catch { }
            }
            if (!_wdCredsKept)
                _wdCredsKept = Servers.RememberLoginInfo();
            // The flag stays set until the script is really running again (RestartScriptAfterLogin clears it).
            if (_wdRestartScript && !_waitForLogin && _wdRestarting == 0 && !RescueActive)
                _ = Task.Run(RestartScriptAfterLogin);
            ResetWatchdogTimer(string.Empty);
            return;
        }

        // AQW reports a dead character as not Playing even though it remains logged in and may still be in the same
        // loaded map. Keep an active rescue request alive across that temporary state.
        if (MaintainRescueCallDuringDeath(Environment.TickCount))
            return;

        // Just left the game. A script that was running then is started again once it's back in, however it
        // gets back (crash, freeze, memory restart, disconnect, stuck login); unless someone logged out on purpose.
        if (_wdWasInGame)
        {
            _wdWasInGame = false;
            Interlocked.Exchange(ref _rsManualRequest, 0);
            if (_rsCalling || _rsHoldingHelpers)
            {
                _rsCalling = false;
                _rsHoldingHelpers = false;
                ClearRescueCall();
            }
            if (_wdScriptWhenInGame || ScriptRanRecently)
                _wdRestartScript = true;
            WdLog($" left the game; script running when last in game: {_wdScriptWhenInGame}, running recently: {ScriptRanRecently}, will restart it: {_wdRestartScript && !Servers.UserLoggedOut}.");
        }
        if (Servers.UserLoggedOut)
            _wdRestartScript = false;

        string text = Flash.IsNull("mcConnDetail.stage") ? string.Empty : Flash.GetGameObject("mcConnDetail.txtDetail.text", string.Empty) ?? string.Empty;
        text = text.Trim();

        // A re-login that sits on a blank screen for 5 minutes (nothing on screen for the stuck-login check to
        // see): take over. Seen overnight: an account sat logged out for 75 minutes like this.
        if (_waitForLogin && text.Length == 0)
        {
            if (_wdWaitSince == 0)
                _wdWaitSince = Environment.TickCount | 1;
            else if (Environment.TickCount - _wdWaitSince >= WatchdogLoginTimeoutMs && BackoffAllows())
            {
                _wdWaitSince = 0;
                StartRecovery($" logging back in has been going for {WatchdogLoginTimeoutMs / 60000} minutes with nothing happening; starting over");
                return;
            }
        }
        else
        {
            _wdWaitSince = 0;
        }

        // Out of the game with nothing happening: log back in after a while, unless someone logged out on
        // purpose or a re-login is already under way.
        if (text.Length == 0 && !_waitForLogin && !Servers.UserLoggedOut && Servers.RememberLoginInfo())
        {
            if (_wdOutSince == 0)
                _wdOutSince = Environment.TickCount | 1;
            else if (Environment.TickCount - _wdOutSince >= WatchdogOutOfGameMs && BackoffAllows())
            {
                _wdOutSince = 0;
                StartRecovery($" logged out with nothing happening for {WatchdogOutOfGameMs / 60000} minutes; logging back in");
                return;
            }
        }
        else
        {
            _wdOutSince = 0;
        }

        // "Communication with server has been lost" normally starts auto relogin. Seen after a wifi restart:
        // one account sat on it for good (auto relogin off, or it gave up). After a minute, log back in.
        if (text.Contains("has been lost", StringComparison.OrdinalIgnoreCase))
        {
            if (_wdLostSince == 0)
                _wdLostSince = Environment.TickCount | 1;
            else if (Environment.TickCount - _wdLostSince >= WatchdogLostMs && !Servers.UserLoggedOut && BackoffAllows())
            {
                _wdLostSince = 0;
                StartRecovery($" stuck on \"{text}\" for {WatchdogLostMs / 1000}s; logging back in");
                return;
            }
        }
        else
        {
            _wdLostSince = 0;
        }

        // Nothing showing, or messages the existing checks already handle.
        if (text.Length == 0
            || text.Contains("has been lost", StringComparison.OrdinalIgnoreCase)
            || text.Contains("loading map", StringComparison.OrdinalIgnoreCase))
        {
            ResetWatchdogTimer(text);
            return;
        }

        if (text != _wdText)
        {
            ResetWatchdogTimer(text);
            return;
        }

        int now = Environment.TickCount;
        if (now - _wdSince < WatchdogStallMs || !BackoffAllows())
            return;
        StartRecovery($" stuck on \"{text}\" for {WatchdogStallMs / 1000}s");
    }

    /// <summary>
    /// VibeSkua's auto re-login got the account back in game but the script is not running and nothing is going to
    /// start it: after 30 seconds steady in game, start it (once). Never after a deliberate logout, and not if the
    /// script is already running again.
    /// </summary>
    private void RestartAfterSilentRelogin()
    {
        if (_wdReloginMarker == 0)
            return;
        int now = Environment.TickCount;
        if (now - _wdReloginMarker > WatchdogFallbackWindowMs || Servers.UserLoggedOut || Manager.ScriptRunning)
        {
            _wdReloginMarker = 0;
            _wdFallbackSince = 0;
            return;
        }
        if (_waitForLogin || _wdBusy || _wdRestarting != 0 || RescueActive || _wdRestartScript)
        {
            _wdFallbackSince = 0;
            return;
        }
        if (_wdFallbackSince == 0)
        {
            _wdFallbackSince = now | 1;
            return;
        }
        if (now - _wdFallbackSince < WatchdogFallbackSteadyMs)
            return;
        _wdReloginMarker = 0;
        _wdFallbackSince = 0;
        WdLog(" VibeSkua's re-login got back in game but the script was not started again; starting it.");
        _wdRestartScript = true;
    }

    /// <summary>After 6 recoveries in a row wait a minute between tries, after 12 ten minutes.</summary>
    private bool BackoffAllows()
    {
        int sinceLast = Environment.TickCount - _wdLastAction;
        if (_wdStalls >= WatchdogSlowAfter)
            return sinceLast >= WatchdogSlowMs;
        if (_wdStalls >= WatchdogBackoffAfter)
            return sinceLast >= WatchdogBackoffMs;
        return true;
    }

    /// <param name="why">What happened, for the log (starts with a space).</param>
    /// <param name="minLevel">2 = restart the game window straight away instead of resetting the login screen first.</param>
    private void StartRecovery(string why, int minLevel = 1)
    {
        _wdBusy = true;
        _wdStalls++;
        _wdLastAction = Environment.TickCount;
        if (_wdStalls == WatchdogBackoffAfter)
            Alert("can't get back in game", $"{WatchdogBackoffAfter} recoveries in a row without getting back in game (last: {why.Trim()}). Still trying, once a minute now.", VibeAlerts.Level.Warning);
        if (_wdStalls == WatchdogSlowAfter)
        {
            WdLog($" {WatchdogSlowAfter} recoveries in a row (AQW may be down); trying every {WatchdogSlowMs / 60000} minutes from now on.");
            Alert("AQW may be down", $"{WatchdogSlowAfter} recoveries in a row without getting back in game. Still trying, every {WatchdogSlowMs / 60000} minutes now.", VibeAlerts.Level.Problem);
        }
        int level = Math.Max(_wdStalls, minLevel);
        _ = Task.Run(() => RecoverStuckLogin(why, level));
    }

    /// <summary>Writes to the script log and to login-watchdog.log next to VibeSkuffle.exe.</summary>
    private void WdLog(string message)
    {
        string line = $"Login watchdog:{message}";
        try { Log(line); } catch { }
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "login-watchdog.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 1_000_000)
                info.Delete();
            string who = Servers.CachedUsername ?? "?";
            lock (WdFileLock)
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{who}]{message}{Environment.NewLine}");
        }
        catch { }
    }

    private static readonly object WdFileLock = new();

    /// <summary>Discord alert (alerts.txt), with this account's name.</summary>
    private void Alert(string title, string text, VibeAlerts.Level level)
    {
        try { VibeAlerts.Send(Servers.CachedUsername ?? "?", title, text, level); } catch { }
    }

    /// <summary>The watchdog's own re-login, through VibeSkua's (cancellable by CancelRelogin like it).</summary>
    private void WatchdogRelogin(int delay)
    {
        CancellationTokenSource cts = new();
        _reloginCTS = cts;
        _waitForLogin = true;
        try
        {
            Relogin(delay, false, cts);
        }
        catch (OperationCanceledException)
        {
            _waitForLogin = false;
        }
    }

    private void ResetWatchdogTimer(string text)
    {
        _wdText = text;
        _wdSince = Environment.TickCount;
    }

    private async Task RecoverStuckLogin(string why, int level)
    {
        try
        {
            WdLog($"{why} (recovery {level}).");
            DogNotice.Working($"{why.Trim().TrimEnd('.', ';')}: logging back in");
            _wdReturnToRoom = true;

            // Credentials first: a reload clears what the player typed into the game.
            bool canLogin = Servers.RememberLoginInfo();
            Servers.MarkServerStalled(Servers.CurrentLoginServer);

            // Take over from an auto re-login that is waiting on the stuck screen.
            if (_waitForLogin ? _reloginStartsScript || ScriptRanRecently : ScriptRanRecently)
                _wdRestartScript = true;
            CancelRelogin();
            // Claim the login before awaiting the script stop, so VibeSkua's own re-login (2.0.7: it reacts to
            // any time out of game) doesn't start a second one meanwhile.
            _waitForLogin = true;
            if (Manager.ScriptRunning)
                await Manager.StopScript();

            try
            {
                if (Servers.IsConnected)
                    Flash.CallGameFunction("sfc.disconnect");
            }
            catch { }

            if (level >= 2 && Flash is IRestartableGamePlayer player)
            {
                // A fresh player clears whatever the game got stuck on (hung requests, half-open
                // connections, broken login screen).
                WdLog(" restarting the game player.");
                DogNotice.Working("Restarting the game window…");
                _wdGameLoaded = false;
                _wdOwnRestart = true;
                player.RestartGamePlayer();
                await WaitForGameLoaded(90000);
            }
            else
            {
                WdLog(" resetting the login screen.");
                try { Flash.CallGameFunction("gotoAndPlay", "Login"); } catch { }
                await Task.Delay(2000);
            }

            if (!canLogin)
            {
                WdLog(" no saved login for this client, log in again by hand.");
                Alert("needs a login", "Got logged out and has no saved login to log back in with. Log in by hand.", VibeAlerts.Level.Problem);
                DogNotice.Failed("No saved login for this client: log in by hand");
                _waitForLogin = false;
                return;
            }

            // Same path as auto re-login: picks a server (avoiding the one that got stuck), logs in,
            // and restarts the script if one was running.
            // Spread out: when AQW hiccups, every client in the army recovers at once.
            int delay = (level == 1 ? 1000 : 3000) + Random.Shared.Next(0, 5000);
            WatchdogRelogin(delay); // the script restart is handled by _wdRestartScript
        }
        catch (Exception ex)
        {
            WdLog($" failed: {ex.Message}");
            DogNotice.Failed($"Recovery failed: {ex.Message}");
            _waitForLogin = false;
        }
        finally
        {
            ResetWatchdogTimer(string.Empty);
            _wdBusy = false;
        }
    }

    /// <summary>Called when the game finishes loading ("loaded").</summary>
    private void OnGameLoaded()
    {
        _wdGameLoaded = true;
        if (_wdOwnRestart)
        {
            _wdOwnRestart = false;
            return;
        }
        // First start, or not in game recently: nothing to restore.
        if (_wdLastInGame == 0 || Environment.TickCount - _wdLastInGame > 180000)
            return;
        _wdLastInGame = 0;
        if (_wdScriptWhenInGame || ScriptRanRecently)
            _wdRestartScript = true;
        _wdReturnToRoom = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            // With Auto Relogin on, VibeSkua's own re-login takes care of it; give it a moment to start.
            for (int i = 0; i < 20 && Options.AutoRelogin && !_waitForLogin; i++)
                await Task.Delay(500);
            if (_waitForLogin || _wdBusy || (Player.Playing && Flash.IsWorldLoaded))
            {
                WdLog(" the game window restarted while in game (crash, freeze, memory limit or graphics switch); auto relogin is logging back in.");
                return;
            }
            WdLog(" the game window restarted while in game (crash, freeze, memory limit or graphics switch); logging back in.");
            if (!Servers.RememberLoginInfo())
            {
                WdLog(" no saved login for this client, log in again by hand.");
                return;
            }
            _waitForLogin = true;
            // The old run was talking to the crashed player; stop it so it can start fresh once back in
            // game (VibeSkua refuses to start a script that is still running).
            if (Manager.ScriptRunning)
            {
                WdLog(" stopping the script; it starts again once back in game.");
                await Manager.StopScript();
            }
            WatchdogRelogin(3000 + Random.Shared.Next(0, 5000));
        });
    }

    // ---- A running script that stopped doing anything ---------------------------------------------------
    // Overnight, scripts sat in game for hours doing nothing (AQW refusing quest turn-ins, a script stuck
    // waiting). When a script runs but there have been no kills, drops, quest turn-ins/accepts, gold changes
    // or moves for SKUA_STUCK_SCRIPT_MINUTES (default 15, 0 = off), log out and back in; the script starts
    // again once back in game. At most once every 20 minutes.

    private int _stuckCheckAt;
    private string _stuckSignature = "";
    private int _stuckSince;
    private int _lastStuckRelog;
    private static int StuckScriptMinutes =>
        int.TryParse(Environment.GetEnvironmentVariable("SKUA_STUCK_SCRIPT_MINUTES"), out int m) && m >= 0 ? m : 15;

    private void CheckStuckScript()
    {
        if (RescueActive) return;
        int now = Environment.TickCount;
        if (now - _stuckCheckAt < 30000)
            return;
        _stuckCheckAt = now;
        int minutes = StuckScriptMinutes;
        if (minutes == 0 || !Manager.ScriptRunning)
        {
            _stuckSince = 0;
            return;
        }
        string signature;
        try
        {
            signature = $"{Stats.Kills}|{Stats.Drops}|{Stats.QuestsCompleted}|{Stats.QuestsAccepted}|{Player.Gold}|{Map.FullName}|{Player.Cell}";
        }
        catch
        {
            return;
        }
        if (signature != _stuckSignature || _stuckSince == 0)
        {
            _stuckSignature = signature;
            _stuckSince = now | 1;
            return;
        }
        if (now - _stuckSince < minutes * 60000)
            return;
        if (_lastStuckRelog != 0 && now - _lastStuckRelog < 1200000)
            return;
        _lastStuckRelog = now | 1;
        _stuckSince = 0;
        StartFixRecovery($" the script has done nothing for {minutes} minutes (no kills, drops, quests or moving)");
    }

    // ---- "Quest Complete Failed: Missing Quest Progress" ------------------------------------------------
    // AQW sometimes gets stuck refusing every turn-in of a quest that is actually done; logging out and back
    // in fixes it. When that message keeps coming while a script runs, relog (the script starts again after).

    private const int QuestErrorCount = 5;
    private const int QuestErrorWindowMs = 120000;
    private const int QuestErrorCooldownMs = 600000;
    private readonly Queue<int> _questErrors = new();
    private readonly Dictionary<int, Queue<int>> _questFailures = new();
    private int _lastQuestRelog;

    /// <summary>A packet said "Missing Quest Progress".</summary>
    private void OnMissingQuestProgress() => CountQuestError(_questErrors, QuestErrorCount, "\"Quest Complete Failed: Missing Quest Progress\"");

    /// <summary>A quest turn-in was refused (any reason). Only counts when the same quest is refused over and over.</summary>
    private void OnQuestTurnInFailed(int questId)
    {
        Queue<int> q;
        lock (_questFailures)
        {
            if (!_questFailures.TryGetValue(questId, out q!))
                _questFailures[questId] = q = new Queue<int>();
            if (_questFailures.Count > 50)
                _questFailures.Clear();
        }
        CountQuestError(q, QuestErrorCount * 2, $"quest {questId} turn-in refused");
    }

    private void CountQuestError(Queue<int> times, int needed, string what)
    {
        int now = Environment.TickCount;
        lock (times)
        {
            times.Enqueue(now);
            while (times.Count > 0 && now - times.Peek() > QuestErrorWindowMs)
                times.Dequeue();
            if (times.Count < needed)
                return;
            times.Clear();
        }
        if (_wdBusy || _waitForLogin || !Manager.ScriptRunning || !Player.LoggedIn)
            return;
        if (_lastQuestRelog != 0 && now - _lastQuestRelog < QuestErrorCooldownMs)
            return;
        _lastQuestRelog = now | 1;
        StartFixRecovery($" {what} {needed} times in {QuestErrorWindowMs / 60000} minutes while farming (an AQW bug a relog usually fixes)");
    }

    // ---- "One Time Quest Only" spam ----------------------------------------------------------------------
    // A script (or the game) that keeps asking for a quest that can only be done once makes the server answer
    // "One Time Quest Only" over and over. Logging out and in clears it. If it starts again soon after that relog,
    // a relog is not enough: restart the whole client.

    private const int OneTimeQuestCount = 8;
    private const int OneTimeQuestWindowMs = 60000;
    private const int OneTimeQuestAgainMs = 15 * 60000;
    private readonly Queue<int> _oneTimeQuest = new();
    private int _lastOneTimeQuestFix;

    /// <summary>A packet said "One Time Quest Only".</summary>
    private void OnOneTimeQuestOnly()
    {
        if (_wdBusy || _waitForLogin || !Player.LoggedIn)
            return;
        int now = Environment.TickCount;
        lock (_oneTimeQuest)
        {
            _oneTimeQuest.Enqueue(now);
            while (_oneTimeQuest.Count > 0 && now - _oneTimeQuest.Peek() > OneTimeQuestWindowMs)
                _oneTimeQuest.Dequeue();
            if (_oneTimeQuest.Count < OneTimeQuestCount)
                return;
            _oneTimeQuest.Clear();
        }
        bool again = _lastOneTimeQuestFix != 0 && now - _lastOneTimeQuestFix < OneTimeQuestAgainMs;
        _lastOneTimeQuestFix = now | 1;
        StartFixRecovery($" \"One Time Quest Only\" came {OneTimeQuestCount} times in {OneTimeQuestWindowMs / 1000} seconds", restartClientNow: again);
    }

    private async Task ReturnToRoom(string room, string? cell, string? pad)
    {
        try
        {
            await Task.Delay(3000);
            if (Manager.ScriptRunning || !(Player.Playing && Flash.IsWorldLoaded))
                return;
            if (string.Equals(Map.FullName, room, StringComparison.OrdinalIgnoreCase))
                return;
            WdLog($" back in game; returning to {room}.");
            Map.Join(room, string.IsNullOrWhiteSpace(cell) ? "Enter" : cell, string.IsNullOrWhiteSpace(pad) ? "Spawn" : pad);
        }
        catch (Exception ex)
        {
            WdLog($" couldn't return to {room}: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts the script again once the account is properly back in game: logged in, map loaded and nothing
    /// changing for a few seconds. Checks that it really is running afterwards and tries up to 3 times.
    /// </summary>
    private async Task RestartScriptAfterLogin()
    {
        if (Interlocked.Exchange(ref _wdRestarting, 1) == 1)
            return;
        try
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (!await WaitInGameSteady(6000, 180000))
                    return; // not back in game yet; the flag stays set and the next time in game tries again
                if (!_wdRestartScript || Servers.UserLoggedOut)
                {
                    if (_wdRestartScript)
                        WdLog(" not restarting the script: the account was logged out on purpose.");
                    _wdRestartScript = false;
                    return;
                }
                if (Manager.ScriptRunning)
                {
                    WdLog(" the script is already running again (VibeSkua's re-login or someone started it).");
                    _wdRestartScript = false;
                    return; // someone (or VibeSkua's own re-login) already started it
                }
                WdLog(attempt == 1 ? " back in game; starting the script again." : $" starting the script again (try {attempt} of 3).");
                // Started by recovery with nobody there: use the script's saved options. (VibeSkua 2.0.6 waits for
                // the options window before starting a script; it would sit there until someone clicked it.)
                IScriptManager scriptManager = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetRequiredService<IScriptManager>();
                bool silent = scriptManager.SilentConfig;
                scriptManager.SilentConfig = true;
                Exception? error;
                try { error = await scriptManager.StartScript(); }
                finally { scriptManager.SilentConfig = silent; }
                if (error is not null)
                {
                    WdLog($" couldn't start the script: {error.Message}");
                }
                else
                {
                    // Wait a moment: if it gets knocked out of the game right away, leaving the game sets up another go.
                    await Task.Delay(15000);
                    if (!Manager.ScriptRunning && !(Player.Playing && Flash.IsWorldLoaded))
                        return; // knocked out again; leaving the game sets it up for another go
                    // Running, or it finished / was stopped by itself while the game is fine: done either way
                    // (starting a script that ends by itself over and over would be wrong).
                    if (!Manager.ScriptRunning)
                        WdLog(" the script started but is no longer running 15s later; not starting it again.");
                    _wdRestartScript = false;
                    return;
                }
                await Task.Delay(10000);
            }
            _wdRestartScript = false;
            WdLog(" couldn't get the script running again after 3 tries; leaving it stopped.");
            Alert("script left stopped", "Couldn't get its script running again after 3 tries; it's back in game but idle.", VibeAlerts.Level.Problem);
        }
        catch (Exception ex)
        {
            WdLog($" couldn't start the script: {ex.Message}");
        }
        finally
        {
            _wdRestarting = 0;
        }
    }

    /// <summary>Waits until the account has been in game (world loaded, no login going on) for <paramref name="steadyMs"/>.</summary>
    private async Task<bool> WaitInGameSteady(int steadyMs, int timeoutMs)
    {
        int start = Environment.TickCount, since = 0;
        while (!_disposed && Environment.TickCount - start < timeoutMs)
        {
            bool inGame = !_waitForLogin && !_wdBusy && Player.Playing && Flash.IsWorldLoaded;
            if (!inGame)
                since = 0;
            else if (since == 0)
                since = Environment.TickCount | 1;
            else if (Environment.TickCount - since >= steadyMs)
                return true;
            await Task.Delay(500);
        }
        return false;
    }

    // ---- When a relog doesn't fix it -------------------------------------------------------------------
    // Seen: an account that keeps getting stuck again soon after every relog (out of sync with the server);
    // only closing the whole client and starting it again fixed it. So: the 2nd stuck relog within 45
    // minutes restarts the game window instead, the 3rd restarts the whole client (the tab window starts it
    // again with the same login and script). The whole client at most once every 2 hours per account.

    private readonly List<int> _wdFixTimes = new();

    private void StartFixRecovery(string why, bool restartClientNow = false)
    {
        int now = Environment.TickCount;
        int count;
        lock (_wdFixTimes)
        {
            _wdFixTimes.RemoveAll(t => now - t > 45 * 60000);
            _wdFixTimes.Add(now);
            count = _wdFixTimes.Count;
        }
        if ((count >= 3 || restartClientNow) && Flash is IRestartableGamePlayer player && player.CanRestartClient && ClientRestartAllowed())
        {
            lock (_wdFixTimes)
                _wdFixTimes.Clear();
            string reason = restartClientNow ? "it came back right after the relog" : $"logging out and back in hasn't fixed it ({count} times in 45 minutes)";
            WdLog($"{why}; {reason}. Restarting the whole client (same as closing the tab and starting it again).");
            Alert("whole client restarted", $"{why.Trim()}; {reason}, so the whole client was restarted (it comes back with its script).", VibeAlerts.Level.Warning);
            _wdBusy = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    bool runScript = ScriptRanRecently;
                    if (Manager.ScriptRunning)
                        await Manager.StopScript();
                    player.RestartClient(runScript);
                }
                catch (Exception ex)
                {
                    WdLog($" couldn't restart the client: {ex.Message}");
                    _wdBusy = false;
                }
            });
            return;
        }
        if (count >= 2)
            StartRecovery($"{why}, again within 45 minutes of the last relog; restarting the game window this time", 2);
        else
            StartRecovery($"{why}; logging out and back in");
    }

    /// <summary>At most one whole-client restart per account every 2 hours (kept in a file: the restart makes a new process).</summary>
    private bool ClientRestartAllowed()
    {
        try
        {
            string who = new((Servers.CachedUsername ?? "unknown").Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            string dir = Path.Combine(Path.GetTempPath(), "VibeSkuaTabs");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"client-restart-{who}.txt");
            if (File.Exists(file) && long.TryParse(File.ReadAllText(file).Trim(), out long ticks)
                && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < TimeSpan.FromHours(2))
                return false;
            File.WriteAllText(file, DateTime.UtcNow.Ticks.ToString());
            return true;
        }
        catch
        {
            return true;
        }
    }

    private async Task WaitForGameLoaded(int timeoutMs)
    {
        int start = Environment.TickCount;
        while (!_wdGameLoaded && Environment.TickCount - start < timeoutMs && !_disposed)
            await Task.Delay(250);
        if (!_wdGameLoaded)
            WdLog(" the game didn't finish loading in time; trying to log in anyway.");
        else
            await Task.Delay(1500);
    }
}
