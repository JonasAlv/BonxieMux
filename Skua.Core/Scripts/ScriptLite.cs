using Skua.Core.Flash;
using Skua.Core.Interfaces;

namespace Skua.Core.Scripts;

public partial class ScriptLite : IScriptLite
{
    private readonly Lazy<IFlashUtil> _lazyFlash;
    private IFlashUtil Flash => _lazyFlash.Value;

    public ScriptLite(Lazy<IFlashUtil> flash)
    {
        _lazyFlash = flash;
    }

    public bool QuestLogTurnIns
    {
        get => _questLogTurnIn;
        set => _questLogTurnIn = value;
    }

    [ObjectBinding("litePreference.data.bDebugger", HasSetter = true)]
    private bool _debugger;

    [ObjectBinding("litePreference.data.dOptions[\"debugPacket\"]", HasSetter = true)]
    private bool _debugPacket;

    [ObjectBinding("litePreference.data.bHideUI", HasSetter = true)]
    private bool _hideUI;

    [ObjectBinding("litePreference.data.bMonsType", HasSetter = true)]
    private bool _showMonsterType;

    [ObjectBinding("litePreference.data.bSmoothBG", HasSetter = true)]
    private bool _smoothBackground;

    [ObjectBinding("litePreference.data.bUntargetSelf", HasSetter = true)]
    private bool _untargetSelf;

    [ObjectBinding("litePreference.data.bUntargetDead", HasSetter = true)]
    private bool _untargetDead;

    [ObjectBinding("litePreference.data.bCustomDrops", HasSetter = true)]
    private bool _customDropsUI;

    [ObjectBinding("litePreference.data.bQuestLog", HasSetter = true)]
    private bool _questLogTurnIn;

    [ObjectBinding("litePreference.data.bBattlepet", HasSetter = true)]
    private bool _battlePets;

    [ObjectBinding("litePreference.data.bCachePlayers", HasSetter = true)]
    private bool _staticPlayerArt;

    [ObjectBinding("litePreference.data.bChatFilter", HasSetter = true)]
    private bool _chatFilter;

    [ObjectBinding("litePreference.data.bChatUI", HasSetter = true)]
    private bool _chatUI;

    [ObjectBinding("litePreference.data.bAuras", HasSetter = true)]
    private bool _aurasUI;

    [ObjectBinding("litePreference.data.bColorSets", HasSetter = true)]
    private bool _colorSets;

    [ObjectBinding("litePreference.data.bDraggable", HasSetter = true)]
    private bool _draggableDrops;

    [ObjectBinding("litePreference.data.bDisChatScroll", HasSetter = true)]
    private bool _disableChatScrolling;

    [ObjectBinding("litePreference.data.bDisDmgDisplay", HasSetter = true)]
    private bool _disableDamageNumbers;

    [ObjectBinding("litePreference.data.bDisSoundFX", HasSetter = true)]
    private bool _disableSoundFx;

    [ObjectBinding("litePreference.data.bDisQPopup", HasSetter = true)]
    private bool _disableQuestPopup;

    [ObjectBinding("litePreference.data.bDisQTracker", HasSetter = true)]
    private bool _disableQuestTracker;

    [ObjectBinding("litePreference.data.bQuestPin", HasSetter = true)]
    private bool _questPinner;

    [ObjectBinding("litePreference.data.bQuestNotif", HasSetter = true)]
    private bool _questProgressNotifications;

    [ObjectBinding("litePreference.data.bSkillCD", HasSetter = true)]
    private bool _visualSkillCooldowns;

    [ObjectBinding("litePreference.data.bDisGround", HasSetter = true)]
    private bool _hideGroundItems;

    [ObjectBinding("litePreference.data.bDisHealBubble", HasSetter = true)]
    private bool _hideHealingBubbles;

    [ObjectBinding("litePreference.data.bDisAuraAnim", HasSetter = true)]
    private bool _disableAuraAnimations;

    [ObjectBinding("litePreference.data.bHideNames", HasSetter = true)]
    private bool _hidePlayerNames;

    [ObjectBinding("litePreference.data.bDisDmgStrobe", HasSetter = true)]
    private bool _disableDamageStrobe;

    [ObjectBinding("litePreference.data.bDisMonAnim", HasSetter = true)]
    private bool _disableMonsterAnimation;

    [ObjectBinding("litePreference.data.bDisSelfMAnim", HasSetter = true)]
    private bool _disableSelfAnimation;

    [ObjectBinding("litePreference.data.bDisSkillAnim", HasSetter = true)]
    private bool _disableSkillAnimation;

    [ObjectBinding("litePreference.data.bDisWepAnim", HasSetter = true)]
    private bool _disableWeaponAnimation;

    [ObjectBinding("litePreference.data.bFreezeMons", HasSetter = true)]
    private bool _freezeMonsterPosition;

    [ObjectBinding("litePreference.data.bHideMons", HasSetter = true)]
    private bool _invisibleMonsters;

    [ObjectBinding("litePreference.data.bHidePlayers", HasSetter = true)]
    private bool _hidePlayers;

    [ObjectBinding("litePreference.data.bReaccept", HasSetter = true)]
    private bool _reacceptQuest;

    [ObjectBinding("litePreference.data.bCharSelect", HasSetter = true)]
    private bool _characterSelectScreen;

    [ObjectBinding("litePreference.data.dOptions[\"disRed\"]", HasSetter = true)]
    private bool _disableRedWarning;

    [ObjectBinding("litePreference.data.dOptions[\"vanishMsg\"]", HasSetter = true)]
    private bool _vanishingMessages;

    [ObjectBinding("litePreference.data.dOptions[\"timeStamps\"]", HasSetter = true)]
    private bool _timestamps;

    [ObjectBinding("litePreference.data.dOptions[\"disBlue\"]", HasSetter = true)]
    private bool _disableBlueMessages;

    [ObjectBinding("litePreference.data.dOptions[\"chatMinimal\"]", HasSetter = true)]
    private bool _minimalChatMode;

    [ObjectBinding("litePreference.data.dOptions[\"chatScroll\"]", HasSetter = true)]
    private bool _disableAutoScrollToBottom;

    [ObjectBinding("litePreference.data.dOptions[\"disAuraTips\"]", HasSetter = true)]
    private bool _disableAuraTooltips;

    [ObjectBinding("litePreference.data.dOptions[\"disAuraText\"]", HasSetter = true)]
    private bool _disableAuraText;

    [ObjectBinding("litePreference.data.dOptions[\"invertMenu\"]", HasSetter = true)]
    private bool _invertMenu;

    [ObjectBinding("litePreference.data.dOptions[\"warnDecline\"]", HasSetter = true)]
    private bool _warnWhenDecliningDrop;

    [ObjectBinding("litePreference.data.dOptions[\"hideDrop\"]", HasSetter = true)]
    private bool _hideDropNotifications;

    [ObjectBinding("litePreference.data.dOptions[\"hideTemp\"]", HasSetter = true)]
    private bool _hideTemporaryDropNotifications;

    [ObjectBinding("litePreference.data.dOptions[\"openMenu\"]", HasSetter = true)]
    private bool _openedMenu;

    [ObjectBinding("litePreference.data.dOptions[\"dragMode\"]", HasSetter = true)]
    private bool _dragMode;

    [ObjectBinding("litePreference.data.dOptions[\"lockMode\"]", HasSetter = true)]
    private bool _lockPosition;

    [ObjectBinding("litePreference.data.dOptions[\"termsAgree\"]", HasSetter = true)]
    private bool _quantityWarnings;

    [ObjectBinding("litePreference.data.dOptions[\"animSelf\"]", HasSetter = true)]
    private bool _showYourSkillAnimationsOnly;

    [ObjectBinding("litePreference.data.dOptions[\"wepSelf\"]", HasSetter = true)]
    private bool _keepYourWeaponAnimationsOnly;

    [ObjectBinding("litePreference.data.dOptions[\"showNames\"]", HasSetter = true)]
    private bool _showNameTags;

    [ObjectBinding("litePreference.data.dOptions[\"showShadows\"]", HasSetter = true)]
    private bool _showShadows;

    [ObjectBinding("litePreference.data.dOptions[\"hideGuild\"]", HasSetter = true)]
    private bool _hideGuildNamesOnly;

    [ObjectBinding("litePreference.data.dOptions[\"hideSelf\"]", HasSetter = true)]
    private bool _hideYourNameOnly;

    [ObjectBinding("litePreference.data.dOptions[\"groundSelf\"]", HasSetter = true)]
    private bool _showYourGroundItemOnly;

    [ObjectBinding("litePreference.data.dOptions[\"auraAnimSelf\"]", HasSetter = true)]
    private bool _showYourAuraAnimationOnly;

    public T? Get<T>(string optionName)
    {
        return Flash.GetGameObject<T>($"litePreference.data.{optionName}");
    }

    public void Set<T>(string optionName, T value)
    {
        Flash.SetGameObject($"litePreference.data.{optionName}", value!);
    }
}