package skua.module {
import flash.display.DisplayObject;
import flash.display.DisplayObjectContainer;
import flash.display.Sprite;
import flash.display.Stage;
import flash.events.Event;
import flash.events.TimerEvent;
import flash.geom.Point;
import flash.utils.Timer;
import flash.utils.getQualifiedClassName;
import flash.utils.getTimer;

import skua.Main;
import skua.util.SFSEvent;

/**
 * High-Performance Floating Combat Text (Damage Numbers) module.
 * Ported from aqw-haxe-ui DamageNumbers.hx into standalone AS3 so it can be
 * toggled, customized, or deleted independently.
 */
public class DamageNumbers extends Module {
    private static var _instance:DamageNumbers = null;

    // -------------------------------------------------------------------------
    // Configuration (mirrors DamageNumbers.hx defaults; runtime-adjustable)
    // -------------------------------------------------------------------------
    public static var filterMode:int = 0; // 0 = Self & Target, 1 = Self Only, 2 = Target Only, 3 = All
    public static var compactFormat:Boolean = false;
    public static var showIncoming:Boolean = true;
    public static var showDots:Boolean = true;
    public static var showHeals:Boolean = true;
    public static var modernRewardsEnabled:Boolean = false;

    // Object pool
    private static const POOL_SIZE:int = 80;
    private static var _pool:Array = [];
    private static var _lastFrameTime:int = 0;

    // State
    private static var _container:Sprite = null;
    private static var _hookedStage:Stage = null;
    private static var _hookedSfc:* = null;
    private static var _ticker:Timer = null;
    private static var _savedNativeDmgDisplay:* = null;

    // Colors
    private static const COLOR_HIT:uint           = 0xFFFFFF; // Crisp white (native AQW: 16777215)
    private static const COLOR_CRIT:uint          = 0xFF9944; // Native AQW radiant orange (16750916)
    private static const COLOR_HEAL:uint          = 0x00FF8A; // Native AQW vibrant green (65450)
    private static const COLOR_DOT:uint           = 0xEE9900; // Native AQW DoT amber/orange (15636736)
    private static const COLOR_AVOID:uint         = 0xEF4444; // Native AQW red (Dodge/Miss/Parry/Block)
    private static const COLOR_AVOID_OUT:uint     = COLOR_AVOID;
    private static const COLOR_INCOMING_HIT:uint  = 0xEF4444; // Incoming hit taken by player (Crimson Red)
    private static const COLOR_INCOMING_CRIT:uint = 0xFF2D55; // Incoming crit taken by player (Ruby Crimson)
    private static const COLOR_INCOMING_DOT:uint  = 0xFB7185; // Incoming DoT taken by player (Soft Crimson Rose)
    private static const COLOR_MUTED:uint         = 0x8E95AA; // Immune / Resist / 0 dmg (Clean slate gray)
    private static const COLOR_XP:uint            = 0xC084FC; // Radiant purple/lavender
    private static const COLOR_GOLD:uint          = 0xFACC15; // Vibrant gold

    public function DamageNumbers() {
        super("DamageNumbers");
        _instance = this;
        enabled = true;
    }

    override public function onToggle(game:*):void {
        if (enabled) {
            ensureInitialized(game);
            if (_ticker != null && !_ticker.running) {
                _lastFrameTime = getTimer();
                _ticker.start();
            }
            onFrame(game);
        } else {
            clearActiveParticles();
            if (_container != null) {
                _container.visible = false;
            }
            if (_ticker != null && _ticker.running) {
                _ticker.stop();
            }
            syncNativeDisplay(game, false);
        }
    }

    override public function onFrame(game:*):void {
        if (!enabled) {
            return;
        }
        ensureInitialized(game);
        ensureSocketHook(game);

        var active:Boolean = isInGame(game);
        syncNativeDisplay(game, active);

        if (_container != null) {
            var stageObj:Stage = _container.stage;
            var throttled:Boolean = (stageObj != null && stageObj.frameRate <= 4);
            var worldVisible:Boolean = (game != null && game.world != null && game.world.visible !== false);
            _container.visible = (active && worldVisible && !throttled);
        }
    }

    // -------------------------------------------------------------------------
    // Setup & Lifecycle
    // -------------------------------------------------------------------------

    private static function ensureInitialized(game:*):void {
        if (game == null || game.stage == null || game.world == null || game.world.CHARS == null) {
            return;
        }

        var world:DisplayObjectContainer = game.world as DisplayObjectContainer;
        var charsObj:DisplayObject = game.world.CHARS as DisplayObject;
        if (world == null || charsObj == null || charsObj.parent != world) {
            return;
        }

        if (_container == null) {
            buildContainer();
        }

        // Attach inside game.world directly above CHARS (below FG and all game.ui frames),
        // matching the exact layer priority of AQW's default damage numbers.
        var charsIndex:int = world.getChildIndex(charsObj);
        if (_container.parent != world) {
            world.addChildAt(_container, Math.min(charsIndex + 1, world.numChildren));
        } else {
            var currentIndex:int = world.getChildIndex(_container);
            if (currentIndex < charsIndex) {
                world.setChildIndex(_container, charsIndex);
            } else if (currentIndex > charsIndex + 1) {
                world.setChildIndex(_container, charsIndex + 1);
            }
        }

        var theStage:Stage = game.stage as Stage;
        if (theStage != null) {
            attachStageHooks(theStage);
        }
    }

    private static function buildContainer():void {
        _container = new Sprite();
        _container.name = "CustomDamageNumbersLayer";
        _container.mouseEnabled = false;
        _container.mouseChildren = false;

        _pool = [];
        for (var i:int = 0; i < POOL_SIZE; i++) {
            var p:DamageParticle = new DamageParticle();
            _pool.push(p);
            _container.addChild(p);
        }

        // Timer fires independently of the stage ENTER_FRAME loop so it never interferes with input handlers.
        _lastFrameTime = getTimer();
        if (_ticker == null) {
            _ticker = new Timer(1000 / 60, 0);
            _ticker.addEventListener(TimerEvent.TIMER, onTick);
            _ticker.start();
        }
    }

    private static function attachStageHooks(theStage:Stage):void {
        if (_hookedStage == theStage) {
            return;
        }
        if (_hookedStage != null) {
            try {
                _hookedStage.removeEventListener(Event.ADDED, onDisplayObjectAdded, true);
            } catch (err:Error) {}
        }
        _hookedStage = theStage;
        try {
            theStage.addEventListener(Event.ADDED, onDisplayObjectAdded, true, 100, true);
        } catch (err2:Error) {}
    }

    private static function onDisplayObjectAdded(e:Event):void {
        if (!modernRewardsEnabled || _instance == null || !_instance.enabled) {
            return;
        }
        var obj:* = e.target;
        if (obj == null) {
            return;
        }
        try {
            var qName:String = getQualifiedClassName(obj);
            if (qName == "goldDisplay" || qName == "xpDisplay" || qName == "xpDisplayBonus") {
                obj.visible = false;
                obj.alpha = 0;
            }
        } catch (err:Error) {}
    }

    private static function onTick(e:TimerEvent):void {
        var now:int = getTimer();
        var dt:Number = (now - _lastFrameTime) / 1000.0;
        _lastFrameTime = now;
        if (dt > 0.1) dt = 0.1;

        if (_container == null) {
            return;
        }

        var game:* = Main.instance != null ? Main.instance.getGame() : null;
        ensureSocketHook(game);

        var modEnabled:Boolean = (_instance != null && _instance.enabled);
        var active:Boolean = isInGame(game);
        var anyActive:Boolean = (modEnabled || modernRewardsEnabled);

        if (active && anyActive) {
            ensureInitialized(game);
        }

        syncNativeDisplay(game, active && modEnabled);

        var stageObj:Stage = _container.stage;
        var throttled:Boolean = (stageObj != null && stageObj.frameRate <= 4);
        var worldVisible:Boolean = (game != null && game.world != null && game.world.visible !== false);
        var showLayer:Boolean = (active && anyActive && worldVisible && !throttled);

        _container.visible = showLayer;
        if (!showLayer) {
            return;
        }

        for (var i:int = 0; i < _pool.length; i++) {
            var p:DamageParticle = _pool[i] as DamageParticle;
            if (p != null && p.inUse) {
                p.step(dt);
            }
        }
    }

    private static function isInGame(game:*):Boolean {
        if (game == null || game.stage == null || game.world == null || game.world.myAvatar == null || game.sfc == null) {
            return false;
        }
        try {
            if ("mcLogin" in game && game.mcLogin != null && game.mcLogin.visible) {
                return false;
            }
        } catch (err:Error) {}
        return true;
    }

    private static function ensureSocketHook(game:*):void {
        try {
            var sfc:* = (game != null) ? game.sfc : null;
            if (sfc == null) {
                if (_hookedSfc != null) {
                    try {
                        _hookedSfc.removeEventListener(SFSEvent.onExtensionResponse, handlePacket);
                    } catch (remErr:Error) {}
                }
                _hookedSfc = null;
                return;
            }
            if (_hookedSfc == sfc) {
                return;
            }
            if (_hookedSfc != null) {
                try {
                    _hookedSfc.removeEventListener(SFSEvent.onExtensionResponse, handlePacket);
                } catch (remErr2:Error) {}
            }
            _hookedSfc = sfc;
            sfc.addEventListener(SFSEvent.onExtensionResponse, handlePacket, false, 100, false);
        } catch (err:Error) {}
    }

    private static function syncNativeDisplay(game:*, suppressed:Boolean):void {
        try {
            if (game != null && game.litePreference != null && game.litePreference.data != null) {
                if (suppressed) {
                    if (_savedNativeDmgDisplay === null) {
                        _savedNativeDmgDisplay = Boolean(game.litePreference.data.bDisDmgDisplay);
                    }
                    game.litePreference.data.bDisDmgDisplay = true;
                } else if (_savedNativeDmgDisplay !== null) {
                    game.litePreference.data.bDisDmgDisplay = Boolean(_savedNativeDmgDisplay);
                    _savedNativeDmgDisplay = null;
                }
            }
        } catch (err:Error) {}
    }

    private static function clearActiveParticles():void {
        for (var i:int = 0; i < _pool.length; i++) {
            var p:DamageParticle = _pool[i] as DamageParticle;
            if (p != null) {
                p.release();
            }
        }
    }

    // -------------------------------------------------------------------------
    // Packet Handling (Direct Server Action Stream)
    // -------------------------------------------------------------------------

    private static function handlePacket(e:*):void {
        var game:* = Main.instance != null ? Main.instance.getGame() : null;
        if (!isInGame(game)) {
            return;
        }
        var modEnabled:Boolean = (_instance != null && _instance.enabled);
        if (!modEnabled && !modernRewardsEnabled) {
            return;
        }
        ensureInitialized(game);
        if (_container == null || _container.stage == null) {
            return;
        }
        var stageObj:Stage = _container.stage;
        if (stageObj.frameRate <= 4) {
            return;
        }
        if (game.world.visible === false) {
            return;
        }

        try {
            if (e == null || e.params == null || e.params.dataObj == null) {
                return;
            }
            var d:* = e.params.dataObj;
            var cmd:String = null;
            if (d.cmd != null) {
                cmd = String(d.cmd);
            } else if (d[0] != null) {
                cmd = String(d[0]);
            }
            if (cmd == null) {
                return;
            }

            if (cmd == "addGoldExp") {
                processGoldExpResult(game, d);
                return;
            }

            if (_instance == null || !_instance.enabled) {
                return;
            }

            if (cmd == "sar") {
                processSingleResult(game, d);
            } else if (cmd == "sars") {
                processMultiResult(game, d);
            } else if (cmd == "ct") {
                var i:int;
                if (d.sara != null && d.sara is Array) {
                    var sara:Array = d.sara as Array;
                    for (i = 0; i < sara.length; i++) {
                        if (sara[i] != null) {
                            processSingleResult(game, sara[i]);
                        }
                    }
                }
                if (d.sarsa != null && d.sarsa is Array) {
                    var sarsa:Array = d.sarsa as Array;
                    for (i = 0; i < sarsa.length; i++) {
                        if (sarsa[i] != null) {
                            processMultiResult(game, sarsa[i]);
                        }
                    }
                }
            } else if (cmd == "showAuraResult") {
                processDotResult(game, d);
            }
        } catch (err:Error) {}
    }

    private static function processGoldExpResult(game:*, d:*):void {
        if (d == null || !modernRewardsEnabled || _container == null) {
            return;
        }
        var exp:int = (d.intExp != null) ? int(d.intExp) : 0;
        var bonusExp:int = (d.bonusExp != null) ? int(d.bonusExp) : 0;
        var gold:int = (d.intGold != null) ? int(d.intGold) : 0;
        if (exp <= 0 && gold <= 0) {
            return;
        }

        var typ:String = (d.typ != null) ? String(d.typ) : "";
        var id:* = d.id;

        var tInf:String = "";
        if (typ == "m" && id != null) {
            tInf = "m:" + id;
        } else {
            var myUid:* = (game != null && game.sfc != null) ? game.sfc.myUserId : null;
            if (myUid != null) {
                tInf = "p:" + myUid;
            }
        }

        var targetMC:* = (tInf != "") ? getTargetMC(game, tInf) : null;
        if (targetMC == null || targetMC.stage == null) {
            try {
                if (game != null && game.world != null && game.world.myAvatar != null) {
                    targetMC = game.world.myAvatar.pMC;
                }
            } catch (err:Error) {}
        }
        if (targetMC == null || targetMC.stage == null) {
            return;
        }

        var screenCenter:Point = computeTargetScreenCenter(targetMC, -25, 0.22, 25.0);
        if (screenCenter == null) {
            return;
        }

        var both:Boolean = (exp > 0 && gold > 0);
        var p:DamageParticle;
        var yOffset:Number;

        if (exp > 0) {
            var expText:String = "+" + formatNumber(exp) + " XP";
            if (bonusExp > 0) {
                expText += " (+" + formatNumber(bonusExp) + ")";
            }
            p = acquireParticle();
            if (p != null) {
                yOffset = both ? -14.0 : 0.0;
                p.spawnReward(expText, COLOR_XP, 15, screenCenter.x, screenCenter.y + yOffset);
            }
        }

        if (gold > 0) {
            var goldText:String = "+" + formatNumber(gold) + "g";
            p = acquireParticle();
            if (p != null) {
                yOffset = both ? 6.0 : 0.0;
                p.spawnReward(goldText, COLOR_GOLD, 15, screenCenter.x, screenCenter.y + yOffset);
            }
        }
    }

    private static function processSingleResult(game:*, r:*):void {
        if (r == null) return;
        var act:* = ("actionResult" in r && r.actionResult != null) ? r.actionResult : r;
        if (act == null) return;
        if ("iRes" in r && r.iRes != null && int(r.iRes) != 1) return;
        if ("iRes" in act && act.iRes != null && int(act.iRes) != 1) return;

        var cInf:String = (act.cInf != null) ? String(act.cInf) : "";
        var tInf:String = (act.tInf != null) ? String(act.tInf) : "";
        var hp:int = (act.hp != null) ? int(act.hp) : 0;
        var type:String = (act.type != null) ? String(act.type).toLowerCase() : "hit";
        var isDot:Boolean = (act.typ != null && String(act.typ) == "d");
        var isHot:Boolean = (act.typ != null && String(act.typ) == "h");

        spawnCombatNumber(game, cInf, tInf, hp, type, isDot, isHot);
    }

    private static function processMultiResult(game:*, m:*):void {
        if (m == null) return;
        var act:* = ("actionResult" in m && m.actionResult != null) ? m.actionResult : m;
        if (act == null) return;
        if ("iRes" in m && m.iRes != null && int(m.iRes) != 1) return;
        if ("iRes" in act && act.iRes != null && int(act.iRes) != 1) return;

        var cInf:String = (act.cInf != null) ? String(act.cInf) : "";
        if (act.a != null && act.a is Array) {
            var hits:Array = act.a as Array;
            for (var i:int = 0; i < hits.length; i++) {
                var hit:* = hits[i];
                if (hit == null) continue;
                var tInf:String = (hit.tInf != null) ? String(hit.tInf) : "";
                var hp:int = (hit.hp != null) ? int(hit.hp) : 0;
                var type:String = (hit.type != null) ? String(hit.type).toLowerCase() : "hit";
                var isDot:Boolean = (hit.typ != null && String(hit.typ) == "d");
                var isHot:Boolean = (hit.typ != null && String(hit.typ) == "h");
                spawnCombatNumber(game, cInf, tInf, hp, type, isDot, isHot);
            }
        }
    }

    private static function processDotResult(game:*, d:*):void {
        if (d == null) return;
        var cInf:String = (d.cInf != null) ? String(d.cInf) : "";
        var tInf:String = (d.tInf != null) ? String(d.tInf) : "";
        var hp:int = (d.hp != null) ? int(d.hp) : 0;
        var isHot:Boolean = (d.typ != null && String(d.typ) == "h") || (hp < 0);
        var isDot:Boolean = !isHot;
        spawnCombatNumber(game, cInf, tInf, hp, isHot ? "hot" : "dot", isDot, isHot);
    }

    // -------------------------------------------------------------------------
    // Combat Number Generation & Center Positioning
    // -------------------------------------------------------------------------

    private static function hasDecayAura(game:*, tInf:String):Boolean {
        try {
            var world:* = (game != null) ? game.world : null;
            if (world == null) return false;

            var leaf:* = null;
            var idVal:Number = parseEntityId(tInf);
            if (isNaN(idVal)) return false;

            if (tInf.indexOf("m:") == 0) {
                var monId:int = int(idVal);
                if (world.monTree != null) {
                    leaf = world.monTree[monId];
                }
            } else if (tInf.indexOf("p:") == 0) {
                var uid:int = int(idVal);
                var myUid:* = (game.sfc != null) ? game.sfc.myUserId : null;
                if (myUid != null && uid == int(myUid) && world.uoTree != null && game.sfc.myUserName != null) {
                    leaf = world.uoTree[String(game.sfc.myUserName).toLowerCase()];
                }
                if (leaf == null) {
                    var avt:* = findAvatarByUserId(world, uid);
                    if (avt != null) {
                        if (avt.dataLeaf != null) {
                            leaf = avt.dataLeaf;
                        } else if (avt.objData != null && avt.objData.strUsername != null && world.uoTree != null) {
                            leaf = world.uoTree[String(avt.objData.strUsername).toLowerCase()];
                        }
                    }
                }
            }

            if (leaf == null) return false;

            if (leaf.auras != null && leaf.auras is Array) {
                var arr:Array = leaf.auras as Array;
                for (var i:int = 0; i < arr.length; i++) {
                    var a:* = arr[i];
                    if (a != null && a.e != 1 && a.nam != null) {
                        if (isDecayAuraName(String(a.nam))) return true;
                    }
                }
            }
            if (leaf.hudAuras != null) {
                for (var auraName:String in leaf.hudAuras) {
                    if (leaf.hudAuras[auraName] != null && isDecayAuraName(auraName)) return true;
                }
            }
        } catch (err:Error) {}
        return false;
    }

    private static function isDecayAuraName(name:String):Boolean {
        var nam:String = name.toLowerCase();
        return (nam.indexOf("decay") >= 0 || nam.indexOf("revert") >= 0 || nam.indexOf("reverse") >= 0 || nam.indexOf("invert") >= 0);
    }

    private static function spawnCombatNumber(game:*, cInf:String, tInf:String, hp:int, type:String, isDot:Boolean, isHot:Boolean = false):void {
        if (tInf == "" || _container == null) return;

        if (isDot && !showDots) return;

        var myUid:* = (game != null && game.sfc != null) ? game.sfc.myUserId : null;
        var myTag:String = (myUid != null) ? ("p:" + myUid) : "";
        var isTargetingMe:Boolean = (tInf != "" && tInf == myTag);

        var isCurrentTarget:Boolean = false;
        try {
            var targetEnt:* = (game != null && game.world != null && game.world.myAvatar != null) ? game.world.myAvatar.target : null;
            if (targetEnt != null && targetEnt.objData != null) {
                if (tInf.indexOf("m:") == 0 && targetEnt.objData.MonMapID != null) {
                    isCurrentTarget = (tInf == ("m:" + targetEnt.objData.MonMapID));
                } else if (tInf.indexOf("p:") == 0 && targetEnt.objData.EntID != null) {
                    isCurrentTarget = (tInf == ("p:" + targetEnt.objData.EntID));
                }
            }
        } catch (err:Error) {}

        var isMyAttack:Boolean = (cInf != "" && cInf == myTag) || (cInf == "" && isCurrentTarget && !isTargetingMe);

        var passesFilter:Boolean = false;
        switch (filterMode) {
            case 0:
                passesFilter = isMyAttack || isCurrentTarget || (isTargetingMe && showIncoming);
                break;
            case 1:
                passesFilter = isMyAttack || (isTargetingMe && showIncoming);
                break;
            case 2:
                passesFilter = isCurrentTarget;
                break;
            case 3:
                passesFilter = true;
                break;
        }
        if (!passesFilter) return;

        var isHealAction:Boolean = (hp < 0 || isHot || type == "heal");
        var isDecayed:Boolean = isHealAction && hasDecayAura(game, tInf);
        var isInvertedHeal:Boolean = isHealAction && (hp > 0 || isDecayed);
        var isNormalHeal:Boolean = isHealAction && !isInvertedHeal;

        if (isNormalHeal && !showHeals) return;

        var targetMC:* = getTargetMC(game, tInf);
        if (targetMC == null || targetMC.stage == null) return;

        // Place numbers centered at the middle of the character / monster body
        var screenCenter:Point = computeTargetScreenCenter(targetMC, -50, 0.50, 0.0);
        if (screenCenter == null) return;

        var spawnX:Number = screenCenter.x;
        var spawnY:Number = screenCenter.y;
        var sideOffset:Number;

        if (isDot || isHot) {
            sideOffset = (Math.random() > 0.5 ? 40.0 : -40.0);
            spawnX += sideOffset + (Math.random() - 0.5) * 12.0;
            spawnY += -12.0 + (Math.random() - 0.5) * 16.0;
        } else if (isNormalHeal) {
            sideOffset = (Math.random() > 0.5 ? 28.0 : -28.0);
            spawnX += sideOffset + (Math.random() - 0.5) * 10.0;
            spawnY += -20.0 + (Math.random() - 0.5) * 12.0;
        } else {
            spawnX += (Math.random() - 0.5) * 20.0;
            spawnY += (Math.random() - 0.5) * 14.0;
        }

        var isIncoming:Boolean = (isTargetingMe && !isMyAttack);
        var isCrit:Boolean = (type == "crit");
        var textStr:String = "";
        var textColor:uint = COLOR_HIT;
        var fontSize:int = 14;

        if (isInvertedHeal) {
            var dmgAmount:int = (hp > 0) ? hp : (-hp);
            textStr = "-" + formatNumber(dmgAmount);
            textColor = isIncoming ? COLOR_INCOMING_CRIT : COLOR_AVOID_OUT;
            fontSize = isCrit ? 22 : 14;
        } else if (isNormalHeal) {
            var healAmount:int = (hp < 0) ? (-hp) : hp;
            textStr = "+" + formatNumber(healAmount);
            textColor = COLOR_HEAL;
            fontSize = isCrit ? 22 : 14;
        } else if (type == "miss" || type == "dodge" || type == "parry" || type == "block") {
            textStr = type.charAt(0).toUpperCase() + type.substr(1) + "!";
            textColor = COLOR_AVOID;
            fontSize = 14;
        } else if (type == "immune") {
            textStr = "Immune!";
            textColor = COLOR_MUTED;
            fontSize = 14;
        } else if (type == "resist") {
            textStr = "Resist!";
            textColor = COLOR_MUTED;
            fontSize = 14;
        } else if (isIncoming) {
            if (isCrit) {
                textStr = "-" + formatNumber(hp);
                textColor = COLOR_INCOMING_CRIT;
                fontSize = 24;
            } else if (isDot || type == "dot") {
                textStr = "-" + formatNumber(hp);
                textColor = COLOR_INCOMING_DOT;
                fontSize = 13;
            } else {
                textStr = "-" + formatNumber(hp);
                textColor = COLOR_INCOMING_HIT;
                fontSize = 15;
            }
        } else if (type == "crit") {
            textStr = formatNumber(hp);
            textColor = COLOR_CRIT;
            fontSize = 24;
        } else if (isDot || type == "dot") {
            textStr = "-" + formatNumber(hp);
            textColor = COLOR_DOT;
            fontSize = 13;
        } else if (type == "hit") {
            textStr = formatNumber(hp);
            textColor = (hp == 0) ? COLOR_MUTED : COLOR_HIT;
            fontSize = 14;
        } else {
            textStr = formatNumber(hp);
            textColor = COLOR_HIT;
            fontSize = 14;
        }

        var p:DamageParticle = acquireParticle();
        if (p != null) {
            p.spawn(textStr, textColor, fontSize, isCrit, spawnX, spawnY, (isDot || isHot || type == "dot"));
        }
    }

    private static function acquireParticle():DamageParticle {
        var oldestIdx:int = 0;
        var maxLifeFound:Number = -1.0;
        for (var i:int = 0; i < _pool.length; i++) {
            var p:DamageParticle = _pool[i] as DamageParticle;
            if (!p.inUse) {
                if (_container != null && _container.numChildren > 1) {
                    _container.setChildIndex(p, _container.numChildren - 1);
                }
                return p;
            }
            if (p.life > maxLifeFound) {
                maxLifeFound = p.life;
                oldestIdx = i;
            }
        }
        var recycled:DamageParticle = _pool[oldestIdx] as DamageParticle;
        if (_container != null && _container.numChildren > 1) {
            _container.setChildIndex(recycled, _container.numChildren - 1);
        }
        return recycled;
    }

    private static function computeTargetScreenCenter(targetMC:*, fallbackY:Number, bodyFraction:Number, clampMinHeight:Number):Point {
        if (_container == null || targetMC == null) return null;

        var localCenterX:Number = 0;
        var localCenterY:Number = fallbackY;
        try {
            var charMC:* = getCharClip(targetMC);
            if (charMC != null && "x" in charMC) {
                localCenterX = Number(charMC.x);
            } else if ("mcChar" in targetMC && targetMC.mcChar != null) {
                localCenterX = Number(targetMC.mcChar.x);
            }

            if ("pname" in targetMC && targetMC.pname != null && targetMC.pname.y < -20) {
                localCenterY = Number(targetMC.pname.y) * bodyFraction;
            } else if (charMC != null && charMC.height > 20) {
                var scaled:Number = Number(charMC.height) * bodyFraction;
                localCenterY = -(clampMinHeight > 0 ? Math.max(clampMinHeight, scaled) : scaled);
            }
        } catch (coordErr:Error) {}

        var globalCenter:Point = null;
        try {
            globalCenter = targetMC.localToGlobal(new Point(localCenterX, localCenterY));
        } catch (ptErr:Error) {
            return null;
        }
        if (globalCenter == null) return null;

        return _container.globalToLocal(globalCenter);
    }

    private static function parseEntityId(tInf:String):Number {
        if (tInf == null || tInf.length < 3) return NaN;
        return parseInt(trimString(tInf.substr(2)), 10);
    }

    private static function getTargetMC(game:*, tInf:String):* {
        try {
            var world:* = (game != null) ? game.world : null;
            if (world == null) return null;

            var idVal:Number = parseEntityId(tInf);
            if (isNaN(idVal)) return null;

            if (tInf.indexOf("m:") == 0) {
                var monId:int = int(idVal);
                var mon:* = null;
                if ("getMonster" in world && world.getMonster is Function) {
                    mon = world.getMonster(monId);
                    if (mon != null && mon.pMC != null) return mon.pMC;
                }
                if (world.monsters != null) {
                    mon = world.monsters[monId];
                    if (mon == null) mon = world.monsters[String(monId)];
                    if (mon != null && mon.pMC != null) return mon.pMC;
                }
            } else if (tInf.indexOf("p:") == 0) {
                var uid:int = int(idVal);
                var myUid:* = (game != null && game.sfc != null) ? game.sfc.myUserId : null;
                if (myUid != null && uid == int(myUid)) {
                    if (world.myAvatar != null && world.myAvatar.pMC != null) return world.myAvatar.pMC;
                }
                if (world.myAvatar != null && world.myAvatar.dataLeaf != null && world.myAvatar.dataLeaf.EntID == uid) {
                    return world.myAvatar.pMC;
                }
                var avt:* = findAvatarByUserId(world, uid);
                if (avt != null && avt.pMC != null) return avt.pMC;
            }
        } catch (err:Error) {}
        return null;
    }

    private static function findAvatarByUserId(world:*, uid:int):* {
        if ("getAvatarByUserID" in world && world.getAvatarByUserID is Function) {
            var avt:* = world.getAvatarByUserID(uid);
            if (avt != null) return avt;
        }
        if (world.avatars != null) {
            var direct:* = world.avatars[uid];
            if (direct != null) return direct;
            direct = world.avatars[String(uid)];
            if (direct != null) return direct;

            for each (var candidate:* in world.avatars) {
                if (candidate != null && candidate.uid == uid) return candidate;
                if (candidate != null && candidate.dataLeaf != null && candidate.dataLeaf.EntID == uid) return candidate;
            }
        }
        return null;
    }

    private static function getCharClip(targetMC:*):* {
        if (targetMC == null) return null;
        try {
            if ("mcChar" in targetMC && targetMC.mcChar != null) {
                return targetMC.mcChar;
            }
            if (targetMC.numChildren > 1) {
                var c:* = targetMC.getChildAt(1);
                if (c != null) return c;
            }
            if (targetMC.numChildren > 0) {
                return targetMC.getChildAt(0);
            }
        } catch (err:Error) {}
        return targetMC;
    }

    private static function formatNumber(val:int):String {
        var isNeg:Boolean = (val < 0);
        var absVal:int = isNeg ? -val : val;

        var formatted:String = "";
        if (compactFormat) {
            if (absVal >= 1000000) {
                var m:Number = Math.round(absVal / 100000) / 10.0;
                formatted = m + "M";
            } else if (absVal >= 10000) {
                var k:Number = Math.round(absVal / 100) / 10.0;
                formatted = k + "K";
            }
        }

        if (formatted == "") {
            var s:String = String(absVal);
            var len:int = s.length;
            if (len <= 3) {
                formatted = s;
            } else {
                var res:String = "";
                var count:int = 0;
                for (var i:int = len - 1; i >= 0; i--) {
                    res = s.charAt(i) + res;
                    count++;
                    if (count % 3 == 0 && i > 0) {
                        res = "," + res;
                    }
                }
                formatted = res;
            }
        }

        return isNeg ? ("-" + formatted) : formatted;
    }

    private static function trimString(s:String):String {
        if (s == null) return "";
        return s.replace(/^\s+|\s+$/g, "");
    }
}
}

import flash.display.Bitmap;
import flash.display.BitmapData;
import flash.display.PixelSnapping;
import flash.display.Sprite;
import flash.display.StageQuality;
import flash.filters.BitmapFilterQuality;
import flash.filters.GlowFilter;
import flash.geom.Matrix;
import flash.text.AntiAliasType;
import flash.text.TextField;
import flash.text.TextFieldAutoSize;
import flash.text.TextFormat;
import flash.text.TextFormatAlign;

/**
 * Lightweight, pooled particle for floating combat text numbers.
 * Pre-renders each spawned number + glow off-screen via drawWithQuality(HIGH) into
 * a bucket-reused BitmapData so typography and outlines remain crisp even when
 * the host Flash stage is running at LOW quality.
 */
class DamageParticle extends Sprite {
    private static const FONT_FAMILY:String = "_sans";
    private static const GLOW_PAD:int = 10;

    // Static cached filters (quality HIGH only runs once per spawn offscreen)
    private static var GLOW_NORMAL:GlowFilter = new GlowFilter(0x000000, 0.95, 3, 3, 4, BitmapFilterQuality.HIGH);
    private static var GLOW_CRIT:GlowFilter   = new GlowFilter(0x000000, 1.0, 5, 5, 6, BitmapFilterQuality.HIGH);
    private static var GLOW_REWARD:GlowFilter = new GlowFilter(0x000000, 1.0, 4, 4, 5, BitmapFilterQuality.HIGH);

    // Shared scratch objects used purely for offscreen baking
    private static var _scratchBox:Sprite = null;
    private static var _scratchTf:TextField = null;
    private static var _scratchMat:Matrix = new Matrix();

    private var _bmp:Bitmap;
    private var _bd:BitmapData = null;
    private var _posX:Number = 0;
    private var _posY:Number = 0;
    private var _snapPixels:Boolean = false;

    public var vx:Number = 0;
    public var vy:Number = 0;
    public var life:Number = 0;
    public var maxLife:Number = 0.85;
    public var isCrit:Boolean = false;
    public var isDot:Boolean = false;
    public var isReward:Boolean = false;
    public var inUse:Boolean = false;
    public var baseScale:Number = 1.0;

    public function DamageParticle() {
        super();
        mouseEnabled = false;
        mouseChildren = false;
        visible = false;

        _bmp = new Bitmap(null, PixelSnapping.AUTO, true);
        addChild(_bmp);
    }

    private static function ensureScratch():void {
        if (_scratchBox != null) return;
        _scratchBox = new Sprite();
        _scratchTf = new TextField();
        _scratchTf.autoSize = TextFieldAutoSize.LEFT;
        _scratchTf.antiAliasType = AntiAliasType.ADVANCED;
        _scratchTf.selectable = false;
        _scratchTf.mouseEnabled = false;
        _scratchBox.addChild(_scratchTf);
    }

    public function spawn(text:String, color:uint, fontSize:int, isCritical:Boolean, startX:Number, startY:Number, isDamageOverTime:Boolean):void {
        isCrit = isCritical;
        isDot = isDamageOverTime;
        isReward = false;
        inUse = true;
        life = 0;
        maxLife = isCrit ? 1.05 : (isDot ? 0.95 : 0.85);

        // Bake Crits directly at resting pixel size (fontSize * 1.25) so 1:1 pixels stay crisp even on LOW quality
        var bakedFontSize:int = isCrit ? Math.round(fontSize * 1.25) : fontSize;
        bakeAndShow(text, color, bakedFontSize, isCrit ? GLOW_CRIT : GLOW_NORMAL, startX, startY);

        if (isCrit) {
            baseScale = _snapPixels ? 1.0 : 1.4;
            vx = (Math.random() - 0.5) * 0.6;
            vy = -4.8;
        } else if (isDot) {
            baseScale = 1.0;
            vx = (Math.random() - 0.5) * 0.3;
            vy = -2.6;
        } else {
            baseScale = 1.0;
            vx = (Math.random() - 0.5) * 0.8;
            vy = -3.2;
        }
        scaleX = baseScale;
        scaleY = baseScale;
        visible = true;
    }

    public function spawnReward(text:String, color:uint, fontSize:int, startX:Number, startY:Number):void {
        isCrit = false;
        isDot = false;
        isReward = true;
        inUse = true;
        life = 0;
        maxLife = 1.25;

        bakeAndShow(text, color, fontSize + 1, GLOW_REWARD, startX, startY);

        baseScale = _snapPixels ? 1.0 : 1.15;
        scaleX = baseScale;
        scaleY = baseScale;
        vx = (Math.random() - 0.5) * 0.2;
        vy = -2.0;
        visible = true;
    }

    private function bakeAndShow(text:String, color:uint, fontSize:int, glow:GlowFilter, startX:Number, startY:Number):void {
        ensureScratch();
        _snapPixels = (stage != null && stage.quality != null && String(stage.quality).toLowerCase() == "low");

        var fmt:TextFormat = new TextFormat(FONT_FAMILY, fontSize, color, true);
        fmt.align = TextFormatAlign.CENTER;
        _scratchTf.defaultTextFormat = fmt;
        _scratchTf.text = text;
        _scratchTf.filters = [glow];

        var neededW:int = Math.ceil(_scratchTf.width) + GLOW_PAD * 2;
        var neededH:int = Math.ceil(_scratchTf.height) + GLOW_PAD * 2;
        var texW:int = ((neededW + 31) >> 5) << 5;
        var texH:int = ((neededH + 15) >> 4) << 4;
        if (texW < 32) texW = 32;
        if (texH < 32) texH = 32;

        if (_bd == null || _bd.width < neededW || _bd.height < neededH) {
            if (_bd != null) {
                _bd.dispose();
            }
            _bd = new BitmapData(texW, texH, true, 0x00000000);
        } else {
            _bd.fillRect(_bd.rect, 0x00000000);
        }

        _scratchMat.identity();
        _scratchMat.tx = Math.round((_bd.width - _scratchTf.width) * 0.5);
        _scratchMat.ty = Math.round((_bd.height - _scratchTf.height) * 0.5);

        try {
            _bd.drawWithQuality(_scratchBox, _scratchMat, null, null, null, true, StageQuality.HIGH);
        } catch (drawErr:Error) {
            _bd.draw(_scratchBox, _scratchMat, null, null, null, true);
        }

        _bmp.bitmapData = _bd;
        _bmp.pixelSnapping = _snapPixels ? PixelSnapping.AUTO : PixelSnapping.NEVER;
        _bmp.smoothing = true;
        _bmp.x = -(_bd.width >> 1);
        _bmp.y = -(_bd.height >> 1);

        _posX = startX;
        _posY = startY;
        x = _snapPixels ? Math.round(_posX) : _posX;
        y = _snapPixels ? Math.round(_posY) : _posY;
        alpha = 1.0;
    }

    public function step(dt:Number):Boolean {
        life += dt;
        if (life >= maxLife) {
            inUse = false;
            visible = false;
            return false;
        }

        var frameFactor:Number = dt * 60.0;
        _posX += vx * frameFactor;
        _posY += vy * frameFactor;
        x = _snapPixels ? Math.round(_posX) : _posX;
        y = _snapPixels ? Math.round(_posY) : _posY;

        if (isCrit) {
            vy *= Math.pow(0.88, frameFactor);
            if (!_snapPixels && scaleX > 1.0) {
                scaleX -= dt * 2.0;
                if (scaleX < 1.0) scaleX = 1.0;
                scaleY = scaleX;
            }
        } else if (isReward) {
            vy *= Math.pow(0.93, frameFactor);
            if (!_snapPixels && scaleX > 1.0) {
                scaleX -= dt * 0.8;
                if (scaleX < 1.0) scaleX = 1.0;
                scaleY = scaleX;
            }
        } else if (isDot) {
            vy *= Math.pow(0.97, frameFactor);
        } else {
            vy *= Math.pow(0.90, frameFactor);
        }

        var fadeStart:Number = maxLife * 0.65;
        if (life > fadeStart) {
            alpha = (maxLife - life) / (maxLife - fadeStart);
        }

        return true;
    }

    public function release():void {
        inUse = false;
        visible = false;
        _bmp.bitmapData = null;
        if (_bd != null) {
            _bd.dispose();
            _bd = null;
        }
    }
}
