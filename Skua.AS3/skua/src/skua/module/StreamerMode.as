package skua.module {
import flash.display.Stage;
import flash.events.Event;
import skua.Main;

public class StreamerMode extends Module {
    private static var _instance:StreamerMode = null;
    private static var _customName:String = "";
    private static var _customGuild:String = "";
    private static var _hookedStage:Stage = null;

    public function StreamerMode() {
        super("StreamerMode");
        _instance = this;
        try {
            if (Main.instance != null && Main.instance.external != null) {
                Main.instance.external.addCallback("setStreamerMode", setStreamerMode);
                Main.instance.external.addCallback("setStreamerCustomName", setCustomName);
                Main.instance.external.addCallback("setStreamerCustomGuild", setCustomGuild);
            }
        } catch (err:Error) {}
    }

    private static function parseBool(v:*):Boolean {
        return (v === true || v === 1 || String(v).toLowerCase() == "true");
    }

    public static function setStreamerMode(enable:*):void {
        var next:Boolean = parseBool(enable);
        if (_instance == null) return;
        var changed:Boolean = (_instance.enabled != next);
        _instance.enabled = next;
        if (changed) {
            _instance.onToggle(Main.instance != null ? Main.instance.getGame() : null);
        } else if (next) {
            _instance.applyStreamer(Main.instance != null ? Main.instance.getGame() : null);
        }
    }

    public static function setCustomName(name:*):void {
        _customName = (name != null) ? String(name).replace(/^\s+|\s+$/g, "") : "";
        if (_instance != null && _instance.enabled) {
            _instance.applyStreamer(Main.instance != null ? Main.instance.getGame() : null);
        }
    }

    public static function setCustomGuild(guild:*):void {
        _customGuild = (guild != null) ? String(guild).replace(/^\s+|\s+$/g, "") : "";
        if (_instance != null && _instance.enabled) {
            _instance.applyStreamer(Main.instance != null ? Main.instance.getGame() : null);
        }
    }

    private static function getClassDisplayName(avt:*):String {
        if (avt != null && avt.objData != null && avt.objData.strClassName != null) {
            var cls:String = String(avt.objData.strClassName);
            if (cls.length > 0 && cls != "null") {
                return cls;
            }
        }
        return "Adventurer";
    }

    override public function onToggle(game:*):void {
        if (!game || !game.world) return;
        if (enabled) {
            ensureStageHook(game);
            applyStreamer(game);
        } else {
            restoreVisuals(game);
        }
    }

    override public function onFrame(game:*):void {
        if (!enabled) return;
        ensureStageHook(game);
        applyStreamer(game);
    }

    private static function ensureStageHook(game:*):void {
        var st:Stage = null;
        try {
            if (game != null && game.stage != null) {
                st = game.stage as Stage;
            }
        } catch (err:Error) {}
        if (st == null || _hookedStage == st) {
            return;
        }
        if (_hookedStage != null) {
            try { _hookedStage.removeEventListener(Event.ENTER_FRAME, onStageFrame); } catch (e1:Error) {}
            try { _hookedStage.removeEventListener(Event.EXIT_FRAME, onStageFrame); } catch (e2:Error) {}
        }
        _hookedStage = st;
        try { st.addEventListener(Event.ENTER_FRAME, onStageFrame, false, -1000, true); } catch (e3:Error) {}
        try { st.addEventListener(Event.EXIT_FRAME, onStageFrame, false, -1000, true); } catch (e4:Error) {}
    }

    private static function onStageFrame(e:Event):void {
        if (_instance == null || !_instance.enabled) return;
        var game:* = (Main.instance != null) ? Main.instance.getGame() : null;
        _instance.applyStreamer(game);
    }

    private function restoreVisuals(game:*):void {
        try {
            var avt:* = game.world.myAvatar;
            if (avt != null && avt.objData != null) {
                var realName:String = avt.objData.strUsername != null ? String(avt.objData.strUsername) : "";
                var realGuild:String = avt.objData.strGuildName != null ? String(avt.objData.strGuildName) : "";
                if (avt.pMC != null && avt.pMC.pname != null) {
                    if (avt.pMC.pname.ti != null && realName.length > 0)
                        avt.pMC.pname.ti.text = realName;
                    if (avt.pMC.pname.tg != null)
                        avt.pMC.pname.tg.text = (realGuild.length > 0 && realGuild != "null") ? ("< " + realGuild + " >") : "";
                }
                if (game.ui != null && game.ui.mcPortrait != null && game.ui.mcPortrait.strName != null && realName.length > 0) {
                    game.ui.mcPortrait.strName.text = realName;
                }
            }

            if (game.world.avatars != null) {
                for each (var other:* in game.world.avatars) {
                    if (other == null || other == avt || other.objData == null) continue;
                    if (other.pMC != null && other.pMC.pname != null) {
                        if (other.pMC.pname.ti != null && other.objData.strUsername != null)
                            other.pMC.pname.ti.text = String(other.objData.strUsername);
                        if (other.pMC.pname.tg != null) {
                            var og:String = other.objData.strGuildName != null ? String(other.objData.strGuildName) : "";
                            other.pMC.pname.tg.text = (og.length > 0 && og != "null") ? ("< " + og + " >") : "";
                        }
                    }
                }
            }

            if (game.ui != null && game.ui.mcInterface != null) {
                if (game.ui.mcInterface.t1 != null) game.ui.mcInterface.t1.visible = true;
                if (game.ui.mcInterface.te != null) game.ui.mcInterface.te.visible = true;
                if (game.ui.mcInterface.teGold != null) game.ui.mcInterface.teGold.visible = true;
                if (game.ui.mcInterface.teCoins != null) game.ui.mcInterface.teCoins.visible = true;
                if (game.ui.mcInterface.strGold != null) game.ui.mcInterface.strGold.visible = true;
                if (game.ui.mcInterface.strCoins != null) game.ui.mcInterface.strCoins.visible = true;
                if (game.ui.mcInterface.txtGold != null) game.ui.mcInterface.txtGold.visible = true;
                if (game.ui.mcInterface.txtCoins != null) game.ui.mcInterface.txtCoins.visible = true;
            }
            if (game.updateAreaName != null) {
                try { game.updateAreaName(); } catch (e:*) {}
            }
            if (game.world.setUserData != null) {
                try { game.world.setUserData(); } catch (e:*) {}
            }
        } catch (err:Error) {}
    }

    private function applyStreamer(game:*):void {
        if (!game || !game.world) return;

        var avt:* = game.world.myAvatar;
        var nameToDisplay:String = (_customName.length > 0) ? _customName : getClassDisplayName(avt);
        var guildToDisplay:String = (_customGuild.length > 0) ? _customGuild : "";

        if (avt != null && avt.pMC != null && avt.pMC.pname != null) {
            if (avt.pMC.pname.ti != null && avt.pMC.pname.ti.text != nameToDisplay)
                avt.pMC.pname.ti.text = nameToDisplay;
            if (avt.pMC.pname.tg != null && avt.pMC.pname.tg.text != guildToDisplay)
                avt.pMC.pname.tg.text = guildToDisplay;
        }

        if (game.world.avatars != null) {
            for each (var other:* in game.world.avatars) {
                if (other == null || other == avt) continue;
                if (other.pMC != null && other.pMC.pname != null) {
                    var otherName:String = getClassDisplayName(other);
                    if (other.pMC.pname.ti != null && other.pMC.pname.ti.text != otherName)
                        other.pMC.pname.ti.text = otherName;
                    if (other.pMC.pname.tg != null && other.pMC.pname.tg.text != "")
                        other.pMC.pname.tg.text = "";
                }
            }
        }

        if (game.ui != null) {
            if (game.ui.mcPortrait != null && game.ui.mcPortrait.strName != null && game.ui.mcPortrait.strName.text != nameToDisplay)
                game.ui.mcPortrait.strName.text = nameToDisplay;

            if (game.ui.mcPortraitTarget != null && game.ui.mcPortraitTarget.strName != null && avt != null && avt.target != null) {
                var target:* = avt.target;
                if (target == avt) {
                    if (game.ui.mcPortraitTarget.strName.text != nameToDisplay)
                        game.ui.mcPortraitTarget.strName.text = nameToDisplay;
                } else if (target.pMC != null && target.objData != null && target.objData.strClassName != null) {
                    var targetClassName:String = getClassDisplayName(target);
                    if (game.ui.mcPortraitTarget.strName.text != targetClassName)
                        game.ui.mcPortraitTarget.strName.text = targetClassName;
                }
            }

            if (game.ui.mcInterface != null) {
                if (game.ui.mcInterface.teGold != null) game.ui.mcInterface.teGold.visible = false;
                if (game.ui.mcInterface.teCoins != null) game.ui.mcInterface.teCoins.visible = false;
                if (game.ui.mcInterface.strGold != null) game.ui.mcInterface.strGold.visible = false;
                if (game.ui.mcInterface.strCoins != null) game.ui.mcInterface.strCoins.visible = false;
                if (game.ui.mcInterface.txtGold != null) game.ui.mcInterface.txtGold.visible = false;
                if (game.ui.mcInterface.txtCoins != null) game.ui.mcInterface.txtCoins.visible = false;
                if (game.ui.mcInterface.t1 != null) game.ui.mcInterface.t1.visible = false;
                if (game.ui.mcInterface.te != null) game.ui.mcInterface.te.visible = false;

                if (game.ui.mcInterface.areaList != null && game.ui.mcInterface.areaList.title != null && game.ui.mcInterface.areaList.title.t1 != null) {
                    var rawMap:String = game.world.strMapName != null ? String(game.world.strMapName) : "";
                    if (rawMap.length > 0 && rawMap != "null") {
                        var dashIdx:int = rawMap.indexOf("-");
                        var mapOnly:String = (dashIdx > 0) ? rawMap.substr(0, dashIdx) : rawMap;
                        var cleanMap:String = mapOnly.charAt(0).toUpperCase() + mapOnly.substr(1).toLowerCase();
                        if (game.ui.mcInterface.areaList.title.t1.text != cleanMap)
                            game.ui.mcInterface.areaList.title.t1.text = cleanMap;
                    }
                }
            }
        }
    }
}
}
