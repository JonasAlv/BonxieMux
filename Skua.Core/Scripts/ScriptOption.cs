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
        GetOptions();
        _pickupACDropsAndSendToBank = settingsService?.Get<bool>("BonxieMux.PickupACDropsAndSendToBank") ?? false;
        _rejectNonACDrops = settingsService?.Get<bool>("BonxieMux.RejectNonACDrops") ?? false;
        OptionDictionary = GenerateDictionary().ToImmutableDictionary();
        StrongReferenceMessenger.Default.Register<ScriptOption, ScriptStartedMessage, int>(this, (int)MessageChannels.ScriptStatus, ScriptStarted);
        StrongReferenceMessenger.Default.Register<ScriptOption, ScriptStoppedMessage, int>(this, (int)MessageChannels.ScriptStatus, ScriptStopped);
        StrongReferenceMessenger.Default.Register<ScriptOption, MapChangedMessage, int>(this, (int)MessageChannels.GameEvents, MapChanged);
        _isInitializing = false;
    }

    private void ScriptStarted(ScriptOption recipient, ScriptStartedMessage message)
    {
        recipient._scriptRunning = true;
        recipient._userLagKiller = recipient._lagKiller;

        if (recipient.AutoCrowdMode)
        {
            try
            {
                var flash = recipient._lazyFlash.Value;
                if (flash != null)
                {
                    string current = flash.GetGameObject<string>("litePreference.data.sCrowd") ?? "off";
                    recipient._priorCrowdMode = current;
                    recipient.ApplyCrowdMode(true);
                }
            }
            catch { }
        }

        if (recipient.StreamerMode)
            recipient.ApplyStreamerMode(true);
    }

    private void MapChanged(ScriptOption recipient, MapChangedMessage message)
    {
        if (recipient.AutoCrowdMode && recipient._priorCrowdMode != null)
        {
            Task.Run(async () =>
            {
                await Task.Delay(500);
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
    }

    private void ScriptStopped(ScriptOption recipient, ScriptStoppedMessage message)
    {
        recipient.AutoRelogin = false;
        recipient._scriptRunning = false;
        recipient.LagKiller = recipient._userLagKiller;
        recipient.AggroAllMonsters = false;
        recipient.AggroMonsters = false;
        recipient.SkipCutscenes = false;

        if (recipient.AutoCrowdMode)
        {
            try
            {
                string restore = !string.IsNullOrEmpty(recipient._priorCrowdMode)
                    ? recipient._priorCrowdMode
                    : "off";

                recipient.ApplyCrowdMode(restore == "on", restore);
                recipient._priorCrowdMode = null;
            }
            catch { }
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
        set => SetProperty(ref _acceptACDrops, value, true);
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
            if (!_scriptRunning && !_isInitializing && _settingsService != null)
            {
                _userLagKiller = value;
                _settingsService.Set("UserLagKiller", value);
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
        if (_autoHeadlessActive == active) return;
        bool before = IsHeadlessNow();
        _autoHeadlessActive = active;
        if (before != IsHeadlessNow()) ApplyHeadless(IsHeadlessNow());
        OnPropertyChanged(AutoHeadlessActiveChanged);
    }

    partial void OnHeadlessModeChanged(bool value) => ApplyHeadless(value || _autoHeadlessActive);

    private void ApplyHeadless(bool value)
    {
        try
        {
            _lazyFlash.Value.Call("headless", value);
            if (value)
            {
                _lazyFlash.Value.Call("setGameObject", "stage.frameRate", 2);
                _lazyFlash.Value.Call("setGameObject", "stage.quality", "LOW");
                _lazyFlash.Value.Call("world.rootClass.mixer.soundTransform.volume", 0);
                Skua.Core.Utils.MemoryUtils.TrimWorkingSet();
            }
            else
            {
                _lazyFlash.Value.Call("setGameObject", "stage.frameRate", SetFPS > 2 ? SetFPS : 30);
                _lazyFlash.Value.Call("setGameObject", "stage.quality", "HIGH");
                _lazyFlash.Value.Call("world.rootClass.mixer.soundTransform.volume", 1);
            }
        }
        catch { }
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

    [ObservableProperty]
    private bool _autoCrowdMode = true;

    private string? _priorCrowdMode;

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
            if (pi.Name is nameof(OptionDictionary) or nameof(CustomName) or nameof(CustomGuild) or nameof(NameColor) or nameof(GuildColor))
                continue;
            string key = pi.Name;
            object? value = pi.GetValue(this);
            saveOptions.Add($"{key}={value}");
        }
        _settingsService.Set("UserOptions", saveOptions);
    }

    public void Reset()
    {
        if (_userOptions is null)
        {
            ResetToDefault();
            return;
        }

        foreach (PropertyInfo pi in GetType().GetProperties())
        {
            if (pi.Name is nameof(OptionDictionary) or nameof(CustomName) or nameof(CustomGuild) or nameof(NameColor) or nameof(GuildColor))
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
            if (pi.Name is nameof(OptionDictionary) or nameof(CustomName) or nameof(CustomGuild) or nameof(NameColor) or nameof(GuildColor))
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
