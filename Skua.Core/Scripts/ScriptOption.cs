using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Flash;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Linq.Expressions;
using System.Reflection;

namespace Skua.Core.Scripts;

public partial class ScriptOption : ObservableRecipient, IScriptOption, IOptionDictionary
{
    private ScriptOption(Lazy<IFlashUtil> flash)
    {
        _lazyFlash = flash;
    }

    public ScriptOption(
        Lazy<IFlashUtil> flash,
        ISettingsService settingsService)
    {
        _lazyFlash = flash;
        _settingsService = settingsService;
        _userLagKiller = _settingsService?.Get<bool>("UserLagKiller", false) ?? false;
        _lagKiller = _userLagKiller;
        _isInitializing = true;
        _stageQuality = ReadGlobalStageQuality();
        GetOptions();
        _acceptACDrops = ReadGlobalAcceptACDrops();
        _pickupACDropsAndSendToBank = settingsService?.Get<bool>("BonxieMux.PickupACDropsAndSendToBank") ?? false;
        _rejectNonACDrops = settingsService?.Get<bool>("BonxieMux.RejectNonACDrops") ?? false;
        OptionDictionary = GenerateDictionary().ToImmutableDictionary();
        PropertyChanged += (_, e) =>
        {
            if (!_isInitializing && e.PropertyName == nameof(SetFPS) && (IsHeadlessNow() || _isBackgroundThrottled))
                RequestRenderStateSync(flushGamePref: false);
        };
        StrongReferenceMessenger.Default.Register<ScriptOption, ScriptStartedMessage, int>(this, (int)MessageChannels.ScriptStatus, ScriptStarted);
        StrongReferenceMessenger.Default.Register<ScriptOption, ScriptStoppedMessage, int>(this, (int)MessageChannels.ScriptStatus, ScriptStopped);
        StrongReferenceMessenger.Default.Register<ScriptOption, MapChangedMessage, int>(this, (int)MessageChannels.GameEvents, MapChanged);
        _isInitializing = false;
        EvaluateAntiLagState();
        _rescueSettings = WatchDog.Shared.SharedRescueSettings.Instance;
        _rescueSettings.Changed += NotifyRescueSettingsChanged;
    }

    private readonly record struct LiteAnimationSnapshot(
        bool LagKiller,
        bool FreezeMonsterPosition,
        bool DisableMonsterAnimation,
        bool DisableDamageStrobe,
        bool DisableSelfAnimation,
        bool DisableWeaponAnimation,
        bool DisableSkillAnimation,
        bool DisableAuraAnimations,
        bool MonstersHidden,
        string CrowdMode);

    private readonly object _antiLagSyncLock = new();
    private LiteAnimationSnapshot? _priorLiteAnimations;
    private bool _scriptAntiLagActive;
    private bool _tabIsShown = true;
    private bool _autoAntiLagHeld;
    private bool _manualOrAutoAntiLagDesired;
    private bool _anyAntiLagApplied;
    private bool _suppressUserLagKillerSave;
    private long _antiLagEpoch;

    private bool _antiLag;
    public bool AntiLag
    {
        get => _antiLag;
        set
        {
            if (!SetProperty(ref _antiLag, value, true))
                return;

            if (!value)
                _autoAntiLagHeld = false;

            if (!_isInitializing)
            {
                EvaluateAntiLagState();
                BroadcastSharedCommandToOtherClients("antiLag", nameof(AntiLag), value ? "True" : "False");
            }
        }
    }

    public void ApplySharedAntiLag(bool value)
    {
        if (_antiLag == value)
            return;

        SetProperty(ref _antiLag, value, true);
        if (!value)
            _autoAntiLagHeld = false;

        EvaluateAntiLagState();
    }

    [ObservableProperty]
    private bool _autoAntiLag = false;

    [ObservableProperty]
    private bool _autoAntiLagLiftOnFocus = true;

    partial void OnAutoAntiLagChanged(bool value)
    {
        if (!value)
            _autoAntiLagHeld = false;
        EvaluateAntiLagState();
    }

    partial void OnAutoAntiLagLiftOnFocusChanged(bool value) => EvaluateAntiLagState();

    public void SetTabShown(bool shown)
    {
        if (_tabIsShown == shown)
            return;

        _tabIsShown = shown;
        EvaluateAntiLagState();
    }

    private bool IsManualOrAutoAntiLagWanted()
    {
        bool wantAuto = false;
        if (_autoAntiLag)
        {
            if (!_tabIsShown)
            {
                wantAuto = true;
                _autoAntiLagHeld = true;
            }
            else if (_autoAntiLagHeld && !_autoAntiLagLiftOnFocus)
            {
                wantAuto = true;
            }
            else
            {
                _autoAntiLagHeld = false;
            }
        }
        else
        {
            _autoAntiLagHeld = false;
        }

        _manualOrAutoAntiLagDesired = _antiLag || wantAuto;
        return _manualOrAutoAntiLagDesired;
    }

    private void EvaluateAntiLagState()
    {
        if (_isInitializing)
            return;

        bool wantManualOrAuto;
        bool wantEffective;
        long epoch;

        lock (_antiLagSyncLock)
        {
            wantManualOrAuto = IsManualOrAutoAntiLagWanted();
            wantEffective = wantManualOrAuto || (_scriptRunning && _scriptAntiLagActive);
            if (wantEffective == _anyAntiLagApplied)
                return;

            _anyAntiLagApplied = wantEffective;
            epoch = ++_antiLagEpoch;
        }

        if (wantEffective)
        {
            if (_priorLiteAnimations == null)
                CaptureLiteAnimationState();

            _ = Task.Run(() => ApplyFullAntiLagBundleWithGuard(epoch, reassertAfterStop: false));
        }
        else
        {
            _ = Task.Run(() => RestoreLiteAnimationState(forceRestoreCrowd: true, clearSnapshot: !_scriptRunning));
        }
    }

    private void ApplyFullAntiLagBundleWithGuard(long expectedEpoch, bool reassertAfterStop)
    {
        ApplyFullAntiLagBundle();

        if (!reassertAfterStop)
            return;

        // CoreBots.StopBotAsync runs a background Task.Run on stop that calls world.toggleMonsters
        // without awaiting. Re-assert our Anti-Lag state after a short settle delay so CoreBots'
        // stop task never leaves monsters visible while manual Anti-Lag or Auto Anti-Lag is active.
        _ = Task.Run(async () =>
        {
            int[] delaysMs = [250, 600];
            foreach (int delay in delaysMs)
            {
                await Task.Delay(delay).ConfigureAwait(false);
                lock (_antiLagSyncLock)
                {
                    if (_antiLagEpoch != expectedEpoch || !_anyAntiLagApplied)
                        return;
                }
                ApplyFullAntiLagBundle();
            }
        });
    }

    private void ApplyFullAntiLagBundle()
    {
        try
        {
            var flash = _lazyFlash.Value;
            if (flash == null || !flash.IsWorldLoaded)
                return;

            _suppressUserLagKillerSave = true;
            try
            {
                LagKiller = true;
            }
            finally
            {
                _suppressUserLagKillerSave = false;
            }

            flash.SetGameObject("litePreference.data.bFreezeMons", true);
            flash.SetGameObject("litePreference.data.bDisMonAnim", true);
            flash.SetGameObject("litePreference.data.bDisDmgStrobe", true);
            flash.SetGameObject("litePreference.data.bDisSelfMAnim", true);
            flash.SetGameObject("litePreference.data.bDisWepAnim", true);
            flash.SetGameObject("litePreference.data.bDisSkillAnim", true);
            flash.SetGameObject("litePreference.data.bDisAuraAnim", true);

            if (!flash.GetGameObject<bool>("ui.monsterIcon.redX.visible"))
                flash.CallGameFunction("world.toggleMonsters");

            ApplyCrowdMode(true);
        }
        catch { }
    }

    private void ScriptStarted(ScriptOption recipient, ScriptStartedMessage message)
    {
        lock (recipient._antiLagSyncLock)
        {
            recipient._scriptRunning = true;
            recipient._scriptAntiLagActive = false;
            if (!recipient._anyAntiLagApplied)
                recipient._userLagKiller = recipient._lagKiller;
        }

        if (recipient._priorLiteAnimations == null)
            recipient.CaptureLiteAnimationState();

        if (recipient.StreamerMode)
            recipient.ApplyStreamerMode(true);
    }

    internal void OnLiteAntiLagTriggered()
    {
        lock (_antiLagSyncLock)
        {
            if (!_scriptRunning || _scriptAntiLagActive)
                return;

            _scriptAntiLagActive = true;
            _anyAntiLagApplied = true;
            _antiLagEpoch++;
        }

        ApplyCrowdMode(true);
    }

    private void CaptureLiteAnimationState()
    {
        try
        {
            var flash = _lazyFlash.Value;
            if (flash == null || !flash.IsWorldLoaded)
            {
                _priorLiteAnimations = null;
                return;
            }

            _priorLiteAnimations = new LiteAnimationSnapshot(
                LagKiller: _userLagKiller,
                FreezeMonsterPosition: flash.GetGameObject<bool>("litePreference.data.bFreezeMons"),
                DisableMonsterAnimation: flash.GetGameObject<bool>("litePreference.data.bDisMonAnim"),
                DisableDamageStrobe: flash.GetGameObject<bool>("litePreference.data.bDisDmgStrobe"),
                DisableSelfAnimation: flash.GetGameObject<bool>("litePreference.data.bDisSelfMAnim"),
                DisableWeaponAnimation: flash.GetGameObject<bool>("litePreference.data.bDisWepAnim"),
                DisableSkillAnimation: flash.GetGameObject<bool>("litePreference.data.bDisSkillAnim"),
                DisableAuraAnimations: flash.GetGameObject<bool>("litePreference.data.bDisAuraAnim"),
                MonstersHidden: flash.GetGameObject<bool>("ui.monsterIcon.redX.visible"),
                CrowdMode: flash.GetGameObject<string>("litePreference.data.sCrowd") ?? "off");
        }
        catch
        {
            _priorLiteAnimations = null;
        }
    }

    private void RestoreLiteAnimationState(bool forceRestoreCrowd = false, bool clearSnapshot = true)
    {
        var snapshot = _priorLiteAnimations;
        if (clearSnapshot)
            _priorLiteAnimations = null;

        bool wasAntiLagActive = _scriptAntiLagActive || forceRestoreCrowd;
        if (clearSnapshot)
            _scriptAntiLagActive = false;

        if (snapshot is not { } s)
            return;

        ApplySnapshotValues(s, wasAntiLagActive);
    }

    private void ApplySnapshotValues(LiteAnimationSnapshot s, bool restoreCrowd)
    {
        try
        {
            _suppressUserLagKillerSave = true;
            try
            {
                LagKiller = s.LagKiller;
            }
            finally
            {
                _suppressUserLagKillerSave = false;
            }

            var flash = _lazyFlash.Value;
            if (flash == null || !flash.IsWorldLoaded)
                return;

            flash.SetGameObject("litePreference.data.bFreezeMons", s.FreezeMonsterPosition);
            flash.SetGameObject("litePreference.data.bDisMonAnim", s.DisableMonsterAnimation);
            flash.SetGameObject("litePreference.data.bDisDmgStrobe", s.DisableDamageStrobe);
            flash.SetGameObject("litePreference.data.bDisSelfMAnim", s.DisableSelfAnimation);
            flash.SetGameObject("litePreference.data.bDisWepAnim", s.DisableWeaponAnimation);
            flash.SetGameObject("litePreference.data.bDisSkillAnim", s.DisableSkillAnimation);
            flash.SetGameObject("litePreference.data.bDisAuraAnim", s.DisableAuraAnimations);

            bool hidden = flash.GetGameObject<bool>("ui.monsterIcon.redX.visible");
            if (hidden != s.MonstersHidden)
                flash.CallGameFunction("world.toggleMonsters");

            if (restoreCrowd)
            {
                string restore = !string.IsNullOrEmpty(s.CrowdMode) ? s.CrowdMode : "off";
                ApplyCrowdMode(restore == "on", restore);
            }
        }
        catch { }
    }

    private CancellationTokenSource? _mapQualityCts;

    private void MapChanged(ScriptOption recipient, MapChangedMessage message)
    {
        if (recipient._anyAntiLagApplied && recipient._priorLiteAnimations != null)
        {
            Task.Run(async () =>
            {
                await Task.Delay(500);
                if (recipient._anyAntiLagApplied)
                    recipient.ApplyCrowdMode(true);
            });
        }

        if (recipient.StreamerMode)
        {
            Task.Run(async () =>
            {
                await Task.Delay(1000);
                recipient.ApplyStreamerMode(true);
            });
        }

        EnforceMapQualityAsync(recipient);
    }

    private static void EnforceMapQualityAsync(ScriptOption recipient)
    {
        try
        {
            recipient.EnforceRenderState(flushGamePref: false);

            var cts = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref recipient._mapQualityCts, cts);
            try { previous?.Cancel(); } catch (ObjectDisposedException) { }
            CancellationToken token = cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    // Wait for the new map SWF load to finish so any frame-1 quality reset is overridden.
                    for (int i = 0; i < 40 && !token.IsCancellationRequested; i++)
                    {
                        await Task.Delay(250, token);
                        var flash = recipient._lazyFlash.Value;
                        if (flash == null)
                            break;

                        if (!flash.TryCall<bool>("isNull", TimeSpan.FromMilliseconds(1000), token, out bool isWorldNull, "world"))
                            continue;

                        bool loading = !isWorldNull
                            && flash.TryCall<bool>("getGameObject", TimeSpan.FromMilliseconds(1000), token, out bool mapLoading, "world.mapLoadInProgress")
                            && mapLoading;
                        if (!loading)
                        {
                            await Task.Delay(150, token);
                            break;
                        }
                    }

                    if (!token.IsCancellationRequested)
                        recipient.EnforceRenderState(flushGamePref: false);
                }
                catch (OperationCanceledException) { }
                catch { }
                finally
                {
                    Interlocked.CompareExchange(ref recipient._mapQualityCts, null, cts);
                    cts.Dispose();
                }
            });
        }
        catch { }
    }

    private void ScriptStopped(ScriptOption recipient, ScriptStoppedMessage message)
    {
        recipient.AutoRelogin = false;
        recipient.AggroAllMonsters = false;
        recipient.AggroMonsters = false;
        recipient.SkipCutscenes = false;

        bool keepAntiLag;
        long epoch;
        lock (recipient._antiLagSyncLock)
        {
            recipient._scriptRunning = false;
            keepAntiLag = recipient.IsManualOrAutoAntiLagWanted();
            recipient._anyAntiLagApplied = keepAntiLag;
            epoch = ++recipient._antiLagEpoch;
        }

        if (keepAntiLag)
        {
            recipient._scriptAntiLagActive = false;
            recipient.ApplyFullAntiLagBundleWithGuard(epoch, reassertAfterStop: true);
        }
        else
        {
            recipient.LagKiller = recipient._userLagKiller;
            recipient.RestoreLiteAnimationState();
        }
    }

    private void ApplyCrowdMode(bool enabled, string? modeOverride = null)
    {
        try
        {
            var flash = _lazyFlash.Value;
            if (flash == null || !flash.IsWorldLoaded)
                return;

            string mode = modeOverride ?? (enabled ? "on" : "off");
            flash.SetGameObject("litePreference.data.sCrowd", mode);
            flash.SetGameObject("world.rootClass.crowdOn", enabled);
            flash.CallGameFunction("world.rootClass.applyPlayerCache");
        }
        catch { }
    }

    private readonly Lazy<IFlashUtil> _lazyFlash;
    private readonly ISettingsService _settingsService;
    private Dictionary<string, string>? _userOptions;
    private bool _isInitializing;

    private IFlashUtil Flash => _lazyFlash.Value;

    public ImmutableDictionary<string, Func<object>> OptionDictionary { get; }

    [ObservableProperty]
    private bool _attackWithoutTarget;

    private bool _acceptACDrops;

    public bool AcceptACDrops
    {
        get => _acceptACDrops;
        set
        {
            if (!SetProperty(ref _acceptACDrops, value, true))
                return;
            if (!_isInitializing)
            {
                PersistGlobalAcceptACDrops(value);
                BroadcastSharedCommandToOtherClients("acceptACDrops", nameof(AcceptACDrops), value ? "True" : "False");
            }
        }
    }

    public void ApplySharedAcceptACDrops(bool value)
    {
        if (_acceptACDrops == value)
            return;

        SetProperty(ref _acceptACDrops, value, true);
        try
        {
            var shared = _settingsService?.GetShared();
            if (shared is not null)
            {
                shared.AcceptACDrops = value;
                shared.HasAcceptACDrops = true;
            }
        }
        catch { }
    }

    private bool ReadGlobalAcceptACDrops()
    {
        try
        {
            var shared = _settingsService?.GetShared();
            if (shared is not null)
            {
                if (shared.HasAcceptACDrops || shared.AcceptACDrops)
                    return shared.AcceptACDrops;
                if (_userOptions is not null &&
                    _userOptions.TryGetValue(nameof(AcceptACDrops), out string? legacy) &&
                    bool.TryParse(legacy, out bool legacyVal) &&
                    legacyVal)
                {
                    PersistGlobalAcceptACDrops(true);
                    return true;
                }
            }
            return _settingsService?.Get<bool>(nameof(AcceptACDrops)) ?? false;
        }
        catch
        {
            return false;
        }
    }

    private void PersistGlobalAcceptACDrops(bool value)
    {
        try
        {
            var shared = _settingsService?.GetShared();
            if (shared is not null)
                shared.HasAcceptACDrops = true;
            _settingsService?.Set(nameof(AcceptACDrops), value);
        }
        catch { }
    }

    private bool _pickupACDropsAndSendToBank;
    public bool PickupACDropsAndSendToBank
    {
        get => _pickupACDropsAndSendToBank;
        set { if (SetProperty(ref _pickupACDropsAndSendToBank, value, true) && !_isInitializing) _settingsService?.Set("BonxieMux.PickupACDropsAndSendToBank", value); }
    }

    private bool _rejectNonACDrops;
    public bool RejectNonACDrops
    {
        get => _rejectNonACDrops;
        set { if (SetProperty(ref _rejectNonACDrops, value, true) && !_isInitializing) _settingsService?.Set("BonxieMux.RejectNonACDrops", value); }
    }

    [ObservableProperty]
    private bool _ignoreScriptRejectElse;

    // When another account on the same server keeps dying (10 deaths without a kill, or 15
    // minutes of only deaths), stop this account's script, go help, then come back and start it again.
    private WatchDog.Shared.SharedRescueSettings? _rescueSettings;
    private string _rescueAccount = "";
    public bool RescueAllies
    {
        get => _rescueSettings?.Enabled ?? false;
        set { if (!_isInitializing) _rescueSettings?.SetEnabled(value); }
    }
    public bool AllowThisAccountToRescue
    {
        get => _rescueSettings?.IsAllowed(_rescueAccount) ?? true;
        set { if (!_isInitializing && _rescueAccount.Length > 0) _rescueSettings?.SetAllowed(_rescueAccount, value); }
    }
    public int RescueDelayMinutes
    {
        get => _rescueSettings?.DelayMinutes ?? 15;
        set { if (!_isInitializing) _rescueSettings?.SetDelayMinutes(value); }
    }
    public int RescueDeathsBeforeCalling
    {
        get => _rescueSettings?.DeathsBeforeRescue ?? 10;
        set { if (!_isInitializing) _rescueSettings?.SetDeathsBeforeRescue(value); }
    }
    /// <summary>Wait before helping the same account again (shared with every client on this PC).</summary>
    public bool RescueCooldownEnabled
    {
        get => _rescueSettings?.CooldownEnabled ?? true;
        set { if (!_isInitializing) _rescueSettings?.SetCooldownEnabled(value); }
    }
    public int RescueCooldownMinutes
    {
        get => _rescueSettings?.CooldownMinutes ?? 10;
        set { if (!_isInitializing) _rescueSettings?.SetCooldownMinutes(value); }
    }
    /// <summary>A helper that dies this many times without a kill gives up and restores its own setup.</summary>
    public int RescueHelperDeathLimit
    {
        get => _rescueSettings?.HelperDeathsWithoutKill ?? 5;
        set { if (!_isInitializing) _rescueSettings?.SetHelperDeathsWithoutKill(value); }
    }
    /// <summary>Helpers are released after this many minutes without a kill or death of the caller (0 = no limit).</summary>
    public int RescueHoldLimitMinutes
    {
        get => _rescueSettings?.HoldLimitMinutes ?? 30;
        set { if (!_isInitializing) _rescueSettings?.SetHoldLimitMinutes(value); }
    }
    /// <summary>Helpers switch to the CoreBots Boss class and gear while helping (default on).</summary>
    public bool RescueUseBossLoadout
    {
        get => _rescueSettings?.UseBossLoadout ?? true;
        set { if (!_isInitializing) _rescueSettings?.SetUseBossLoadout(value); }
    }

    /// <summary>One account of the Watch Dog's Accounts tab.</summary>
    public sealed record RescueAccountInfo(string Account, string Server, string Map, string Status, bool CanHelp, bool CanRequest);

    /// <summary>The accounts on this PC that run the Watch Dog (VibeSkuffle or the plugin) and reported in the last seconds.</summary>
    public IReadOnlyList<RescueAccountInfo> RescueAccounts()
    {
        DateTime now = DateTime.UtcNow;
        List<RescueAccountInfo> list = new();
        foreach (var a in WatchDog.Shared.RescueAccountHub.ReadAccounts())
        {
            var s = a.Status;
            string status = !s.Enabled ? "Rescue Allies is off" : s.Calling ? "Help requested" : s.Phase.Length > 0 ? s.Phase : s.Helper;
            bool canHelp = _rescueSettings?.IsAllowed(a.Account) ?? true;
            list.Add(new RescueAccountInfo(a.Account, a.Server, a.Map, status, canHelp, s.Enabled && !s.Calling));
        }
        return list;
    }

    /// <summary>Asks the named account to request help in its current map (it needs Rescue Allies on). Returns what to tell the person.</summary>
    public string RequestRescueHelpFor(string account) => WatchDog.Shared.RescueAccountHub.RequestManualHelp(account);

    /// <summary>Lets or stops the named account from helping others.</summary>
    public void SetAccountCanHelp(string account, bool allowed) => _rescueSettings?.SetAllowed(account, allowed);

    internal void BindRescueAccount(string? account)
    {
        account = account?.Trim() ?? "";
        if (_rescueAccount.Equals(account, StringComparison.OrdinalIgnoreCase)) return;
        _rescueAccount = account;
        OnPropertyChanged(nameof(AllowThisAccountToRescue));
    }

    private void NotifyRescueSettingsChanged()
    {
        OnPropertyChanged(nameof(RescueAllies));
        OnPropertyChanged(nameof(AllowThisAccountToRescue));
        OnPropertyChanged(nameof(RescueDelayMinutes));
        OnPropertyChanged(nameof(RescueDeathsBeforeCalling));
        OnPropertyChanged(nameof(RescueCooldownEnabled));
        OnPropertyChanged(nameof(RescueCooldownMinutes));
        OnPropertyChanged(nameof(RescueHelperDeathLimit));
        OnPropertyChanged(nameof(RescueHoldLimitMinutes));
        OnPropertyChanged(nameof(RescueUseBossLoadout));
    }

    private bool _acceptAllDrops;

    public bool AcceptAllDrops
    {
        get => _acceptAllDrops;
        set
        {
            if (SetProperty(ref _acceptAllDrops, value, true) && value)
                RejectAllDrops = false;
        }
    }

    private bool _rejectAllDrops;

    public bool RejectAllDrops
    {
        get => _rejectAllDrops;
        set
        {
            if (SetProperty(ref _rejectAllDrops, value, true) && value)
                AcceptAllDrops = false;
        }
    }

    [ObservableProperty]
    private bool _restPackets;

    [ObservableProperty]
    private bool _safeTimings = true;

    [CallBinding("skipCutscenes", UseValue = false, Get = false, HasSetter = true)]
    private bool _skipCutscenes;

    [ObservableProperty]
    private bool _privateRooms;

    [CallBinding("magnetize", UseValue = false, Get = false, HasSetter = true)]
    private bool _magnetise;

    private bool _lagKiller;
    private bool _userLagKiller;
    private bool _scriptRunning;

    public bool LagKiller
    {
        get => _lagKiller;
        set
        {
            if (!_scriptRunning && !_anyAntiLagApplied && !_suppressUserLagKillerSave && !_isInitializing && _settingsService != null)
            {
                _userLagKiller = value;
                _settingsService.Set("UserLagKiller", value);
            }
            else if (_scriptRunning && value)
            {
                OnLiteAntiLagTriggered();
            }

            try
            {
                var flash = _lazyFlash?.Value;
                if (flash != null)
                    flash.Call("killLag", value);
            }
            catch { }

            SetProperty(ref _lagKiller, value);
        }
    }

    [ObjectBinding("stage.frameRate", Get = false, HasSetter = true)]
    private int _setFPS = 30;

    [ObjectBinding("ui.mcFPS.visible", HasSetter = true)]
    private bool _showFPS = false;

    private readonly record struct RenderSyncKey(
        int TargetFps,
        double TargetVolume,
        string PrefQuality,
        string RuntimeQuality,
        bool ShouldFlush);

    private string _stageQuality = "MEDIUM";
    private bool _isForegroundFocused = true;
    private bool _isBackgroundThrottled;
    private bool _mutedByHeadless;
    private bool _renderHasPendingRequest;
    private bool _renderForceRuntimeQuality;
    private bool _renderFlushGamePrefPending;
    private bool _renderForceApplyPending;
    private bool _hasAppliedRenderKey;
    private RenderSyncKey _lastAppliedRenderKey;
    private DateTime _lastWorkingSetTrimUtc = DateTime.MinValue;
    private long _renderDesiredVersion;
    private bool _renderWorkerRunning;
    private CancellationTokenSource? _renderWaitCts;
    private readonly object _renderLock = new();

    /// <summary>
    /// Gets whether this client is currently the focused foreground tab and eligible for full stage quality rendering.
    /// </summary>
    public bool CanRenderFullQuality => !IsHeadlessNow() && !_isBackgroundThrottled && _isForegroundFocused;

    /// <summary>
    /// Sets whether this window/tab is currently the focused foreground tab.
    /// </summary>
    public void SetForegroundFocus(bool isForegroundFocused)
    {
        lock (_renderLock)
        {
            if (_isForegroundFocused == isForegroundFocused)
                return;

            _isForegroundFocused = isForegroundFocused;
        }

        RequestRenderStateSync(flushGamePref: false);
    }

    /// <summary>
    /// Sets whether this client is throttled because it is a background tab.
    /// </summary>
    public void SetBackgroundThrottle(bool throttled)
    {
        lock (_renderLock)
        {
            if (_isBackgroundThrottled == throttled)
                return;

            _isBackgroundThrottled = throttled;
        }

        RequestRenderStateSync(flushGamePref: false);
    }

    /// <summary>
    /// Applies Flash stage quality and syncs AQW's in-game Visuals preference:
    /// LOW if headless or a background tab; configured StageQuality if foreground focused.
    /// </summary>
    public void ApplyStageQuality(bool flushGamePref = false) =>
        RequestRenderStateSync(flushGamePref: flushGamePref, forceApply: true);

    /// <summary>
    /// Re-enforces headless/throttled render limits and Flash stage quality.
    /// </summary>
    public void EnforceRenderState(bool flushGamePref = false) =>
        RequestRenderStateSync(flushGamePref: flushGamePref, forceApply: true);

    /// <summary>
    /// Gets or sets the Flash rendering stage quality (LOW, MEDIUM, HIGH, AUTO).
    /// </summary>
    public string StageQuality
    {
        get => _stageQuality;
        set
        {
            string val = NormalizeStageQuality(value);
            if (_stageQuality == val)
                return;

            SetProperty(ref _stageQuality, val);
            PersistGlobalStageQuality(val);

            RequestRenderStateSync(flushGamePref: true, forceApply: true);

            BroadcastSharedCommandToOtherClients("stageQuality", nameof(StageQuality), val);
        }
    }

    /// <summary>
    /// Updates global StageQuality after the user changes Visuals inside the game's Options UI.
    /// </summary>
    public void SyncStageQualityFromGame(string? quality)
    {
        string val = NormalizeStageQuality(quality);
        if (_stageQuality == val)
        {
            RequestRenderStateSync(flushGamePref: false, triggeredFromGameUi: true, forceApply: true);
            return;
        }

        SetProperty(ref _stageQuality, val);
        PersistGlobalStageQuality(val);

        RequestRenderStateSync(flushGamePref: false, triggeredFromGameUi: true, forceApply: true);

        BroadcastSharedCommandToOtherClients("stageQuality", nameof(StageQuality), val);
    }

    /// <summary>
    /// Applies a global StageQuality update received from a sibling client without re-broadcasting.
    /// </summary>
    public void ApplySharedStageQuality(string? quality)
    {
        string val = NormalizeStageQuality(quality);
        if (_stageQuality == val)
            return;

        SetProperty(ref _stageQuality, val);
        PersistGlobalStageQuality(val);
        RequestRenderStateSync(flushGamePref: true, forceApply: true);
    }

    private static string NormalizeStageQuality(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "MEDIUM";

        string upper = value.Trim().ToUpperInvariant();
        return upper switch
        {
            "LOW" => "LOW",
            "MEDIUM" => "MEDIUM",
            "HIGH" => "HIGH",
            "AUTO" => "AUTO",
            _ => "MEDIUM"
        };
    }

    private string ReadGlobalStageQuality()
    {
        try
        {
            string? saved = _settingsService?.Get<string>("StageQuality");
            if (!string.IsNullOrWhiteSpace(saved))
                return NormalizeStageQuality(saved);

            return NormalizeStageQuality(_settingsService?.GetShared()?.StageQuality);
        }
        catch
        {
            return "MEDIUM";
        }
    }

    private void PersistGlobalStageQuality(string quality)
    {
        try
        {
            _settingsService?.Set("StageQuality", quality);
            var shared = _settingsService?.GetShared();
            if (shared is not null)
                shared.StageQuality = quality;
        }
        catch { }
    }

    private static void BroadcastSharedCommandToOtherClients(string kind, string name, string value)
    {
        try
        {
            int myPid = Environment.ProcessId;
            foreach (var other in Skua.Core.Services.OptionSharing.OtherClients(myPid))
            {
                Skua.Core.Services.OptionSharing.Send(other.ProcessId, new Skua.Core.Services.SharedOptionCommand(kind, name, value));
            }
        }
        catch { }
    }

    [ObservableProperty]
    private bool _aggroMonsters;

    [ObservableProperty]
    private bool _aggroAllMonsters;

    [CallBinding("infiniteRange", UseValue = false, Get = false, HasSetter = true)]
    private bool _infiniteRange;

    [ModuleBinding("DisableFX")]
    private bool _disableFX;

    [ObservableProperty]
    private bool _autoRelogin;

    [ObservableProperty]
    private bool _autoReloginAny;

    [ObservableProperty]
    private bool _retryRelogin = true;

    [ObservableProperty]
    private bool _safeRelogin;

    // Deliberately not on IScriptOption: it is a UI setting only, scripts don't get a handle on it
    [ObservableProperty]
    private bool _reloginFallback;

    [ModuleBinding("DisableCollisions")]
    private bool _disableCollisions;

    [CallBinding("disableDeathAd", Get = false, HasSetter = true)]
    private bool _disableDeathAds;

    [ObservableProperty]
    private bool _headlessMode;

    [ObservableProperty]
    private bool _autoHeadless;
    [ObservableProperty]
    private bool _autoHeadlessBackgroundTabs = true;
    [ObservableProperty]
    private bool _autoHeadlessGridView = true;
    [ObservableProperty]
    private bool _autoHeadlessWhenMinimized = true;
    [ObservableProperty]
    private bool _autoHeadlessLiftOnFocus = true;

    public const string AutoHeadlessActiveChanged = "AutoHeadlessActive";
    private bool _autoHeadlessActive;
    public bool IsHeadlessNow() => _headlessMode || _autoHeadlessActive;

    public void SetAutoHeadless(bool active)
    {
        bool before;
        bool nowHeadless;
        lock (_renderLock)
        {
            if (_autoHeadlessActive == active)
                return;

            before = IsHeadlessNow();
            _autoHeadlessActive = active;
            nowHeadless = IsHeadlessNow();
        }

        // Notify UI before dispatching Flash IPC so XMapWindow(_player) and Avalonia repaints never wait on a blocked player.
        OnPropertyChanged(AutoHeadlessActiveChanged);
        if (before != nowHeadless)
            RequestRenderStateSync(flushGamePref: false);
    }

    partial void OnHeadlessModeChanged(bool value) => RequestRenderStateSync(flushGamePref: false);

    private void RequestRenderStateSync(bool flushGamePref, bool triggeredFromGameUi = false, bool forceApply = false)
    {
        bool startWorker = false;
        CancellationTokenSource? toCancel = null;

        lock (_renderLock)
        {
            if (flushGamePref)
                _renderFlushGamePrefPending = true;
            if (forceApply)
                _renderForceApplyPending = true;

            if (!_renderHasPendingRequest || !triggeredFromGameUi)
                _renderForceRuntimeQuality = !triggeredFromGameUi;
            _renderHasPendingRequest = true;

            _renderDesiredVersion++;

            // Cancel any pending settle debounce or retry sleep so state transitions proceed promptly,
            // while never aborting an already-transmitted TCP request (avoids flooding Mugetsu).
            toCancel = _renderWaitCts;

            if (!_renderWorkerRunning)
            {
                _renderWorkerRunning = true;
                startWorker = true;
            }
        }

        try { toCancel?.Cancel(); } catch (ObjectDisposedException) { }

        if (startWorker)
            _ = Task.Run(RunRenderStateSyncLoopAsync);
    }

    private async Task RunRenderStateSyncLoopAsync()
    {
        long lastRetryVersion = -1;
        int retriesForVersion = 0;

        while (true)
        {
            long version;
            bool isHeadless;
            bool isReduced;
            bool shouldDebounceReduced;
            RenderSyncKey key;
            bool forceApply;
            CancellationTokenSource? waitCts = null;

            lock (_renderLock)
            {
                version = _renderDesiredVersion;
                isHeadless = IsHeadlessNow();
                isReduced = isHeadless || _isBackgroundThrottled;
                bool allowFullQuality = !isReduced && _isForegroundFocused;

                int targetFps = isReduced ? 2 : (SetFPS > 2 ? SetFPS : 30);
                double targetVolume = isHeadless ? 0.0 : (_mutedByHeadless ? 1.0 : -1.0);

                bool isAuto = string.Equals(StageQuality, "AUTO", StringComparison.OrdinalIgnoreCase);
                string prefQuality = !allowFullQuality && isAuto ? "LOW" : StageQuality;
                bool fromGameUi = _renderHasPendingRequest && !_renderForceRuntimeQuality;
                string runtimeQuality = fromGameUi
                    ? string.Empty
                    : (allowFullQuality ? StageQuality : "LOW");
                bool shouldFlush = !fromGameUi
                    && _renderFlushGamePrefPending
                    && string.Equals(prefQuality, StageQuality, StringComparison.OrdinalIgnoreCase);

                key = new RenderSyncKey(targetFps, targetVolume, prefQuality, runtimeQuality, shouldFlush);
                forceApply = _renderForceApplyPending;

                if (!forceApply && _hasAppliedRenderKey && key.Equals(_lastAppliedRenderKey))
                {
                    _renderHasPendingRequest = false;
                    _renderForceRuntimeQuality = false;
                    _renderWorkerRunning = false;
                    return;
                }

                // Debounce entry into reduced/headless state slightly so rapid tab switching does not
                // churn FPS=2 / LOW quality while switching back and forth.
                shouldDebounceReduced = isReduced && version != lastRetryVersion && _hasAppliedRenderKey;
                if (shouldDebounceReduced)
                {
                    waitCts = new CancellationTokenSource();
                    _renderWaitCts = waitCts;
                }
                else
                {
                    _renderHasPendingRequest = false;
                    _renderForceRuntimeQuality = false;
                    _renderForceApplyPending = false;
                }
            }

            if (waitCts != null)
            {
                try
                {
                    await Task.Delay(220, waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                finally
                {
                    lock (_renderLock)
                    {
                        if (ReferenceEquals(_renderWaitCts, waitCts))
                            _renderWaitCts = null;
                    }
                    waitCts.Dispose();
                }

                lock (_renderLock)
                {
                    if (_renderDesiredVersion != version)
                    {
                        lastRetryVersion = version;
                        retriesForVersion = 0;
                        continue;
                    }

                    // Settle period passed with same reduced state; mark this version as settled and execute.
                    lastRetryVersion = version;
                    retriesForVersion = 0;
                    _renderHasPendingRequest = false;
                    _renderForceRuntimeQuality = false;
                    _renderForceApplyPending = false;
                }
            }
            else if (version != lastRetryVersion)
            {
                lastRetryVersion = version;
                retriesForVersion = 0;
            }

            if (isHeadless)
            {
                lock (_renderLock)
                    _mutedByHeadless = true;
            }

            bool applied = false;
            try
            {
                var flash = _lazyFlash.Value;
                if (flash != null)
                {
                    TimeSpan timeout = TimeSpan.FromMilliseconds(isReduced ? 650 : 900);
                    applied = flash.TryCall<bool>(
                        "syncRenderState",
                        timeout,
                        CancellationToken.None,
                        out bool ok,
                        key.TargetFps, key.TargetVolume, key.PrefQuality, key.RuntimeQuality, key.ShouldFlush) && ok;
                }
            }
            catch { }

            if (isHeadless && DateTime.UtcNow - _lastWorkingSetTrimUtc >= TimeSpan.FromSeconds(60))
            {
                _lastWorkingSetTrimUtc = DateTime.UtcNow;
                _ = Task.Delay(2000).ContinueWith(_ =>
                {
                    if (IsHeadlessNow())
                    {
                        try { Skua.Core.Utils.MemoryUtils.TrimWorkingSet(); } catch { }
                    }
                });
            }

            bool shouldRetry = false;
            int retryDelayMs = 0;
            CancellationTokenSource? retryWaitCts = null;

            lock (_renderLock)
            {
                if (applied)
                {
                    _hasAppliedRenderKey = true;
                    _lastAppliedRenderKey = key;
                    if (key.ShouldFlush)
                        _renderFlushGamePrefPending = false;
                    if (!isHeadless && key.TargetVolume >= 0)
                        _mutedByHeadless = false;
                }
                else
                {
                    _hasAppliedRenderKey = false;
                }

                bool superseded = _renderDesiredVersion != version;
                if (!superseded)
                {
                    if (!applied && !isReduced && retriesForVersion < 5)
                    {
                        retriesForVersion++;
                        retryDelayMs = retriesForVersion switch
                        {
                            1 => 120,
                            2 => 200,
                            3 => 300,
                            4 => 400,
                            _ => 500
                        };
                        retryWaitCts = new CancellationTokenSource();
                        _renderWaitCts = retryWaitCts;
                        shouldRetry = true;
                    }
                    else
                    {
                        _renderWorkerRunning = false;
                        return;
                    }
                }
            }

            if (shouldRetry && retryWaitCts != null)
            {
                try
                {
                    await Task.Delay(retryDelayMs, retryWaitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                finally
                {
                    lock (_renderLock)
                    {
                        if (ReferenceEquals(_renderWaitCts, retryWaitCts))
                            _renderWaitCts = null;
                    }
                    retryWaitCts.Dispose();
                }
            }
        }
    }

    private bool _streamerMode;
    public bool StreamerMode
    {
        get => _streamerMode;
        set
        {
            if (SetProperty(ref _streamerMode, value, true))
            {
                ApplyStreamerMode(value);
                if (!_isInitializing)
                    Save();
            }
        }
    }

    private string _originalName = string.Empty;
    private string _originalGuild = string.Empty;
    private bool _originalHidePlayers = false;
    private CancellationTokenSource? _streamerModeCts;

    // Retained for compatibility with BonxieMux's settings interface; Streamer Mode
    // uses BonxieMux's fixed anonymized display name.
    [ObservableProperty]
    private string _streamerName = string.Empty;

    private void ApplyStreamerMode(bool enabled)
    {
        if (enabled)
        {
            if (CustomName != "Hidden" && CustomName != "BonxieMuxUser")
                _originalName = CustomName;
            if (CustomGuild != "Hidden")
                _originalGuild = CustomGuild;

            _originalHidePlayers = HidePlayers;
            HidePlayers = true;
            CustomName = "BonxieMuxUser";
            CustomGuild = "Hidden";

            _streamerModeCts?.Cancel();
            _streamerModeCts?.Dispose();
            _streamerModeCts = new CancellationTokenSource();
            var token = _streamerModeCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var flash = _lazyFlash.Value;
                        if (flash == null) return;
                        // Override visuals only, NEVER overwrite objData as it breaks shops/inventory
                        flash.Call("setGameObject", "world.myAvatar.pMC.pname.ti.text", "BonxieMuxUser");
                        flash.Call("setGameObject", "world.myAvatar.pMC.pname.tg.text", "Hidden");
                        flash.Call("setGameObject", "world.rootClass.ui.mcPortrait.strName.text", "BonxieMuxUser");
                        flash.Call("setGameObject", "world.rootClass.ui.mcPortraitTarget.strName.text", "Hidden");

                        // Hide Gold/Coins visually instead of wiping the client's cached integer amounts
                        flash.Call("setGameObject", "world.rootClass.ui.mcInterface.teGold.visible", false);
                        flash.Call("setGameObject", "world.rootClass.ui.mcInterface.teCoins.visible", false);
                        flash.Call("setGameObject", "world.rootClass.ui.mcInterface.strGold.visible", false);
                        flash.Call("setGameObject", "world.rootClass.ui.mcInterface.strCoins.visible", false);
                        flash.Call("setGameObject", "world.rootClass.ui.mcInterface.txtGold.visible", false);
                        flash.Call("setGameObject", "world.rootClass.ui.mcInterface.txtCoins.visible", false);

                        flash.Call("setGameObject", "world.rootClass.ui.mcInterface.t1.visible", false);
                        flash.Call("setGameObject", "world.rootClass.ui.mcInterface.te.visible", false);

                        var rawMapName = flash.GetGameObject("world.strMapName") ?? "";
                        if (!string.IsNullOrEmpty(rawMapName) && rawMapName != "null")
                        {
                            string cleanMapName = rawMapName.Length > 1
                                ? char.ToUpper(rawMapName[0]) + rawMapName.Substring(1).ToLower()
                                : rawMapName.ToUpper();
                            flash.Call("setGameObject", "ui.mcInterface.areaList.title.t1.text", cleanMapName);
                        }
                    }
                    catch { }
                    try
                    {
                        await Task.Delay(500, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, token);
        }
        else
        {
            _streamerModeCts?.Cancel();
            _streamerModeCts?.Dispose();
            _streamerModeCts = null;

            CustomName = (_originalName == "BonxieMuxUser" || _originalName == "Hidden") ? string.Empty : _originalName;
            CustomGuild = (_originalGuild == "Hidden") ? string.Empty : _originalGuild;
            HidePlayers = _originalHidePlayers;

            try
            {
                var flash = _lazyFlash.Value;
                if (flash != null)
                {
                    flash.Call("setGameObject", "world.rootClass.ui.mcInterface.t1.visible", true);
                    flash.Call("setGameObject", "world.rootClass.ui.mcInterface.te.visible", true);
                    flash.Call("setGameObject", "world.rootClass.ui.mcInterface.teGold.visible", true);
                    flash.Call("setGameObject", "world.rootClass.ui.mcInterface.teCoins.visible", true);
                    flash.Call("setGameObject", "world.rootClass.ui.mcInterface.strGold.visible", true);
                    flash.Call("setGameObject", "world.rootClass.ui.mcInterface.strCoins.visible", true);
                    flash.Call("setGameObject", "world.rootClass.ui.mcInterface.txtGold.visible", true);
                    flash.Call("setGameObject", "world.rootClass.ui.mcInterface.txtCoins.visible", true);
                    flash.CallGameFunction("world.setUserData");
                }
            }
            catch { }

        }
    }

    [ObservableProperty]
    private bool _useFunctionBasedSkills;

    [ModuleBinding("OptimizePlayers")]
    private bool _optimizePlayers = true;

    [ModuleBinding("HidePlayers")]
    private bool _hidePlayers;

    [ModuleBinding("DamageNumbers")]
    private bool _damageNumbers = true;

    [CallBinding("setCompactNumbers", Get = false, HasSetter = true)]
    private bool _compactNumbers;

    [CallBinding("setUnitFrameHealth", Get = false, HasSetter = true)]
    private bool _unitFrameHealth = true;

    [CallBinding("setMovableTargetFrame", Get = false, HasSetter = true)]
    private bool _movableTargetFrame = true;

    [ObservableProperty]
    private bool _autoCrowdMode = true;

    private string? _reloginServer = "Twilly";

    public string? ReloginServer
    {
        get => _reloginServer;
        set => SetProperty(ref _reloginServer, value, true);
    }

    [ObjectBinding("world.myAvatar.objData.strUsername", "world.rootClass.ui.mcPortrait.strName.text", "world.myAvatar.pMC.pname.ti.text", Get = false, HasSetter = true, Default = "string.Empty")]
    private string _customName = string.Empty;

    [ObjectBinding("world.myAvatar.pMC.pname.ti.textColor", Get = false, HasSetter = true, Default = "0xFFFFFF")]
    private int _nameColor = 0xFFFFFF;

    [ObjectBinding("world.myAvatar.pMC.pname.tg.text", Get = false, HasSetter = true, Default = "string.Empty")]
    private string _customGuild = string.Empty;

    [ObjectBinding("world.myAvatar.pMC.pname.tg.textColor", Get = false, HasSetter = true, Default = "0xFFFFFF")]
    private int _guildColor = 0xFFFFFF;

    [ObjectBinding("world.WALKSPEED", Get = false, HasSetter = true, Default = "8")]
    private int _walkSpeed = 8;

    [ObservableProperty]
    private int _loadTimeout = 30000;

    [ObservableProperty]
    private int _huntDelay = 1000;

    [ObservableProperty]
    private int _huntBuffer = 1;

    [ObservableProperty]
    private int _maximumTries = 10;

    [ObservableProperty]
    private int _actionDelay = 800;

    [ObservableProperty]
    private int _privateNumber = 0;

    [ObservableProperty]
    private int _joinMapTries = 3;

    [ObservableProperty]
    private int _questAcceptAndCompleteTries = 30;

    [ObservableProperty]
    private int _reloginTries = 5;

    [ObservableProperty]
    private int _reloginTryDelay = 800;

    [ObservableProperty]
    private int _loginTimeout = 30000;

    [ObservableProperty]
    private HuntPriorities _HuntPriority = HuntPriorities.None;

    public void Save()
    {
        StringCollection saveOptions = new();
        foreach (PropertyInfo pi in GetType().GetProperties())
        {
            if (pi.Name is nameof(OptionDictionary) or nameof(StageQuality) or nameof(AcceptACDrops) or nameof(CustomName) or nameof(CustomGuild) or nameof(NameColor) or nameof(GuildColor) or nameof(RescueAllies) or nameof(AllowThisAccountToRescue) or nameof(RescueDelayMinutes) or nameof(RescueDeathsBeforeCalling) or nameof(RescueCooldownEnabled) or nameof(RescueCooldownMinutes) or nameof(RescueHelperDeathLimit) or nameof(RescueHoldLimitMinutes) or nameof(RescueUseBossLoadout))
                continue;
            string key = pi.Name;
            object? value = pi.GetValue(this);
            saveOptions.Add($"{key}={value}");
        }
        _settingsService.Set("UserOptions", saveOptions);
    }

    public void Reset()
    {
        if (!_isInitializing && _settingsService is not null)
        {
            _settingsService.ReloadSettings();
            ApplySharedAcceptACDrops(ReadGlobalAcceptACDrops());
            ApplySharedStageQuality(ReadGlobalStageQuality());
        }

        if (_userOptions is null)
        {
            ResetToDefault();
            return;
        }

        foreach (PropertyInfo pi in GetType().GetProperties())
        {
            if (!pi.CanWrite || (_scriptRunning && pi.Name == nameof(LagKiller)) || pi.Name is nameof(CanRenderFullQuality) or nameof(StageQuality) or nameof(AcceptACDrops) or nameof(OptionDictionary) or nameof(CustomName) or nameof(CustomGuild) or nameof(NameColor) or nameof(GuildColor) or nameof(RescueAllies) or nameof(AllowThisAccountToRescue) or nameof(RescueDelayMinutes) or nameof(RescueDeathsBeforeCalling) or nameof(RescueCooldownEnabled) or nameof(RescueCooldownMinutes) or nameof(RescueHelperDeathLimit) or nameof(RescueHoldLimitMinutes) or nameof(RescueUseBossLoadout))
                continue;
            if (_userOptions.ContainsKey(pi.Name))
            {
                if (pi.PropertyType.BaseType == typeof(Enum))
                {
                    pi.SetValue(this, Enum.Parse(pi.PropertyType, _userOptions[pi.Name], true));
                    continue;
                }
                pi.SetValue(this, Convert.ChangeType(_userOptions[pi.Name], pi.PropertyType));
            }
        }
    }

    public void ResetToDefault()
    {
        ScriptOption defaults = new(_lazyFlash);
        foreach (PropertyInfo pi in GetType().GetProperties())
        {
            if (!pi.CanWrite || (_scriptRunning && pi.Name == nameof(LagKiller)) || pi.Name is nameof(CanRenderFullQuality) or nameof(StageQuality) or nameof(AcceptACDrops) or nameof(OptionDictionary) or nameof(CustomName) or nameof(CustomGuild) or nameof(NameColor) or nameof(GuildColor) or nameof(RescueAllies) or nameof(AllowThisAccountToRescue) or nameof(RescueDelayMinutes) or nameof(RescueDeathsBeforeCalling) or nameof(RescueCooldownEnabled) or nameof(RescueCooldownMinutes) or nameof(RescueHelperDeathLimit) or nameof(RescueHoldLimitMinutes) or nameof(RescueUseBossLoadout))
                continue;

            pi.SetValue(this, pi.GetValue(defaults), null);
        }
    }

    private void GetOptions()
    {
        StringCollection? userOptions = _settingsService.Get<StringCollection>("UserOptions");
        if (userOptions is null)
            return;
        _userOptions = new();
        foreach (string? option in userOptions)
        {
            if (string.IsNullOrEmpty(option))
                continue;
            string[] optionKeyValue = option.Split('=', StringSplitOptions.TrimEntries);
            _userOptions.Add(optionKeyValue[0], optionKeyValue[1]);
        }
        Reset();
    }

    private Dictionary<string, Func<object>> GenerateDictionary()
    {
        Dictionary<string, Func<object>> dict = new();
        foreach (PropertyInfo pi in GetType().GetProperties())
        {
            if (pi.Name == nameof(OptionDictionary) || pi.GetGetMethod() == null)
                continue;
            MethodCallExpression methodCall = Expression.Call(Expression.Constant(this), pi.GetGetMethod()!, null);
            UnaryExpression convertedExpression = Expression.Convert(methodCall, typeof(object));
            Func<object> function = Expression.Lambda<Func<object>>(convertedExpression).Compile();
            dict.Add(pi.Name, function);
        }
        return dict;
    }
}
